using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The invoice reconciliation over the real host and a real PostgreSQL: the ledger's live totals beside
/// the stored roll-up, every way the two can disagree, the roll-up's own gap flags and the period's
/// repeated keys.
/// </summary>
/// <remarks>
/// The comparison is a database aggregate, so the seeded row count is the only lever these tests need on
/// cost; the statement log proves a large period is still one aggregate statement rather than a query per
/// row. Rows are seeded as the owner with pinned instants: a period's boundary and a roll-up's stored
/// <c>computed_at</c> are inputs, not outcomes, of this read.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageReconciliationReadTests
{
    private const string Period = "2026-10";
    private const string Earlier = "2026-09";

    private static readonly DateTimeOffset ComputedAt = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public UsageReconciliationReadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reconciliation_AgreeingSides_ReportMatchAndBothTotals()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(Ledger(world, "agree:1", 1m), Ledger(world, "agree:2", 2m));
        await world.SeedRollupAsync(Rollup(world, "results", 3m, rowCount: 2));

        var answer = await world.GetAsync(world.ReconciliationUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(Period);

        var unit = SingleUnit(answer);
        unit.GetProperty("matches").GetBoolean().ShouldBeTrue();
        unit.GetProperty("mismatch").ValueKind.ShouldBe(JsonValueKind.Null);
        unit.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        unit.GetProperty("metric").GetString().ShouldBe("results");
        unit.GetProperty("ledger_total_quantity").GetDecimal().ShouldBe(3m);
        unit.GetProperty("ledger_row_count").GetInt64().ShouldBe(2);
        unit.GetProperty("rollup_total_quantity").GetDecimal().ShouldBe(3m);
        unit.GetProperty("rollup_row_count").GetInt64().ShouldBe(2);
        unit.GetProperty("rollup_computed_at").GetDateTimeOffset().ShouldBe(ComputedAt);

        answer.Body.GetProperty("gaps").GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("duplicate_attempts").GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("duplicate_attempts_truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Reconciliation_DifferingTotal_NamesQuantity()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(Ledger(world, "quantity:1", 1m), Ledger(world, "quantity:2", 2m));
        await world.SeedRollupAsync(Rollup(world, "results", 4m, rowCount: 2));

        var unit = SingleUnit(await world.GetAsync(world.ReconciliationUrl($"period={Period}")));

        unit.GetProperty("matches").GetBoolean().ShouldBeFalse();
        unit.GetProperty("mismatch").GetString().ShouldBe("quantity");
        unit.GetProperty("ledger_total_quantity").GetDecimal().ShouldBe(3m);
        unit.GetProperty("rollup_total_quantity").GetDecimal().ShouldBe(4m);
    }

    [Fact]
    public async Task Reconciliation_EqualTotalOnFewerRows_NamesRowCount()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(Ledger(world, "rows:1", 1m), Ledger(world, "rows:2", 1m));
        await world.SeedRollupAsync(Rollup(world, "results", 2m, rowCount: 1));

        var unit = SingleUnit(await world.GetAsync(world.ReconciliationUrl($"period={Period}")));

        unit.GetProperty("matches").GetBoolean().ShouldBeFalse();
        unit.GetProperty("mismatch").GetString().ShouldBe("row_count");
        unit.GetProperty("ledger_row_count").GetInt64().ShouldBe(2);
        unit.GetProperty("rollup_row_count").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Reconciliation_SideWithoutCounterpart_IsNamedNotInvented()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(Ledger(world, "norollup:1", 2m) with { Metric = "artifact_gb_month" });
        await world.SeedRollupAsync(Rollup(world, "support_hours", 5m, rowCount: 3));

        var units = Units(await world.GetAsync(world.ReconciliationUrl($"period={Period}")));
        units.Count.ShouldBe(2);

        var ledgerOnly = units.Single(unit => MetricOf(unit) == "artifact_gb_month");
        ledgerOnly.GetProperty("mismatch").GetString().ShouldBe("missing_rollup");
        ledgerOnly.GetProperty("rollup_total_quantity").ValueKind.ShouldBe(JsonValueKind.Null);
        ledgerOnly.GetProperty("rollup_computed_at").ValueKind.ShouldBe(JsonValueKind.Null);
        ledgerOnly.GetProperty("ledger_total_quantity").GetDecimal().ShouldBe(2m);

        var rollupOnly = units.Single(unit => MetricOf(unit) == "support_hours");
        rollupOnly.GetProperty("mismatch").GetString().ShouldBe("missing_ledger");
        rollupOnly.GetProperty("ledger_total_quantity").GetDecimal().ShouldBe(0m);
        rollupOnly.GetProperty("ledger_row_count").GetInt64().ShouldBe(0);
        rollupOnly.GetProperty("rollup_total_quantity").GetDecimal().ShouldBe(5m);
    }

    [Fact]
    public async Task Reconciliation_OrgLevelRollup_ComparesAgainstTheNullProjectRows()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(
            Ledger(world, "orglevel:1", 4m) with { ProjectId = null, Metric = "support_hours" },
            Ledger(world, "orglevel:2", 1m) with { ProjectId = null, Metric = "support_hours" });
        await world.SeedRollupAsync(Rollup(world, "support_hours", 5m, rowCount: 2) with { ProjectId = null });

        var unit = SingleUnit(await world.GetAsync(world.ReconciliationUrl($"period={Period}")));

        unit.GetProperty("project_id").ValueKind.ShouldBe(JsonValueKind.Null, "an org-level metric has no project");
        unit.GetProperty("matches").GetBoolean().ShouldBeTrue();
        unit.GetProperty("ledger_total_quantity").GetDecimal().ShouldBe(5m);
    }

    [Fact]
    public async Task Reconciliation_CorrectionRows_AreCountedInTheLedgerTotal()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var original = Ledger(world, "correction:original", 5m);
        var adjustment = Ledger(world, "correction:adjustment", -2m) with
        {
            AdjustmentOf = original.Id,
            Reason = "duplicate row withdrawn",
        };
        await world.SeedLedgerAsync(original, adjustment);
        await world.SeedRollupAsync(Rollup(world, "results", 3m, rowCount: 2));

        var unit = SingleUnit(await world.GetAsync(world.ReconciliationUrl($"period={Period}")));

        unit.GetProperty("ledger_total_quantity").GetDecimal().ShouldBe(3m, "the correction counts toward the period");
        unit.GetProperty("ledger_row_count").GetInt64().ShouldBe(2);
        unit.GetProperty("matches").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Reconciliation_GapsAndRepeatedKeys_AreReportedForThisPeriodOnly()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(
            Ledger(world, "gapped:1", 1m) with { Metric = "artifact_gb_month" },
            Ledger(world, "repeat:1", 1m),
            Ledger(world, "repeat:other-period", 1m) with { Period = Earlier });
        await world.SeedRollupAsync(
            Rollup(world, "artifact_gb_month", 1m, rowCount: 1, gapFlag: true),
            Rollup(world, "results", 1m, rowCount: 1));
        await world.SeedDuplicateAttemptsAsync(
            new DuplicateSeed("repeat:1", 3, ComputedAt, ComputedAt.AddMinutes(5)),
            new DuplicateSeed(
                "repeat:other-period", 2, ComputedAt.AddDays(-1), ComputedAt.AddDays(-1).AddMinutes(1)));

        var answer = await world.GetAsync(world.ReconciliationUrl($"period={Period}"));
        answer.Status.ShouldBe(HttpStatusCode.OK);

        var gaps = answer.Body.GetProperty("gaps").EnumerateArray().ToArray();
        gaps.Length.ShouldBe(1);
        gaps[0].GetProperty("metric").GetString().ShouldBe("artifact_gb_month");
        gaps[0].GetProperty("total_quantity").GetDecimal().ShouldBe(1m);
        gaps[0].GetProperty("computed_at").GetDateTimeOffset().ShouldBe(ComputedAt);

        var duplicates = answer.Body.GetProperty("duplicate_attempts").EnumerateArray().ToArray();
        duplicates.Length.ShouldBe(1, "a key of another period is not this period's re-send");
        duplicates[0].GetProperty("billing_key").GetString().ShouldBe("repeat:1");
        duplicates[0].GetProperty("attempts").GetInt32().ShouldBe(3);
        duplicates[0].GetProperty("first_seen_at").GetDateTimeOffset().ShouldBe(ComputedAt);
        answer.Body.GetProperty("duplicate_attempts_truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Reconciliation_ManyRepeatedKeys_AreCappedAndFlagged()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedRepeatedKeysAsync(205, Period);
        await world.SeedRollupAsync(Rollup(world, "results", 205m, rowCount: 205) with { ProjectId = null });

        var answer = await world.GetAsync(world.ReconciliationUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("duplicate_attempts").GetArrayLength().ShouldBe(200);
        answer.Body.GetProperty("duplicate_attempts_truncated").GetBoolean().ShouldBeTrue(
            "the cap is reported, so a caller can page or narrow rather than trust a short list");
    }

    [Fact]
    public async Task Reconciliation_LargePeriod_CostsTheSameSingleAggregate()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        var small = await AggregateStatementAsync(world, Period);

        await world.SeedManyLedgerRowsAsync(300, Earlier, "results", 1m);
        var large = await AggregateStatementAsync(world, Earlier);

        large.ShouldBe(
            small,
            "the aggregate is parameterised by period, so a period of 300 rows runs the same text as one of two");
    }

    [Fact]
    public async Task Reconciliation_UnknownOrg_NotFoundAndNoAuditRow()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var unknown = Guid.CreateVersion7();

        var answer = await world.GetAsync($"/internal/operator/orgs/{unknown:D}/usage-reconciliation?period={Period}");

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe(ApiErrorCode.NotFound);
        (await world.AuditCountForAsync(unknown, ReconciliationAction())).ShouldBe(0);
    }

    [Fact]
    public async Task Reconciliation_DeniedGate_ForbiddenAndNoAuditRow()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Refused);

        var answer = await world.GetAsync(world.ReconciliationUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.AuditCountAsync(ReconciliationAction())).ShouldBe(0);
    }

    [Fact]
    public async Task Reconciliation_Period_IsRequiredAndMustNameAMonth()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        var missing = await world.GetAsync(world.ReconciliationUrl());
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe("period");

        var malformed = await world.GetAsync(world.ReconciliationUrl("period=2026-1"));
        malformed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        malformed.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        malformed.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe("period");
    }

    [Fact]
    public async Task Reconciliation_EachSuccessfulRead_WritesOneAuditRowNamingThePeriod()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(Ledger(world, "audited:1", 1m));

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var answer = await world.GetAsync(world.ReconciliationUrl($"period={Period}"));
            answer.Status.ShouldBe(HttpStatusCode.OK);
        }

        var rows = await world.AuditRowsAsync(ReconciliationAction());
        rows.Count.ShouldBe(2);

        var parts = rows[0].Split('|', 5);
        parts[0].ShouldBe("platform_operator");
        parts[1].ShouldBe(UsageReadsWorld.OperatorId.ToString("D"));
        parts[2].ShouldBe("usage_rollup");
        parts[3].ShouldBe(world.Org.ToString("D"));

        using var metadata = JsonDocument.Parse(parts[4]);
        metadata.RootElement.GetProperty("period").GetString().ShouldBe(Period);
        metadata.RootElement.GetProperty("count").GetString()
            .ShouldBe("1", "one reconciled scope: the project's results metric");
        metadata.RootElement.GetProperty("outcome").GetString().ShouldBe("complete");
    }

    /// <summary>The single aggregate statement the reconciliation ran for a period.</summary>
    /// <param name="world">The world.</param>
    /// <param name="period">The reconciled period.</param>
    private static async Task<string> AggregateStatementAsync(UsageReadsWorld world, string period)
    {
        world.Api.Statements.Clear();
        var answer = await world.GetAsync(world.ReconciliationUrl($"period={period}"));
        answer.Status.ShouldBe(HttpStatusCode.OK);

        var statements = world.StatementsTouching("usage_rollup");
        statements.Count.ShouldBe(1, "the comparison is one aggregate joining the roll-up to the ledger");

        return statements[0];
    }

    private static LedgerSeed Ledger(UsageReadsWorld world, string key, decimal quantity) => new(
        Guid.CreateVersion7(),
        world.Project,
        "results",
        quantity,
        Period,
        key,
        new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

    private static RollupSeed Rollup(UsageReadsWorld world, string metric, decimal total, int rowCount, bool gapFlag = false) =>
        new(Guid.CreateVersion7(), world.Project, Period, metric, total, rowCount, ComputedAt, gapFlag);

    private static string? MetricOf(JsonElement unit) => unit.GetProperty("metric").GetString();

    private static IReadOnlyList<JsonElement> Units(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("units").EnumerateArray()];

    private static JsonElement SingleUnit(ApiAnswer answer)
    {
        var units = Units(answer);
        units.Count.ShouldBe(1);
        return units[0];
    }

    /// <summary>The audited action of a reconciliation read.</summary>
    private static string ReconciliationAction() => "usage.reconciliation_read";
}
