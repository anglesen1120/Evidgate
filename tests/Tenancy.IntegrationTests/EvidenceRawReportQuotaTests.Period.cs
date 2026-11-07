// The quota month itself: the counter rows a period owns, how the month the gate charges is resolved, the audited
// operator raise that lifts a block, and the organization-wide counter the report path charges when the project has none.
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Usage.Application.Commands;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Application.Quota;
using Evidgate.Modules.Usage.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Fact]
    public async Task The_Organization_Counter_Blocks_And_Is_The_One_Charged_And_Stamped()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), ProjectId: null, await PeriodAsync(world), "results", 1m, 1m, WarnRatio, Org: world.Org));

        // Only the organization's counter exists: the project has no limit, so the batch is admitted or refused by the
        // org-wide row alone and charged there alone.
        var blocked = await ParseAsync(world, artifactId);
        blocked.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);

        var tenant = (await ReadTenantCounterAsync(counters, world)).ShouldNotBeNull();
        tenant.Used.ShouldBe(1m, "the refused batch charges nothing");
        tenant.BlockedAt.ShouldNotBeNull("the org counter is the one that refused");

        // An operator raise of the org limit admits the same payload, and the charge lands on that counter once.
        var raised = await RaiseAsync(counters, world, project: null, limit: 10m);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);

        var admitted = await ParseAsync(world, artifactId);

        admitted.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        admitted.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await ReadTenantCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(2m);
    }

    [Fact]
    public async Task An_Operator_Raise_Admits_The_Blocked_Report_And_Charges_It_Once()
    {
        var world = await StartAsync();
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);

        (await ParseAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldNotBeNull();
        var artifactAfterBlock = await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where org_id = '{world.Org:D}' and id = '{artifactId:D}'");
        artifactAfterBlock.ShouldBe(1, "a block writes nothing, not even an artifact change");

        var raised = await RaiseAsync(counters, world, world.Project, limit: 20m);

        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);

        var admitted = await ParseAsync(world, artifactId);

        admitted.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        admitted.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "event_batch")).ShouldBe(1);
        var counter = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(11m, "the blocked attempt charged nothing, so the admitted one charges exactly one unit");
        counter.BlockedAt.ShouldBeNull("the raise is the one transition that clears the stamp");
    }

    /// <summary>How many audit rows the tenant holds, so a stamp that writes none can be told from one that does.</summary>
    /// <param name="world">The attempt's world.</param>
    /// <returns>The audit count.</returns>
    private Task<long> AuditCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}'");

    /// <summary>
    /// The blocked stamp the usage summary reports for the run's project, read through the settings page's own query:
    /// an operator must see the pre-check's refusal there, not only in the counter row.
    /// </summary>
    /// <param name="counters">The counters world whose permissive host dispatches the query.</param>
    /// <param name="world">The attempt's world.</param>
    /// <returns>The project counter's <c>blocked_at</c>, or null when it is not blocked.</returns>
    private static async Task<DateTimeOffset?> SummaryBlockedAtAsync(UsageQuotaWorld counters, World world)
    {
        await using var services = counters.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, null);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Service(world.Project, [Permissions.Usage.Read]));

        var answer = await provider.GetRequiredService<IMediator>().Send(
            new GetUsageSummaryQuery(world.Org, world.Project, Cursor: null, Limit: 50),
            Token);
        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);

        return answer.Value.Metrics
            .Single(metric => string.Equals(metric.Metric, "results", StringComparison.Ordinal))
            .Projects
            .ShouldHaveSingleItem()
            .BlockedAt;
    }

    /// <summary>Seeds one project-scoped counter for the DB transaction's month, which is the month the gate charges.</summary>
    private async Task<Guid> SeedCounterAsync(UsageQuotaWorld counters, World world, decimal used, decimal limit) =>
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, await PeriodAsync(world), "results", used, limit, WarnRatio, Org: world.Org));

    private async Task<CounterState?> ReadCounterAsync(UsageQuotaWorld counters, World world) =>
        await counters.CounterAsync(org: world.Org, project: world.Project, period: await PeriodAsync(world), metric: "results");

    private async Task<CounterState?> ReadTenantCounterAsync(UsageQuotaWorld counters, World world) =>
        await counters.CounterAsync(org: world.Org, project: null, period: await PeriodAsync(world), metric: "results");

    /// <summary>
    /// The audited operator raise, run in process against the counters world's permissive host: the ingest suite's own
    /// seam, which also proves the raise clears the stamp a block left behind.
    /// </summary>
    private async Task<Result<QuotaRaiseResult>> RaiseAsync(
        UsageQuotaWorld counters,
        World world,
        Guid? project,
        decimal limit)
    {
        await using var services = counters.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, null);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Service(project, [Permissions.Billing.Write]));

        return await provider.GetRequiredService<IMediator>().Send(
            new RaiseQuotaLimit(project, await PeriodAsync(world), UsageMetric.Results, limit, RaiseReason),
            Token);
    }

    /// <summary>The month the gate charges: the database's UTC month, which the host's clock cannot move.</summary>
    private Task<string> PeriodAsync(World world) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            "select to_char(now() at time zone 'UTC', 'YYYY-MM')");
}
