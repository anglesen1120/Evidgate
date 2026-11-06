using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The invoice worksheet over the real host and a real PostgreSQL: the period's usage rows beside its corrections,
/// the totals the ledger itself aggregates, the hash that names the document, and the ways a period is reported as
/// unpriced, mixed, truncated or unreconciled instead of being passed off as billable.
/// </summary>
/// <remarks>
/// Every world runs on one fixed instant (2026-11-06), so "the period" is a decided input: <see cref="Period"/> has
/// ended and <see cref="CurrentPeriod"/> has not. Seeds run as the owner, which is what lets a case write rows for
/// another org and assert the read never reaches them.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageInvoiceWorksheetTests
{
    private const string Period = "2026-10";
    private const string CurrentPeriod = "2026-11";
    private const string Earlier = "2026-09";

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public UsageInvoiceWorksheetTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Worksheet_PricedPeriod_SplitsLinesAndCorrectionsAndTotalsAgree()
    {
        await using var world = await StartAsync();
        var first = Ledger(world, "priced:1", "results", 1m, 2m, 20);
        await world.SeedLedgerAsync(
            first,
            Ledger(world, "priced:2", "results", 2m, 3m, 21),
            Ledger(world, "priced:3", "artifact_gb_month", 4m, 4m, 22),
            OrgLevel(world, "priced:4", 5m, 5m),
            Adjustment(world, "priced:5", first.Id, -1m, -2m, 23));

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(Period);
        answer.Body.GetProperty("currency").GetString().ShouldBe("USD");
        answer.Body.GetProperty("line_count").GetInt32().ShouldBe(4);
        answer.Body.GetProperty("unpriced_line_count").GetInt32().ShouldBe(0);
        answer.Body.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        answer.Body.GetProperty("reconciled").GetBoolean().ShouldBeTrue(
            "the rows and the ledger aggregate describe the same period");

        // The total is the ledger's own sum over every row of the period, corrections included.
        answer.Body.GetProperty("line_total").GetDecimal().ShouldBe(14m);
        answer.Body.GetProperty("adjustment_total").GetDecimal().ShouldBe(-2m);
        answer.Body.GetProperty("total").GetDecimal().ShouldBe(12m);
        answer.Body.GetProperty("total").GetDecimal()
            .ShouldBe(answer.Body.GetProperty("line_total").GetDecimal()
                + answer.Body.GetProperty("adjustment_total").GetDecimal());

        var lines = Lines(answer);
        lines.Count.ShouldBe(4);
        MetricOf(lines[0]).ShouldBe("artifact_gb_month");
        MetricOf(lines[^1]).ShouldBe("support_hours", "an org-level metric sorts after the projects' ones");
        lines[^1].GetProperty("project_id").ValueKind.ShouldBe(JsonValueKind.Null);
        lines[1].GetProperty("occurred_at").GetDateTimeOffset()
            .ShouldBeLessThan(lines[2].GetProperty("occurred_at").GetDateTimeOffset());

        var adjustments = Adjustments(answer);
        adjustments.Count.ShouldBe(1);
        adjustments[0].GetProperty("adjustment_of").GetGuid().ShouldBe(first.Id);
        adjustments[0].GetProperty("reason").GetString().ShouldBe("duplicate row withdrawn");
        adjustments[0].GetProperty("quantity").GetDecimal().ShouldBe(-1m);
        adjustments[0].GetProperty("amount").GetDecimal().ShouldBe(-2m);
    }

    [Fact]
    public async Task Worksheet_ReadTwice_IsByteIdenticalAndHoldsTheSameHash()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "stable:1", "results", 1m, 2m, 20),
            OrgLevel(world, "stable:2", 3m, 7m));

        var first = await world.GetTextAsync(world.WorksheetUrl($"period={Period}"));
        var second = await world.GetTextAsync(world.WorksheetUrl($"period={Period}"));

        first.Status.ShouldBe(HttpStatusCode.OK);
        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Text.ShouldBe(
            first.Text,
            "the worksheet carries no instant of generation, so an unchanged period renders the same bytes");

        using var document = JsonDocument.Parse(first.Text);
        document.RootElement.GetProperty("content_hash").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Worksheet_UnpricedRow_CountsItAndLeavesTheSumsUnknown()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "partial:1", "results", 1m, 2m, 20),
            Ledger(world, "partial:2", "results", 2m, null, 21));

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("unpriced_line_count").GetInt32().ShouldBe(1);
        answer.Body.GetProperty("line_total").ValueKind.ShouldBe(JsonValueKind.Null);
        answer.Body.GetProperty("total").ValueKind.ShouldBe(JsonValueKind.Null);
        answer.Body.GetProperty("currency").ValueKind.ShouldBe(JsonValueKind.Null);
        answer.Body.GetProperty("reconciled").GetBoolean().ShouldBeTrue(
            "both sides are unknown, so a missing price is a gap in pricing, not a reconciliation mismatch");
    }

    [Fact]
    public async Task Worksheet_MixedCurrencies_NamesNoCurrency()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "mixed:1", "results", 1m, 2m, 20),
            Ledger(world, "mixed:2", "artifact_gb_month", 1m, 3m, 21, currency: "EUR"));

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("currency").ValueKind.ShouldBe(JsonValueKind.Null);
        answer.Body.GetProperty("total").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Worksheet_AnotherOrgsRows_NeverAppear()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "foreign:1", "results", 1m, 2m, 20) with
            {
                Org = otherOrg,
                ProjectId = otherProject,
            });

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        Lines(answer).ShouldBeEmpty();
        Adjustments(answer).ShouldBeEmpty();
        answer.Body.GetProperty("line_count").GetInt32().ShouldBe(0);
        answer.Body.GetProperty("total").GetDecimal().ShouldBe(0m);
        answer.Body.GetProperty("currency").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Worksheet_RollupOnlyScope_IsNotAWorksheetRow()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "rollup:1", "results", 1m, 2m, 20));
        await world.SeedRollupAsync(Rollup(world, "support_hours", 5m, rowCount: 3));

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        Lines(answer).Count.ShouldBe(1);
        answer.Body.GetProperty("total").GetDecimal().ShouldBe(2m);
        answer.Body.GetProperty("reconciled").GetBoolean().ShouldBeTrue(
            "a roll-up without ledger rows bills nothing of the period, so it cannot disagree with the lines");
    }

    [Fact]
    public async Task Worksheet_Period_IsRequiredAndMustNameAMonth()
    {
        await using var world = await StartAsync();

        var missing = await world.GetAsync(world.WorksheetUrl());
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(missing).GetProperty("reason").GetString().ShouldBe("required");

        var malformed = await world.GetAsync(world.WorksheetUrl("period=2026-13"));
        malformed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        malformed.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        Detail(malformed).GetProperty("path").GetString().ShouldBe("period");
        Detail(malformed).GetProperty("reason").GetString().ShouldBe("format");
    }

    [Fact]
    public async Task Worksheet_UnknownOrg_NotFoundAndNoAuditRow()
    {
        await using var world = await StartAsync();
        var unknown = Guid.CreateVersion7();

        var answer = await world.GetAsync(UsageReadsWorld.WorksheetUrlFor(unknown, $"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe(ApiErrorCode.NotFound);
        (await world.AuditCountForAsync(unknown, Action)).ShouldBe(0);
    }

    [Fact]
    public async Task Worksheet_AnonymousCaller_Unauthenticated()
    {
        await using var world = await StartAsync(OperatorGateMode.Denied);

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await world.AuditCountAsync(Action)).ShouldBe(0);
    }

    [Fact]
    public async Task Worksheet_NonOperator_Forbidden()
    {
        await using var world = await StartAsync(OperatorGateMode.Refused);

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.AuditCountAsync(Action)).ShouldBe(0);
    }

    [Fact]
    public async Task Worksheet_EachSuccessfulRead_WritesOneAuditRowWithoutAmounts()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "audited:1", "results", 1m, 2m, 20);
        await world.SeedLedgerAsync(
            original,
            Adjustment(world, "audited:2", original.Id, -1m, -2m, 21));

        foreach (var _ in Enumerable.Range(0, 2))
        {
            (await world.GetAsync(world.WorksheetUrl($"period={Period}"))).Status.ShouldBe(HttpStatusCode.OK);
        }

        var rows = await world.AuditRowsAsync(Action);
        rows.Count.ShouldBe(2);

        var parts = rows[0].Split('|', 5);
        parts[0].ShouldBe("platform_operator");
        parts[1].ShouldBe(UsageReadsWorld.OperatorId.ToString("D"));
        parts[2].ShouldBe("usage_ledger_entry");
        parts[3].ShouldBe(world.Org.ToString("D"));

        using var metadata = JsonDocument.Parse(parts[4]);
        metadata.RootElement.GetProperty("period").GetString().ShouldBe(Period);
        metadata.RootElement.GetProperty("count").GetString().ShouldBe("2", "both rows of the period");
        metadata.RootElement.GetProperty("outcome").GetString().ShouldBe("complete");
        metadata.RootElement.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["period", "count", "outcome"], ignoreOrder: true);
    }

    [Fact]
    public async Task Worksheet_PastTheRowBound_ReportsTruncationAndIsNotReconciled()
    {
        await using var world = await StartAsync();
        await world.SeedManyPricedLedgerRowsAsync(
            count: 10_001, period: Period, metric: "results", quantityEach: 1m, amountEach: 1m, currency: "USD");

        var answer = await world.GetAsync(world.WorksheetUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("truncated").GetBoolean().ShouldBeTrue(
            "the bound is reported, so a cut worksheet is never mistaken for a complete period");
        answer.Body.GetProperty("line_count").GetInt32().ShouldBe(10_000);
        answer.Body.GetProperty("reconciled").GetBoolean().ShouldBeFalse(
            "a cut list cannot be compared against the whole period's ledger sum");
    }

    private Task<UsageReadsWorld> StartAsync(OperatorGateMode gate = OperatorGateMode.Permitted) =>
        UsageReadsWorld.StartAsync(_fixture, gate, new FakeTimeProvider(Now));

    /// <summary>One priced usage row of <see cref="Period"/>, at the minute <paramref name="minute"/> of 2026-10-20.</summary>
    private static LedgerSeed Ledger(
        UsageReadsWorld world,
        string key,
        string metric,
        decimal quantity,
        decimal? amount,
        int minute,
        string currency = "USD") =>
        new(
            Guid.CreateVersion7(),
            world.Project,
            metric,
            quantity,
            Period,
            key,
            new DateTimeOffset(2026, 10, 20, 8, minute, 0, TimeSpan.Zero),
            Amount: amount,
            Currency: amount is null ? null : currency);

    /// <summary>One usage row of the org itself: a metric no project owns.</summary>
    private static LedgerSeed OrgLevel(UsageReadsWorld world, string key, decimal quantity, decimal amount) =>
        Ledger(world, key, "support_hours", quantity, amount, 25) with { ProjectId = null };

    /// <summary>A correction of <paramref name="originalId"/>: negative quantity, negative amount, and its reason.</summary>
    private static LedgerSeed Adjustment(
        UsageReadsWorld world,
        string key,
        Guid originalId,
        decimal quantity,
        decimal amount,
        int minute) =>
        Ledger(world, key, "results", quantity, amount, minute) with
        {
            AdjustmentOf = originalId,
            Reason = "duplicate row withdrawn",
        };

    private static RollupSeed Rollup(UsageReadsWorld world, string metric, decimal total, int rowCount) =>
        new(Guid.CreateVersion7(), world.Project, Period, metric, total, rowCount,
            new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero));

    private static string? MetricOf(JsonElement line) => line.GetProperty("metric").GetString();

    private static IReadOnlyList<JsonElement> Lines(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("lines").EnumerateArray()];

    private static IReadOnlyList<JsonElement> Adjustments(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("adjustments").EnumerateArray()];

    private static JsonElement Detail(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0];

    /// <summary>The audited action of one worksheet read.</summary>
    private static string Action => "usage.invoice_worksheet_read";
}
