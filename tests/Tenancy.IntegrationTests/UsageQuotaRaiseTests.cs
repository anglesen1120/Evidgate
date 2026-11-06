// 12.3.1-T4: raising one scope's quota limit against a real PostgreSQL — the audited operator ingress route and the
// in-process command that share one writer, the single upsert that creates a missing counter, the strict "greater than
// the stored limit" rule that keeps a raise from lowering a limit below usage, the block a raise lifts, the one audit row
// carrying the operator and the reason, the permission floor, and the tenant scope the raise can never leave.
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Application.Commands;
using Evidgate.Modules.Usage.Application.Quota;
using Evidgate.Modules.Usage.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 12.3.1 §5: an operator raises one scope's limit with a reason, the counter it left behind answers the caller, and the
/// audit chain says who raised it and why. A limit at or below the stored one is refused, so no path of this module can
/// move a limit under the usage a period already holds.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageQuotaRaiseTests
{
    private const string Period = "2026-10";
    private const string Action = "usage.quota_raise";
    private const string Reason = "pilot extension for the onboarding week";

    private readonly PostgresTenancyFixture _fixture;

    public UsageQuotaRaiseTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static RaiseQuotaLimit Raise(
        UsageQuotaWorld world,
        decimal newLimit,
        string? reason = Reason,
        Guid? project = null,
        string period = Period) =>
        new(project ?? world.Project, period, UsageMetric.Results, newLimit, reason);

    /// <summary>Dispatches the command through the real Mediator pipeline.</summary>
    /// <param name="world">The world.</param>
    /// <param name="command">The raise.</param>
    /// <param name="host">The host to dispatch on, or null for the permissive one.</param>
    /// <param name="member">A member caller, or null for the service principal claiming <c>billing:write</c>.</param>
    private static async Task<Result<QuotaRaiseResult>> SendAsync(
        UsageQuotaWorld world,
        RaiseQuotaLimit command,
        TenancyApiFactory? host = null,
        Guid? member = null)
    {
        await using var services = (host ?? world.Commands).Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, null);
        using var principal = member is { } user
            ? provider.GetRequiredService<IRequestPrincipalSetter>().Publish(RequestPrincipal.Member(user))
            : provider.GetRequiredService<IRequestPrincipalSetter>().Publish(
                RequestPrincipal.Service(world.Project, [Permissions.Billing.Write]));

        return await provider.GetRequiredService<IMediator>().Send(command, TestContext.Current.CancellationToken);
    }

    private static Task<Guid> SeedAsync(
        UsageQuotaWorld world,
        decimal used,
        decimal limit,
        DateTimeOffset? warnedAt = null,
        DateTimeOffset? blockedAt = null) =>
        world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, Period, "results", used, limit, 0.8m, warnedAt, blockedAt));

    // ------------------------------------------------------------------ the audited raise

    [Fact]
    public async Task RaiseQuotaLimit_BlockedScope_RaisesAndClearsBlock_WritesOneAuditRow()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var warnedAt = new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);
        var counterId = await SeedAsync(world, used: 10m, limit: 10m, warnedAt: warnedAt, blockedAt: warnedAt);

        var raised = await SendAsync(world, Raise(world, 20m));

        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        raised.Value.Limit.ShouldBe(20m);
        raised.Value.Used.ShouldBe(10m, "a raise never spends or refunds usage");
        raised.Value.CounterId.ShouldBe(counterId);

        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Limit.ShouldBe(20m);
        stored.Used.ShouldBe(10m);
        stored.BlockedAt.ShouldBeNull("the new limit is above the usage, so the block is lifted");
        stored.WarnedAt.ShouldBe(warnedAt, "a scope that once warned still says so");

        var rows = await world.AuditRowsAsync(null, Action);
        rows.Count.ShouldBe(1);
        var row = rows[0];
        row.ShouldContain($"{counterId:D}");
        row.ShouldContain("|quota_counter|");
        row.ShouldContain($"\"reason\": \"{Reason}\"");
        row.ShouldContain("\"limit\": \"20\"");
        row.ShouldContain("\"outcome\": \"raised\"");
    }

    [Fact]
    public async Task RaiseQuotaLimit_NoCounter_CreatesRowWithZeroUsed()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        (await world.CounterCountAsync()).ShouldBe(0L);

        var raised = await SendAsync(world, Raise(world, 15m));

        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        raised.Value.Limit.ShouldBe(15m);
        raised.Value.Used.ShouldBe(0m, "a scope that never used its quota starts at zero");

        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Limit.ShouldBe(15m);
        stored.Used.ShouldBe(0m);
        (await world.CounterCountAsync()).ShouldBe(1L);
        (await world.AuditCountAsync(null, Action)).ShouldBe(1L);
    }

    [Fact]
    public async Task RaiseQuotaLimit_BelowOrEqualCurrentLimit_422()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 10m, limit: 20m);

        var equal = await SendAsync(world, Raise(world, 20m));
        var lower = await SendAsync(world, Raise(world, 15m));

        var equalError = equal.Error.ShouldNotBeNull();
        equalError.Code.ShouldBe("request_invalid");
        equalError.Details.ShouldNotBeNull().ShouldContain(
            detail => detail.Path == "new_limit" && detail.Reason == "limit_not_increased");
        lower.Error.ShouldNotBeNull().Code.ShouldBe("request_invalid", "a raise can never lower a limit below the usage");

        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Limit.ShouldBe(20m);
        (await world.AuditCountAsync(null, Action)).ShouldBe(0L, "a refused raise writes no audit row");
    }

    [Fact]
    public async Task RaiseQuotaLimit_MissingReason_422()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);

        var refused = await SendAsync(world, Raise(world, 20m, reason: "   "));

        var error = refused.Error.ShouldNotBeNull();
        error.Code.ShouldBe("request_invalid");
        error.Details.ShouldNotBeNull().ShouldContain(
            detail => detail.Path == "reason" && detail.Reason == "required");
        (await world.CounterCountAsync()).ShouldBe(0L, "an invalid raise writes nothing");
    }

    [Fact]
    public async Task RaiseQuotaLimit_WithoutBillingWrite_Refused()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 5m, limit: 10m);

        // `billing:write` is staff-only, so no tenant role holds it: the founder's real evaluator refuses the
        // dispatch, and the same plain member on the permissive host is the positive control.
        await using var evaluator = TenancyApiFactory.RealEvaluator(_fixture.AppConnectionString(world.Database));
        var refused = await SendAsync(world, Raise(world, 20m), host: evaluator, member: world.User);

        refused.Error.ShouldNotBeNull().Code.ShouldBe("forbidden");
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Limit.ShouldBe(10m);
        (await world.AuditCountAsync(null, Action)).ShouldBe(0L);

        var allowed = await SendAsync(world, Raise(world, 20m), member: world.User);
        allowed.IsSuccess.ShouldBeTrue(allowed.Error?.Message);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Limit.ShouldBe(20m);
    }

    // ------------------------------------------------------------------ the operator ingress route

    [Fact]
    public async Task RaiseQuotaLimit_OperatorRoute_RaisesAndAuditsTheOperator()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var counterId = await SeedAsync(world, used: 10m, limit: 10m, blockedAt: DateTimeOffset.UnixEpoch);

        var answer = await world.PostRaiseAsync(new
        {
            project_id = world.Project,
            period = Period,
            metric = "results",
            new_limit = 30m,
            reason = Reason,
        });

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("limit").GetDecimal().ShouldBe(30m);
        answer.Body.GetProperty("used").GetDecimal().ShouldBe(10m);
        answer.Body.GetProperty("metric").GetString().ShouldBe("results");
        answer.Body.GetProperty("period").GetString().ShouldBe(Period);
        answer.Body.GetProperty("counter_id").GetGuid().ShouldBe(counterId);

        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Limit.ShouldBe(30m);
        stored.BlockedAt.ShouldBeNull();

        var rows = await world.AuditRowsAsync(null, Action);
        rows.Count.ShouldBe(1);
        rows[0].ShouldContain(UsageQuotaWorld.OperatorId.ToString("D"));
        rows[0].ShouldContain($"\"reason\": \"{Reason}\"");
    }

    [Fact]
    public async Task RaiseQuotaLimit_OperatorRoute_NonOperatorRefused()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, OperatorGateMode.Refused);
        await SeedAsync(world, used: 10m, limit: 10m);

        var answer = await world.PostRaiseAsync(new
        {
            project_id = world.Project,
            period = Period,
            metric = "results",
            new_limit = 30m,
            reason = Reason,
        });

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Limit.ShouldBe(10m);
        (await world.AuditCountAsync(null, Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task RaiseQuotaLimit_ForOrgX_CannotTouchOrgYCounter()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await SeedAsync(world, used: 10m, limit: 10m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), otherProject, Period, "results", 5m, 10m, 0.8m, Org: otherOrg));

        var raised = await world.PostRaiseAsync(new
        {
            project_id = world.Project,
            period = Period,
            metric = "results",
            new_limit = 40m,
            reason = Reason,
        });
        raised.Status.ShouldBe(HttpStatusCode.OK);

        var untouched = (await world.CounterAsync(org: otherOrg, project: otherProject)).ShouldNotBeNull();
        untouched.Limit.ShouldBe(10m);
        untouched.Used.ShouldBe(5m);
        untouched.BlockedAt.ShouldBeNull();

        // Naming the other org's project from this org's scope is a refusal, not a write somewhere else.
        var crossed = await world.PostRaiseAsync(new
        {
            project_id = otherProject,
            period = Period,
            metric = "results",
            new_limit = 40m,
            reason = Reason,
        });
        crossed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        crossed.ErrorCode.ShouldBe("request_invalid");

        (await world.CounterAsync(org: otherOrg, project: otherProject)).ShouldNotBeNull().Limit.ShouldBe(10m);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Limit.ShouldBe(40m);
    }

    // ------------------------------------------------------------------ a raise racing the gate

    [Fact]
    public async Task RaiseQuotaLimit_RacingACheck_LeavesUsedWithinNewLimit()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await SeedAsync(world, used: 10m, limit: 10m, blockedAt: DateTimeOffset.UnixEpoch);

        var checks = Enumerable.Range(0, 20)
            .Select(_ => world.CheckAsync(
                new QuotaCheckRequest(UsageMetric.Results, Period, world.Project, 1m)))
            .ToArray();
        var raise = SendAsync(world, Raise(world, 20m));
        var answers = await Task.WhenAll(checks);
        var raised = await raise;

        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        answers.Length.ShouldBe(20);

        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Limit.ShouldBe(20m);
        stored.Used.ShouldBeGreaterThanOrEqualTo(10m);
        stored.Used.ShouldBeLessThanOrEqualTo(stored.Limit);
        stored.Used.ShouldBeLessThanOrEqualTo(20m);

        var granted = answers.Count(answer => answer.Decision != QuotaDecision.Block);
        ((decimal)granted).ShouldBe(stored.Used - 10m, "every granted unit is counted exactly once");
    }
}
