using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The two statements an invoice needs against a real PostgreSQL: the worksheet read the document is
/// built from, and the issue that must leave exactly one row per period. Both run through the store the
/// application service uses, inside the host's own tenant scope, so the ordering, the adjustment rows
/// and the tenant boundary are exercised on the path production takes.
/// </summary>
/// <remarks>
/// The seeds run as the owner over parameterised statements, which is how a row of a second org can
/// exist while the read under test must never return it.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageBillingStatementStoreTests
{
    private const string Period = "2026-09";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly PostgresTenancyFixture _fixture;

    public UsageBillingStatementStoreTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ the worksheet read

    [Fact]
    public async Task WorksheetLines_OrderIsMetricThenNullProjectThenInstant_AndAdjustmentsAreIncluded()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();

        // The expected sequence is metric, then the organisation-level row of that metric, then the
        // project rows by occurrence. The instants are pinned so a project row that occurred first still
        // sorts after the organisation-level one.
        var resultsOrg = Guid.CreateVersion7();
        var resultsProject = Guid.CreateVersion7();
        var resultsAdjustment = Guid.CreateVersion7();
        var supportOrg = Guid.CreateVersion7();
        var supportProject = Guid.CreateVersion7();
        var otherPeriodRow = Guid.CreateVersion7();
        var foreignOrgRow = Guid.CreateVersion7();

        await world.SeedLedgerAsync(
            new LedgerSeed(resultsOrg, null, "results", 1m, Period, "sheet:results:org", Start.AddHours(9),
                OccurredAt: Start.AddDays(10), Amount: 4m, Currency: "USD"),
            new LedgerSeed(resultsProject, world.Project, "results", 3m, Period, "sheet:results:project", Start.AddHours(9),
                OccurredAt: Start.AddDays(1), Amount: 12m, Currency: "USD"),
            new LedgerSeed(resultsAdjustment, world.Project, "results", -1m, Period, "sheet:results:adjustment", Start.AddHours(9),
                OccurredAt: Start.AddDays(2), AdjustmentOf: resultsProject, Reason: "over-counted retry"),
            new LedgerSeed(supportOrg, null, "support_hours", 2m, Period, "sheet:support:org", Start.AddHours(9),
                OccurredAt: Start.AddDays(4), Amount: 30m, Currency: "USD"),
            new LedgerSeed(supportProject, world.Project, "support_hours", 1m, Period, "sheet:support:project", Start.AddHours(9),
                OccurredAt: Start.AddDays(3), Amount: 15m, Currency: "USD"),
            new LedgerSeed(otherPeriodRow, world.Project, "results", 9m, "2026-08", "sheet:other:period", Start.AddHours(9)),
            new LedgerSeed(foreignOrgRow, otherProject, "results", 7m, Period, "sheet:other:org", Start.AddHours(9),
                Org: otherOrg));

        var rows = await InScopeAsync(world, world.Org, readOnly: true, db =>
            new UsageReadStore(db).ReadWorksheetLinesAsync(world.Org, Period, 11, Token));

        rows.Select(row => row.Id).ShouldBe([resultsOrg, resultsProject, resultsAdjustment, supportOrg, supportProject]);
        rows.ShouldNotContain(row => row.Id == otherPeriodRow, "another period's ledger rows are not in this worksheet");
        rows.ShouldNotContain(row => row.Id == foreignOrgRow, "another tenant's ledger rows are never read");

        rows[1].ProjectId.ShouldBe(world.Project);
        rows[1].Amount.ShouldBe(12m);
        rows[1].Currency.ShouldBe("USD");
        rows[1].AdjustmentOf.ShouldBeNull();
        rows[1].Reason.ShouldBeNull();

        rows[2].AdjustmentOf.ShouldBe(resultsProject, "an adjustment carries the row it corrects");
        rows[2].Reason.ShouldBe("over-counted retry");
        rows[2].Quantity.ShouldBe(-1m, "an adjustment may subtract");
        rows[2].Amount.ShouldBeNull("an unpriced line stays unknown rather than becoming free");

        // The other period's row and the other org's row are absent from the period's worksheet.
        rows.ShouldNotContain(row => row.Id == supportOrg && row.Quantity == 7m);

        // Reading twice answers the same sequence, which is what makes the document hash stable.
        var again = await InScopeAsync(world, world.Org, readOnly: true, db =>
            new UsageReadStore(db).ReadWorksheetLinesAsync(world.Org, Period, 11, Token));
        again.Select(row => row.Id).ShouldBe(rows.Select(row => row.Id));

        // The limit is the caller's, not a page: it cuts the tail and nothing else.
        var firstThree = await InScopeAsync(world, world.Org, readOnly: true, db =>
            new UsageReadStore(db).ReadWorksheetLinesAsync(world.Org, Period, 3, Token));
        firstThree.Select(row => row.Id).ShouldBe([resultsOrg, resultsProject, resultsAdjustment]);

        // The org predicate is not decorative: asking for the other tenant inside this scope answers nothing.
        var foreign = await InScopeAsync(world, world.Org, readOnly: true, db =>
            new UsageReadStore(db).ReadWorksheetLinesAsync(otherOrg, Period, 11, Token));
        foreign.ShouldBeEmpty();
    }

    [Fact]
    public async Task WorksheetLines_SameInstant_OrdersById()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var instant = Start.AddDays(5);

        await world.SeedLedgerAsync(
            new LedgerSeed(first, world.Project, "results", 1m, Period, "tie:a", Start, OccurredAt: instant),
            new LedgerSeed(second, world.Project, "results", 2m, Period, "tie:b", Start, OccurredAt: instant));

        var rows = await InScopeAsync(world, world.Org, readOnly: true, db =>
            new UsageReadStore(db).ReadWorksheetLinesAsync(world.Org, Period, 11, Token));

        rows.Count.ShouldBe(2);

        // The tie-break is the database's uuid order, read from the same catalogue rather than guessed.
        var smaller = await InScopeAsync(world, world.Org, readOnly: true, db =>
            db.ScalarAsync<Guid>("select least(@a::uuid, @b::uuid)", new { a = first, b = second }, Token));

        rows[0].Id.ShouldBe(smaller, "two rows of one instant must be ordered by id");
        rows[1].Id.ShouldNotBe(smaller);
    }

    // ------------------------------------------------------------------ the issue

    [Fact]
    public async Task TryInsert_FirstIssue_WritesTheDocument_AndFindReadsItBack()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var statement = Statement(period: "2026-08");

        var inserted = await InScopeAsync(world, world.Org, readOnly: false, db =>
            new BillingStatementStore(db).TryInsertAsync(world.Org, statement, Token));

        inserted.ShouldNotBeNull("the period has no statement yet, so the insert wins");
        inserted.Id.ShouldBe(statement.Id);
        inserted.TotalAmount.ShouldBe(statement.TotalAmount);
        inserted.ContentHash.ShouldBe(statement.ContentHash);

        var found = await InScopeAsync(world, world.Org, readOnly: true, db =>
            new BillingStatementStore(db).FindAsync(world.Org, statement.Period, BillingStatementStore.InvoiceKind, Token));

        found.ShouldNotBeNull();
        found.LineCount.ShouldBe(statement.LineCount);
        found.IssuedBy.ShouldBe(statement.IssuedBy);
        found.IssuedAt.ShouldBe(statement.IssuedAt);
        found.ContentHash.ShouldBe(Hash, "the document address is the stored hash, not the jsonb text");

        // jsonb normalises the document's key order and whitespace, so the text is compared as a document.
        using var stored = JsonDocument.Parse(found.WorksheetJson);
        using var sent = JsonDocument.Parse(statement.WorksheetJson);
        JsonElement.DeepEquals(stored.RootElement, sent.RootElement).ShouldBeTrue(
            "the worksheet round-trips as the same document");

        // Another tenant's lookup answers nothing, which is the policy and the org predicate together.
        var foreign = await InScopeAsync(world, world.Org, readOnly: true, db =>
            new BillingStatementStore(db).FindAsync(Guid.CreateVersion7(), statement.Period, BillingStatementStore.InvoiceKind, Token));
        foreign.ShouldBeNull();
    }

    [Fact]
    public async Task TryInsert_SecondIssueOfThePeriod_ReturnsNullAndKeepsTheIssuedDocument()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var issued = Statement(period: "2026-07");

        var first = await InScopeAsync(world, world.Org, readOnly: false, db =>
            new BillingStatementStore(db).TryInsertAsync(world.Org, issued, Token));
        first.ShouldNotBeNull();

        var late = Statement(period: "2026-07")
            with
        { ContentHash = new string('b', 64), TotalAmount = 11m };

        var second = await InScopeAsync(world, world.Org, readOnly: false, db =>
            new BillingStatementStore(db).TryInsertAsync(world.Org, late, Token));

        second.ShouldBeNull("the period already has a statement, so the late issue does not win");

        var found = await InScopeAsync(world, world.Org, readOnly: true, db =>
            new BillingStatementStore(db).FindAsync(world.Org, "2026-07", BillingStatementStore.InvoiceKind, Token));

        found.ShouldNotBeNull();
        found.Id.ShouldBe(issued.Id, "the issued document is untouched");
        found.ContentHash.ShouldBe(issued.ContentHash, "the loser must not overwrite the frozen hash");
        found.TotalAmount.ShouldBe(10.50m);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One statement to issue, with a worksheet small enough to compare as a document.</summary>
    private static BillingStatementRow Statement(string period) => new(
        Guid.CreateVersion7(),
        period,
        BillingStatementStore.InvoiceKind,
        10.50m,
        "USD",
        2,
        Hash,
        $$"""{"period":"{{period}}","lines":[{"metric":"results","quantity":3},{"metric":"results","quantity":-1}]}""",
        Guid.CreateVersion7(),
        new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero));

    /// <summary>Runs one store call inside a production tenant scope of the world's database.</summary>
    /// <remarks>
    /// A write scope commits before it disposes, because a scope that is only disposed rolls back: the
    /// next read would then prove nothing about the row the call wrote.
    /// </remarks>
    private static async Task<T> InScopeAsync<T>(
        UsageReadsWorld world,
        Guid org,
        bool readOnly,
        Func<ITenantDb, Task<T>> call)
    {
        await using var provider = world.Host.Services.CreateAsyncScope();
        var scopes = provider.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = provider.ServiceProvider.GetRequiredService<ITenantDb>();

        await using var tenant = await scopes.BeginAsync(org, world.Project, readOnly, Token);

        var result = await call(db);

        if (!readOnly)
        {
            await tenant.CommitAsync(Token);
        }

        return result;
    }
}
