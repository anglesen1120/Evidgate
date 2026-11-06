using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The worksheet read of a period that was already issued: the statement it froze is reported beside the live
/// figures, and a row or a correction that reached the period after the issue is reported as a drift instead of
/// silently moving the total the org was billed.
/// </summary>
/// <remarks>
/// The world runs on one fixed instant (2026-11-06), so <see cref="Period"/> has ended and can be issued while a row
/// still lands in it: the write-side freeze of a period is not assumed here, because it is exactly what this read
/// has to survive. Every assertion about the stored statement reads the table as the owner.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageInvoiceWorksheetDriftTests
{
    private const string Period = "2026-10";
    private const string Action = "usage.invoice_worksheet_read";
    private const string IssueAction = "usage.statement_issued";

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public UsageInvoiceWorksheetDriftTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Worksheet_IssuedPeriod_ReportsTheStoredStatementAndReadsIdenticallyTwice()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "issued:1", 1m, 2m, 20),
            Ledger(world, "issued:2", 2m, 3m, 21));

        var issued = await world.PostStatementAsync(Period, 5m, "USD");
        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        var statementId = issued.Body.GetProperty("id").GetGuid();
        var issuedHash = issued.Body.GetProperty("content_hash").GetString();

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("total").GetDecimal().ShouldBe(5m);
        answer.Body.GetProperty("content_hash").GetString().ShouldBe(issuedHash);
        answer.Body.GetProperty("drifted_since_issue").GetBoolean().ShouldBeFalse(
            "no row reached the period after the issue, so the live worksheet still hashes to the document");

        var statement = answer.Body.GetProperty("issued");
        statement.ValueKind.ShouldBe(JsonValueKind.Object);
        statement.GetProperty("statement_id").GetGuid().ShouldBe(statementId);
        statement.GetProperty("content_hash").GetString().ShouldBe(issuedHash);
        statement.GetProperty("total_amount").GetDecimal().ShouldBe(5m);
        statement.GetProperty("currency").GetString().ShouldBe("USD");
        statement.GetProperty("issued_at").GetDateTimeOffset()
            .ShouldBe(
                issued.Body.GetProperty("issued_at").GetDateTimeOffset(),
                "the instant is the one the stored row carries, not the instant of this read");

        var first = await world.GetTextAsync(world.WorksheetUrl($"period={Period}"));
        var second = await world.GetTextAsync(world.WorksheetUrl($"period={Period}"));

        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Text.ShouldBe(
            first.Text,
            "the issued values come from the stored row, so two reads of an unchanged period render the same bytes");
    }

    [Fact]
    public async Task Worksheet_LateLedgerRow_DriftsFromTheIssuedStatementAndReissueConflicts()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "late:1", 1m, 2m, 20));
        var issued = await world.PostStatementAsync(Period, 2m, "USD");
        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        var issuedHash = issued.Body.GetProperty("content_hash").GetString();
        var stored = await world.StatementRowAsync(Period);

        await world.SeedLedgerAsync(Ledger(world, "late:2", 1m, 3m, 21));

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("total").GetDecimal().ShouldBe(5m, "the period now bills both rows");
        answer.Body.GetProperty("line_count").GetInt32().ShouldBe(2);
        answer.Body.GetProperty("drifted_since_issue").GetBoolean().ShouldBeTrue(
            "a row reached the period after the statement froze it");

        var statement = answer.Body.GetProperty("issued");
        statement.GetProperty("total_amount").GetDecimal().ShouldBe(2m, "the issued total never follows the ledger");
        statement.GetProperty("content_hash").GetString().ShouldBe(issuedHash);
        answer.Body.GetProperty("content_hash").GetString().ShouldNotBe(
            issuedHash,
            "the live document describes a period the statement does not");

        // The conflict is the drift itself: the recompute passes every rule and then meets the issued document.
        var reissue = await world.PostStatementAsync(Period, 5m, "USD");

        reissue.Status.ShouldBe(HttpStatusCode.Conflict, reissue.ErrorMessage);
        reissue.ErrorCode.ShouldBe("statement_exists");
        (await world.StatementRowAsync(Period)).ShouldBe(stored, "the issued document is never rewritten");
        (await world.StatementCountAsync(Period)).ShouldBe(1L);
        (await world.AuditCountAsync(IssueAction)).ShouldBe(1L);

        var rows = await world.AuditRowsAsync(Action);
        using var metadata = JsonDocument.Parse(rows.ShouldHaveSingleItem().Split('|', 5)[4]);
        metadata.RootElement.GetProperty("outcome").GetString().ShouldBe(
            "drifted",
            "the read's audit row says the period moved, still without naming an amount");
    }

    [Fact]
    public async Task Worksheet_LateAdjustment_DriftsFromTheIssuedStatement()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "late-adjustment:1", 1m, 2m, 20);
        await world.SeedLedgerAsync(original);
        var issued = await world.PostStatementAsync(Period, 2m, "USD");
        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        var issuedHash = issued.Body.GetProperty("content_hash").GetString();

        await world.SeedLedgerAsync(Adjustment(world, "late-adjustment:2", original.Id, -1m, -2m, 21));

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("total").GetDecimal().ShouldBe(0m, "the correction cancels the issued row");
        answer.Body.GetProperty("adjustment_total").GetDecimal().ShouldBe(-2m);
        answer.Body.GetProperty("drifted_since_issue").GetBoolean().ShouldBeTrue(
            "a correction reached the period after the statement froze it");

        var statement = answer.Body.GetProperty("issued");
        statement.GetProperty("total_amount").GetDecimal().ShouldBe(2m);
        statement.GetProperty("content_hash").GetString().ShouldBe(issuedHash);
        answer.Body.GetProperty("content_hash").GetString().ShouldNotBe(issuedHash);

        // The correction reaches the conflict even though the recompute passes every rule: the total is 0 and the
        // document it hashes to is not the one the period was issued under.
        var reissue = await world.PostStatementAsync(Period, 0m, "USD");

        reissue.Status.ShouldBe(HttpStatusCode.Conflict, reissue.ErrorMessage);
        reissue.ErrorCode.ShouldBe("statement_exists");
        (await world.StatementCountAsync(Period)).ShouldBe(1L);
        (await world.AuditCountAsync(IssueAction)).ShouldBe(1L, "a refused re-issue writes nothing");
    }

    [Fact]
    public async Task Worksheet_NeverIssuedPeriod_ReportsNoStatement()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "unissued:1", 1m, 2m, 20));

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("issued").ValueKind.ShouldBe(JsonValueKind.Null);
        answer.Body.GetProperty("drifted_since_issue").GetBoolean().ShouldBeFalse(
            "nothing was issued, so there is no document to have drifted from");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
    }

    [Fact]
    public async Task Worksheet_AnotherOrgsStatement_IsNeverReportedAsThisPeriodsIssue()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "own:1", 1m, 2m, 20),
            new LedgerSeed(
                Guid.CreateVersion7(),
                otherProject,
                "results",
                1m,
                Period,
                "other:1",
                new DateTimeOffset(2026, 10, 20, 8, 20, 0, TimeSpan.Zero),
                Org: otherOrg,
                Amount: 4m,
                Currency: "USD"));

        var otherIssued = await world.PostStatementAsync(Period, 4m, "USD", org: otherOrg);
        otherIssued.Status.ShouldBe(HttpStatusCode.OK, otherIssued.ErrorMessage);

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("total").GetDecimal().ShouldBe(2m, "the other org's row never reaches this worksheet");
        answer.Body.GetProperty("issued").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "a statement of another org is not this period's issue");
        answer.Body.GetProperty("drifted_since_issue").GetBoolean().ShouldBeFalse();
        answer.Body.GetProperty("content_hash").GetString().ShouldNotBe(
            otherIssued.Body.GetProperty("content_hash").GetString());

        // Issuing this org's own statement then reports the statement of this org, not the neighbour's.
        var own = await world.PostStatementAsync(Period, 2m, "USD");
        own.Status.ShouldBe(HttpStatusCode.OK, own.ErrorMessage);

        var issuedHere = await world.GetAsync(world.WorksheetUrl($"period={Period}"));
        var statement = issuedHere.Body.GetProperty("issued");
        statement.GetProperty("statement_id").GetGuid().ShouldBe(own.Body.GetProperty("id").GetGuid());
        statement.GetProperty("total_amount").GetDecimal().ShouldBe(2m);
        issuedHere.Body.GetProperty("drifted_since_issue").GetBoolean().ShouldBeFalse();
    }

    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted, new FakeTimeProvider(Now));

    /// <summary>One priced usage row of <see cref="Period"/>, at the minute <paramref name="minute"/> of 2026-10-20.</summary>
    private static LedgerSeed Ledger(
        UsageReadsWorld world,
        string key,
        decimal quantity,
        decimal amount,
        int minute) =>
        new(
            Guid.CreateVersion7(),
            world.Project,
            "results",
            quantity,
            Period,
            key,
            new DateTimeOffset(2026, 10, 20, 8, minute, 0, TimeSpan.Zero),
            Amount: amount,
            Currency: "USD");

    /// <summary>A correction of <paramref name="originalId"/>: negative quantity, negative amount, and its reason.</summary>
    private static LedgerSeed Adjustment(
        UsageReadsWorld world,
        string key,
        Guid originalId,
        decimal quantity,
        decimal amount,
        int minute) =>
        Ledger(world, key, quantity, amount, minute) with
        {
            AdjustmentOf = originalId,
            Reason = "duplicate row withdrawn",
        };
}
