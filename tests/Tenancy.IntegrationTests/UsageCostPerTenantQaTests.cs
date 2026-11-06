using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests.CostBoundaries;

[Trait("Category", "Docker")]
public sealed class UsageCostPerTenantTests(PostgresTenancyFixture fixture)
{
    private const string Period = "2026-10";
    private const string Action = "usage.cost_per_tenant_read";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset October = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("abc")]
    [InlineData("2026-11 ")]
    [InlineData("0000-01")]
    [InlineData("0000-12")]
    [InlineData("2026-00")]
    [InlineData("2026-10\n")]
    [InlineData("２０２６-10")]
    [InlineData("")]
    public async Task CostPerTenant_InvalidCalendarMonth_RefusesWithoutAuditAndAcceptsCanonicalMonth(string period)
    {
        await using var world = await StartAsync();
        var refused = await world.GetAsync(world.CostUrl($"period={Uri.EscapeDataString(period)}"));

        AssertRefused(refused, "format");
        (await world.AuditCountAsync(Action)).ShouldBe(0L);
        (await world.GetAsync(world.CostUrl($"period={Period}"))).Status.ShouldBe(HttpStatusCode.OK);
        (await world.AuditCountAsync(Action)).ShouldBe(1L);
    }

    [Theory]
    [InlineData("2026-11")]
    [InlineData("2026-12")]
    [InlineData("2027-01")]
    [InlineData("9999-01")]
    [InlineData("9999-12")]
    public async Task CostPerTenant_CurrentOrFutureMonth_RefusesWithoutAuditAndAcceptsClosedMonth(string period)
    {
        await using var world = await StartAsync();
        var refused = await world.GetAsync(world.CostUrl($"period={period}"));

        AssertRefused(refused, "open_period");
        (await world.AuditCountAsync(Action)).ShouldBe(0L);
        (await world.GetAsync(world.CostUrl($"period={Period}"))).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("0001-01")]
    [InlineData("2025-12")]
    public async Task CostPerTenant_EmptyClosedMonth_ReturnsFiveNullLinesWithSourcesAndReasons(string period)
    {
        await using var world = await StartAsync();
        var answer = await world.GetAsync(world.CostUrl($"period={period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("period").GetString().ShouldBe(period);
        var lines = answer.Body.GetProperty("lines").EnumerateArray().ToArray();
        lines.Select(line => line.GetProperty("line").GetString()).ShouldBe(
            ["api_db_share", "object_gb_month", "egress_gb", "ai_tokens", "support_hours"]);
        foreach (var line in lines)
        {
            line.GetProperty("quantity").ValueKind.ShouldBe(JsonValueKind.Null);
            line.GetProperty("source").GetString().ShouldNotBeNullOrWhiteSpace();
            line.GetProperty("unavailable_reason").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        var audit = await world.AuditRowsAsync(Action);
        audit.Count.ShouldBe(1);
        using var metadata = JsonDocument.Parse(audit[0].Split('|', 5)[4]);
        metadata.RootElement.GetProperty("period").GetString().ShouldBe(period);
        metadata.RootElement.GetProperty("count").GetString().ShouldBe("0");
    }

    [Fact]
    public async Task CostPerTenant_ForeignLedgerAndHealing_DoNotSuppressOrInflateOwnHealingFallback()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "foreign-tokens", "ai_tokens", 999m) with { Org = otherOrg, ProjectId = otherProject },
            Ledger(world, "foreign-storage", "artifact_gb_month", 50m) with { Org = otherOrg, ProjectId = otherProject },
            Ledger(world, "foreign-egress", "egress_gb", 60m) with { Org = otherOrg, ProjectId = otherProject },
            Ledger(world, "foreign-support", "support_hours", 70m) with { Org = otherOrg, ProjectId = null });
        await world.SeedHealingCallsAsync(
            Healing(12),
            Healing(900) with { Org = otherOrg, ProjectId = otherProject });

        var own = await world.GetAsync(world.CostUrl($"period={Period}"));
        own.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(own, "ai_tokens", 12m, "healing_llm_call.tokens");
        foreach (var name in new[] { "api_db_share", "object_gb_month", "egress_gb", "support_hours" })
        {
            Line(own, name).GetProperty("quantity").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        var other = await world.GetAsync(UsageReadsWorld.CostUrlFor(otherOrg, $"period={Period}"));
        other.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(other, "ai_tokens", 999m, "usage_ledger_entry:ai_tokens");
        AssertAvailable(other, "object_gb_month", 50m, "usage_ledger_entry:artifact_gb_month");
        AssertAvailable(other, "egress_gb", 60m, "usage_ledger_entry:egress_gb");
        AssertAvailable(other, "support_hours", 70m, "usage_ledger_entry:support_hours");
        (await world.AuditCountAsync(Action)).ShouldBe(1L);
        (await world.AuditCountForAsync(otherOrg, Action)).ShouldBe(1L);
    }

    [Fact]
    public async Task CostPerTenant_ForeignHealingOnly_IsVisibleOnlyWhenReadingThatOrganization()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await world.SeedHealingCallsAsync(Healing(500) with { Org = otherOrg, ProjectId = otherProject });

        var own = await world.GetAsync(world.CostUrl($"period={Period}"));
        own.Status.ShouldBe(HttpStatusCode.OK);
        Line(own, "ai_tokens").GetProperty("quantity").ValueKind.ShouldBe(JsonValueKind.Null);
        var other = await world.GetAsync(UsageReadsWorld.CostUrlFor(otherOrg, $"period={Period}"));
        other.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(other, "ai_tokens", 500m, "healing_llm_call.tokens");
    }

    [Theory]
    [InlineData("artifact_gb_month", "object_gb_month")]
    [InlineData("egress_gb", "egress_gb")]
    [InlineData("ai_tokens", "ai_tokens")]
    [InlineData("support_hours", "support_hours")]
    public async Task CostPerTenant_CorrectionOnlyMonth_ReportsItsCreditWithoutOriginalMonthUsage(
        string metric, string line)
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "earlier-original", metric, 10m) with { Period = "2026-09" };
        var correction = Ledger(world, "current-credit", metric, -1.25m) with
        {
            AdjustmentOf = original.Id,
            Reason = "overcount corrected in next month",
        };
        await world.SeedLedgerAsync(original, correction);
        await world.SeedHealingCallsAsync(Healing(500));

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(answer, line, -1.25m, $"usage_ledger_entry:{metric}");
        var earlier = await world.GetAsync(world.CostUrl("period=2026-09"));
        earlier.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(earlier, line, 10m, $"usage_ledger_entry:{metric}");
    }

    [Fact]
    public async Task CostPerTenant_ZeroNetTokenLedger_PrefersMeasuredZeroOverNonzeroHealing()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "token-original", "ai_tokens", 9m);
        await world.SeedLedgerAsync(original, Ledger(world, "token-credit", "ai_tokens", -9m) with
        {
            AdjustmentOf = original.Id,
            Reason = "duplicate token usage withdrawn",
        });
        await world.SeedHealingCallsAsync(Healing(999));

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(answer, "ai_tokens", 0m, "usage_ledger_entry:ai_tokens");
        var audit = await world.AuditRowsAsync(Action);
        audit.Count.ShouldBe(1);
        var parts = audit[0].Split('|', 5);
        parts.Take(4).ShouldBe(["platform_operator", UsageReadsWorld.OperatorId.ToString("D"),
            "usage_ledger_entry", world.Org.ToString("D")]);
        using var metadata = JsonDocument.Parse(parts[4]);
        metadata.RootElement.GetProperty("period").GetString().ShouldBe(Period);
        metadata.RootElement.GetProperty("count").GetString().ShouldBe("1");
        metadata.RootElement.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["period", "count"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(0, 0L)]
    [InlineData(int.MaxValue, 4294967294L)]
    public async Task CostPerTenant_HealingTokens_ReportsMeasuredZeroOrSumBeyondInt32(int each, long expected)
    {
        await using var world = await StartAsync();
        await world.SeedHealingCallsAsync(Healing(each), Healing(each) with { Status = "error" });

        var answer = await world.GetAsync(world.CostUrl($"period={Period}"));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        AssertAvailable(answer, "ai_tokens", expected, "healing_llm_call.tokens");
    }

    [Fact]
    public async Task CostPerTenant_LargePeriod_UsesOneAggregateStatementAndReturnsIdenticalReads()
    {
        await using var world = await StartAsync();
        await world.SeedManyLedgerRowsAsync(10_001, Period, "artifact_gb_month", 0.5m);
        await world.SeedHealingCallsAsync(Healing(12));
        var before = world.StatementsTouching("public.usage_ledger_entry", "public.healing_llm_call").Count;

        var first = await world.GetTextAsync(world.CostUrl($"period={Period}"));
        first.Status.ShouldBe(HttpStatusCode.OK);
        var after = world.StatementsTouching("public.usage_ledger_entry", "public.healing_llm_call").Count;
        (after - before).ShouldBe(1);
        using var document = JsonDocument.Parse(first.Text);
        document.RootElement.GetProperty("lines").EnumerateArray()
            .Single(line => line.GetProperty("line").GetString() == "object_gb_month")
            .GetProperty("quantity").GetDecimal().ShouldBe(5000.5m);
        var second = await world.GetTextAsync(world.CostUrl($"period={Period}"));
        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Text.ShouldBe(first.Text);
        (await world.AuditCountAsync(Action)).ShouldBe(2L);
    }

    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(fixture, OperatorGateMode.Permitted, new FakeTimeProvider(Now));

    private static LedgerSeed Ledger(UsageReadsWorld world, string key, string metric, decimal quantity) =>
        new(Guid.CreateVersion7(), world.Project, metric, quantity, Period, key, October);

    private static HealingCallSeed Healing(int tokens) => new(Guid.CreateVersion7(), tokens, "ok", October);

    private static JsonElement Line(ApiAnswer answer, string name) =>
        answer.Body.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("line").GetString() == name);

    private static void AssertAvailable(ApiAnswer answer, string name, decimal quantity, string source)
    {
        var line = Line(answer, name);
        line.GetProperty("quantity").GetDecimal().ShouldBe(quantity);
        line.GetProperty("source").GetString().ShouldBe(source);
        line.GetProperty("unavailable_reason").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static void AssertRefused(ApiAnswer answer, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("period");
        detail.GetProperty("reason").GetString().ShouldBe(reason);
    }
}
