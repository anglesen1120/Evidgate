using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The per-tenant cost read over the real host and a real PostgreSQL: the five lines' sums against their seeded
/// sources, the fixed line order, and the ways an input with no source is reported as null with a reason instead of
/// being passed off as a measured zero.
/// </summary>
/// <remarks>
/// Every world runs on one fixed instant (2026-11-06), so <see cref="Period"/> has ended and
/// <see cref="CurrentPeriod"/> has not. Seeds run as the owner, which is what lets a case write rows for another org
/// and assert the read never counts them.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageCostPerTenantTests
{
    private const string Period = "2026-10";
    private const string CurrentPeriod = "2026-11";
    private const string Earlier = "2026-09";

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The five lines the cost model lists, in the order the read answers them.</summary>
    private static readonly string?[] ExpectedLines =
        ["api_db_share", "object_gb_month", "egress_gb", "ai_tokens", "support_hours"];

    private readonly PostgresTenancyFixture _fixture;

    public UsageCostPerTenantTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CostPerTenant_AvailableLines_EqualSeededSourceRows()
    {
        await using var world = await StartAsync();
        var stored = Ledger(world, "cost:1", "artifact_gb_month", 2.5m, 20);
        await world.SeedLedgerAsync(
            stored,
            Ledger(world, "cost:2", "artifact_gb_month", 1.5m, 21),
            Correction(world, "cost:3", stored.Id, -0.5m, 22),
            Ledger(world, "cost:4", "egress_gb", 4m, 23),
            OrgLevel(world, "cost:5", 1.5m, 24));

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(Period);

        var lines = Lines(answer);
        lines.Select(LineOf).ShouldBe(ExpectedLines);

        Line(answer, "api_db_share").GetProperty("quantity").ValueKind.ShouldBe(JsonValueKind.Null);
        Line(answer, "api_db_share").GetProperty("unit").GetString().ShouldBe("share");
        Line(answer, "api_db_share").GetProperty("unavailable_reason").GetString()
            .ShouldNotBeNullOrWhiteSpace();

        // 2.5 + 1.5 - 0.5: a correction's negative quantity is part of the line's sum.
        AssertAvailable(answer, "object_gb_month", 3.5m, "gb_month", "usage_ledger_entry:artifact_gb_month");
        AssertAvailable(answer, "egress_gb", 4m, "gb", "usage_ledger_entry:egress_gb");
        AssertAvailable(answer, "support_hours", 1.5m, "hours", "usage_ledger_entry:support_hours");

        // No ai_tokens row and no gateway attempt: null with the reason its producer is missing.
        Line(answer, "ai_tokens").GetProperty("quantity").ValueKind.ShouldBe(JsonValueKind.Null);
        Line(answer, "ai_tokens").GetProperty("unavailable_reason").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CostPerTenant_UnmeteredLines_AreNullWithReason()
    {
        await using var world = await StartAsync();

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var lines = Lines(answer);
        lines.Select(LineOf).ShouldBe(ExpectedLines);

        foreach (var line in lines)
        {
            line.GetProperty("quantity").ValueKind.ShouldBe(
                JsonValueKind.Null,
                $"{LineOf(line)} has no source row, so reporting 0 would claim a measurement nobody made");
            line.GetProperty("unavailable_reason").GetString().ShouldNotBeNullOrWhiteSpace();
            line.GetProperty("source").GetString().ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task CostPerTenant_LineNettedToZero_IsAvailableAndZero()
    {
        await using var world = await StartAsync();
        var usage = Ledger(world, "netted:1", "egress_gb", 1m, 20);
        await world.SeedLedgerAsync(
            usage,
            Correction(world, "netted:2", usage.Id, -1m, 21) with { Metric = "egress_gb" });

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var line = Line(answer, "egress_gb");
        line.GetProperty("quantity").GetDecimal().ShouldBe(0m);
        line.GetProperty("unavailable_reason").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "the producer wrote rows that net to zero, which is a measurement unlike a missing source");
    }

    [Fact]
    public async Task CostPerTenant_AiTokens_FallsBackToHealingWhenTheLedgerHoldsNone()
    {
        await using var world = await StartAsync();
        await world.SeedHealingCallsAsync(
            Healing(100, "ok", 7, 30),
            Healing(40, "timeout", 7, 31));

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(answer, "ai_tokens", 140m, "tokens", "healing_llm_call.tokens");
    }

    [Fact]
    public async Task CostPerTenant_AiTokens_PrefersTheLedgerWhenBothHoldRows()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "tokens:1", "ai_tokens", 7m, 20));
        await world.SeedHealingCallsAsync(Healing(999, "ok", 7, 30));

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(answer, "ai_tokens", 7m, "tokens", "usage_ledger_entry:ai_tokens");
    }

    [Fact]
    public async Task CostPerTenant_HealingWindow_IsTheUtcMonth()
    {
        await using var world = await StartAsync();
        await world.SeedHealingCallsAsync(
            new HealingCallSeed(Guid.CreateVersion7(), 10, "ok", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
            new HealingCallSeed(Guid.CreateVersion7(), 20, "error", new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.Zero)),
            new HealingCallSeed(Guid.CreateVersion7(), 40, "ok", new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero)),
            new HealingCallSeed(Guid.CreateVersion7(), 80, "ok", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(answer, "ai_tokens", 30m, "tokens", "healing_llm_call.tokens");
    }

    [Fact]
    public async Task CostPerTenant_HealingAttemptsWithoutKnownTokens_AreUnavailable()
    {
        await using (var unknown = await StartAsync())
        {
            await unknown.SeedHealingCallsAsync(Healing(0, "timeout", 7, 30));

            var answer = await unknown.GetAsync(unknown.CostUrl($"period={Period}"));

            answer.Status.ShouldBe(HttpStatusCode.OK);
            var line = Line(answer, "ai_tokens");
            line.GetProperty("quantity").ValueKind.ShouldBe(
                JsonValueKind.Null,
                "a failed attempt that reported no tokens says nothing about how many it burned");
            line.GetProperty("unavailable_reason").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        await using (var known = await StartAsync())
        {
            await known.SeedHealingCallsAsync(Healing(0, "ok", 7, 30));

            var answer = await known.GetAsync(known.CostUrl($"period={Period}"));

            answer.Status.ShouldBe(HttpStatusCode.OK);
            AssertAvailable(answer, "ai_tokens", 0m, "tokens", "healing_llm_call.tokens");
        }
    }

    [Fact]
    public async Task CostPerTenant_OtherOrgData_IsNeverCounted()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "foreign:1", "artifact_gb_month", 5m, 20) with { Org = otherOrg, ProjectId = otherProject },
            Ledger(world, "foreign:2", "egress_gb", 5m, 21) with { Org = otherOrg, ProjectId = otherProject },
            OrgLevel(world, "foreign:3", 5m, 22) with { Org = otherOrg });
        await world.SeedHealingCallsAsync(
            Healing(500, "ok", 7, 30) with { Org = otherOrg, ProjectId = otherProject });

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        Lines(answer).ShouldAllBe(line => line.GetProperty("quantity").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task CostPerTenant_OtherPeriodRows_AreNotCounted()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "earlier:1", "artifact_gb_month", 5m, 20) with
            {
                Period = Earlier,
                CreatedAt = new DateTimeOffset(2026, 9, 20, 8, 20, 0, TimeSpan.Zero),
            },
            OrgLevel(world, "earlier:2", 5m, 21) with
            {
                Period = Earlier,
                CreatedAt = new DateTimeOffset(2026, 9, 20, 8, 21, 0, TimeSpan.Zero),
            });
        await world.SeedHealingCallsAsync(new HealingCallSeed(
            Guid.CreateVersion7(), 500, "ok", new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero)));

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        Lines(answer).ShouldAllBe(line => line.GetProperty("quantity").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task CostPerTenant_OpenPeriod_Is422AndNotAudited()
    {
        await using var world = await StartAsync();

        var answer = await world.GetAsync(world.CostUrl($"period={CurrentPeriod}"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        Detail(answer).GetProperty("path").GetString().ShouldBe("period");
        Detail(answer).GetProperty("reason").GetString().ShouldBe("open_period");
        (await world.AuditCountAsync(Action)).ShouldBe(0);
    }

    [Fact]
    public async Task CostPerTenant_MissingPeriod_Is422()
    {
        await using var world = await StartAsync();

        var answer = await world.GetAsync(world.CostUrl());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(answer).GetProperty("path").GetString().ShouldBe("period");
        Detail(answer).GetProperty("reason").GetString().ShouldBe("required");
    }

    [Fact]
    public async Task CostPerTenant_MalformedPeriod_Is422()
    {
        await using var world = await StartAsync();

        var answer = await world.GetAsync(world.CostUrl("period=2026-13"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        Detail(answer).GetProperty("path").GetString().ShouldBe("period");
        Detail(answer).GetProperty("reason").GetString().ShouldBe("format");
    }

    [Fact]
    public async Task CostPerTenant_UnknownOrg_Is404AndNotAudited()
    {
        await using var world = await StartAsync();
        var unknown = Guid.CreateVersion7();

        var answer = await world.GetAsync(UsageReadsWorld.CostUrlFor(unknown, $"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe(ApiErrorCode.NotFound);
        (await world.AuditCountForAsync(unknown, Action)).ShouldBe(0);
    }

    [Fact]
    public async Task CostPerTenant_AnonymousCaller_Is401()
    {
        await using var world = await StartAsync(OperatorGateMode.Denied);

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await world.AuditCountAsync(Action)).ShouldBe(0);
    }

    [Fact]
    public async Task CostPerTenant_NonOperator_Is403()
    {
        await using var world = await StartAsync(OperatorGateMode.Refused);

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.AuditCountAsync(Action)).ShouldBe(0);
    }

    [Fact]
    public async Task CostPerTenant_ReadTwice_IsIdentical()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "stable:1", "artifact_gb_month", 1m, 20),
            OrgLevel(world, "stable:2", 2m, 21));
        await world.SeedHealingCallsAsync(Healing(12, "ok", 7, 30));

        var first = await world.GetTextAsync(world.CostUrl($"period={Period}"));
        var second = await world.GetTextAsync(world.CostUrl($"period={Period}"));

        first.Status.ShouldBe(HttpStatusCode.OK);
        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Text.ShouldBe(
            first.Text,
            "the answer carries no instant of generation and a fixed line order, so two reads render the same bytes");
    }

    [Fact]
    public async Task CostPerTenant_EachSuccessfulRead_WritesOneAuditRowWithoutQuantities()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "audited:1", "artifact_gb_month", 1m, 20),
            OrgLevel(world, "audited:2", 2m, 21));

        foreach (var _ in Enumerable.Range(0, 2))
        {
            (await world.GetAsync(world.CostUrl($"period={Period}"))).Status.ShouldBe(HttpStatusCode.OK);
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
        metadata.RootElement.GetProperty("count").GetString().ShouldBe(
            "2",
            "the artifact and support lines were stated, the other three had no source row");
        metadata.RootElement.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["period", "count"], ignoreOrder: true);
    }

    private Task<UsageReadsWorld> StartAsync(OperatorGateMode gate = OperatorGateMode.Permitted) =>
        UsageReadsWorld.StartAsync(_fixture, gate, new FakeTimeProvider(Now));

    /// <summary>One usage row of <see cref="Period"/>, at the minute <paramref name="minute"/> of 2026-10-20.</summary>
    private static LedgerSeed Ledger(
        UsageReadsWorld world,
        string key,
        string metric,
        decimal quantity,
        int minute) =>
        new(
            Guid.CreateVersion7(),
            world.Project,
            metric,
            quantity,
            Period,
            key,
            new DateTimeOffset(2026, 10, 20, 8, minute, 0, TimeSpan.Zero));

    /// <summary>A correction of <paramref name="originalId"/>: negative quantity, and its reason.</summary>
    private static LedgerSeed Correction(
        UsageReadsWorld world,
        string key,
        Guid originalId,
        decimal quantity,
        int minute) =>
        Ledger(world, key, "artifact_gb_month", quantity, minute) with
        {
            AdjustmentOf = originalId,
            Reason = "duplicate row withdrawn",
        };

    /// <summary>One usage row of the org itself: a metric no project owns.</summary>
    private static LedgerSeed OrgLevel(UsageReadsWorld world, string key, decimal quantity, int minute) =>
        Ledger(world, key, "support_hours", quantity, minute) with { ProjectId = null };

    /// <summary>One healing attempt of <see cref="Period"/>, on <paramref name="day"/> of 2026-10 at that minute.</summary>
    private static HealingCallSeed Healing(
        int tokens,
        string status,
        int day,
        int minute) =>
        new(
            Guid.CreateVersion7(),
            tokens,
            status,
            new DateTimeOffset(2026, 10, day, 8, minute, 0, TimeSpan.Zero));

    private static void AssertAvailable(
        ApiAnswer answer,
        string line,
        decimal quantity,
        string unit,
        string source)
    {
        var element = Line(answer, line);
        element.GetProperty("quantity").GetDecimal().ShouldBe(quantity);
        element.GetProperty("unit").GetString().ShouldBe(unit);
        element.GetProperty("source").GetString().ShouldBe(source);
        element.GetProperty("unavailable_reason").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static string? LineOf(JsonElement line) => line.GetProperty("line").GetString();

    /// <summary>The one line of <paramref name="answer"/> named <paramref name="name"/>.</summary>
    private static JsonElement Line(ApiAnswer answer, string name) =>
        Lines(answer).Single(line => string.Equals(LineOf(line), name, StringComparison.Ordinal));

    private static IReadOnlyList<JsonElement> Lines(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("lines").EnumerateArray()];

    private static JsonElement Detail(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0];

    /// <summary>The audited action of one cost read.</summary>
    private static string Action => "usage.cost_per_tenant_read";
}
