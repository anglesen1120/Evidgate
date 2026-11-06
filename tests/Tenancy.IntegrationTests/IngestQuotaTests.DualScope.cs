// The events route's quota admission when the organization's counter caps the work as well: an org-wide limit that
// binds one project's batch, two projects sharing it, a refusal that charges neither scope, and the refusal stamp the
// rolled-back batch would otherwise carry away.
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
/// The transport half of the two-scope rule: the batch is refused when either the project's counter or the
/// organization's has no room, both counters keep what they held, and the first refusal leaves a durable stamp even
/// though the batch that earned it rolled back. The single-scope route cases live beside this file.
/// </summary>
public sealed partial class IngestQuotaTests
{
    private const string RaiseReason = "additional capacity for the pilot";

    private static Task<Guid> SeedTenantAsync(
        UsageQuotaWorld counters,
        IngestTestWorld world,
        decimal used,
        decimal limit) =>
        counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), ProjectId: null, Period(world), "results", used, limit, WarnRatio, Org: world.Org));

    private static Task<CounterState?> ReadTenantAsync(UsageQuotaWorld counters, IngestTestWorld world) =>
        counters.CounterAsync(org: world.Org, project: null, period: Period(world), metric: "results");

    [Fact]
    public async Task IngestEvents_TenantCapBelowProjectCap_Returns429()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        // The project has room for the two events; the organization's counter has room for one.
        await SeedAsync(counters, world, used: 0m, limit: 100m);
        await SeedTenantAsync(counters, world, used: 9m, limit: 10m);

        var refused = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("quota_exceeded");

        (await EventRowsAsync(world)).ShouldBe(0, "the refused batch commits no event");
        (await BatchRowsAsync(world)).ShouldBe(0);

        // The positive control: one unit fits the organization's counter, and both scopes are charged for it.
        var fits = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L));

        fits.Status.ShouldBe(HttpStatusCode.OK, fits.ErrorMessage);
        (await ReadTenantAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(10m);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(1m);
    }

    [Fact]
    public async Task IngestEvents_ProjectCapBelowTenantCap_Returns429()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        await SeedAsync(counters, world, used: 9m, limit: 10m);
        await SeedTenantAsync(counters, world, used: 0m, limit: 100m);

        var refused = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("quota_exceeded");

        var project = (await ReadAsync(counters, world)).ShouldNotBeNull();
        var tenant = (await ReadTenantAsync(counters, world)).ShouldNotBeNull();
        project.Used.ShouldBe(9m, "the refused batch is never counted in the scope that refused it");
        project.BlockedAt.ShouldNotBeNull("and the refused scope keeps the stamp the block earned");
        tenant.Used.ShouldBe(0m, "nor in the scope that had room");
        tenant.BlockedAt.ShouldBeNull();
        (await EventRowsAsync(world)).ShouldBe(0);

        var fits = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L));

        fits.Status.ShouldBe(HttpStatusCode.OK, fits.ErrorMessage);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(10m);
        (await ReadTenantAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(1m);
    }

    [Fact]
    public async Task IngestEvents_TenantCapShared_AcrossTwoProjects()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        await SeedAsync(counters, world, used: 0m, limit: 100m);
        await SeedTenantAsync(counters, world, used: 0m, limit: 3m);

        // A second project of the same org, with its own run and its own producer token: the org-wide counter is what
        // both projects' batches have to fit in.
        var (secondProject, secondPlan) = await SeedSecondProjectAsync(world, counters, "shared-cap");

        var (otherOrg, otherProject) = await world.CreateOtherOrgAsync(Unique.Slug("quota-foreign"));
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), otherProject, Period(world), "results", 1m, 1m, WarnRatio, Org: otherOrg));
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), ProjectId: null, Period(world), "results", 1m, 1m, WarnRatio, Org: otherOrg));
        var foreignProject = (await counters.CounterAsync(otherOrg, otherProject, Period(world))).ShouldNotBeNull();
        var foreignTenant = (await counters.CounterAsync(otherOrg, project: null, Period(world))).ShouldNotBeNull();

        await using var secondHost = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            secondProject,
            IngestTestWorld.Writer(projectScope: secondProject));
        using var secondClient = secondHost.CreateClient();

        var firstProject = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));
        firstProject.Status.ShouldBe(HttpStatusCode.OK, firstProject.ErrorMessage);

        var shared = await IngestQaRequests.PostEventsAsync(
            secondClient,
            secondPlan.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(
                    Guid.CreateVersion7(), secondPlan.Executions[0].ExecutionKey, 1, type: "test.step", status: null),
                IngestQaRequests.Event(
                    Guid.CreateVersion7(), secondPlan.Executions[0].ExecutionKey, 2, type: "test.step", status: null)));

        shared.Status.ShouldBe(
            HttpStatusCode.TooManyRequests, "the second project's two units do not fit the counter the first project spent");
        shared.ErrorCode.ShouldBe("quota_exceeded");

        (await ReadTenantAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(
            2m, "the organization's counter holds only the units it admitted");
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(2m);
        (await counters.CounterAsync(world.Org, secondProject, Period(world))).ShouldNotBeNull().Used.ShouldBe(
            0m, "and the refused project is not charged for the batch it never committed");

        (await counters.CounterAsync(otherOrg, otherProject, Period(world))).ShouldBe(foreignProject);
        (await counters.CounterAsync(otherOrg, project: null, Period(world))).ShouldBe(foreignTenant);
    }

    /// <summary>
    /// Another project of this org with a run of its own: the run gets a producer slot, because the events route binds
    /// one before it reads any counter and a run without one answers <c>422 unknown_producer</c>.
    /// </summary>
    /// <param name="world">The world whose org the project joins.</param>
    /// <param name="counters">The quota world that seeds the project's own counter.</param>
    /// <param name="slug">The project's slug suffix.</param>
    private async Task<(Guid Project, PlannedExecutionSeeder.SeededRun Plan)> SeedSecondProjectAsync(
        IngestTestWorld world,
        UsageQuotaWorld counters,
        string slug)
    {
        using var setup = world.Api.CreateClient();
        var project = await RegistryApi.CreateProjectAsync(setup, world.User, world.Org, Unique.Slug(slug));
        var plan = await world.SeedRunInProjectAsync(project);

        await IngestTestWorld.SeedSlotsAsync(
            _fixture, world.Database, world.Org, plan.RunId, count: 1, boundJti: null);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), project, Period(world), "results", 0m, 100m, WarnRatio, Org: world.Org));

        return (project, plan);
    }

    [Fact]
    public async Task IngestEvents_FirstQuotaBlock_StampsBlockedAtDurably()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 10m, limit: 10m);

        var refused = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L));

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);

        // The batch rolled back, so the gate's own stamp went with it: what survives is the stamp the route wrote
        // afterwards, in a scope of its own, and nothing else of the batch.
        var counter = (await ReadAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(10m);
        counter.BlockedAt.ShouldNotBeNull("the first refusal is what an operator reads after the rollback");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("outbox", $"org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task IngestEvents_FirstBatchOfANewPeriod_StampsTheCarriedCounter()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        // The period has no counter yet: its limit is the previous month's, and that limit has room for one of the
        // batch's two units.
        await SeedTenantAsync(counters, world, used: 0m, limit: 100m);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, PreviousMonth(world), "results", 0m, 1m, WarnRatio,
            Org: world.Org));

        var refused = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L));

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("quota_exceeded");

        // The batch rolled back, and the counter the gate stamped rolled back with it: the route's own stamp creates
        // the carried counter and stamps that one, or no operator would ever see this block.
        var carried = (await ReadAsync(counters, world)).ShouldNotBeNull();
        carried.Used.ShouldBe(0m, "the refused batch charged nothing");
        carried.Limit.ShouldBe(1m, "the carried limit is the previous month's");
        carried.BlockedAt.ShouldNotBeNull("the refusal is stamped on the counter the stamp created");
        (await ReadTenantAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(0m, "nor did it charge the tenant");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task IngestEvents_RepeatedBlock_DoesNotRewriteTheStamp()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 10m, limit: 10m);

        (await PostBatchAsync(world, (Guid.CreateVersion7(), 1L))).Status.ShouldBe(HttpStatusCode.TooManyRequests);
        var stamped = (await ReadAsync(counters, world)).ShouldNotBeNull();
        var before = await CounterXminAsync(world);

        var again = await PostBatchAsync(world, (Guid.CreateVersion7(), 2L));

        again.Status.ShouldBe(HttpStatusCode.TooManyRequests, again.ErrorMessage);
        (await ReadAsync(counters, world)).ShouldBe(
            stamped, "a refusal whose stamp already exists writes nothing, so it cannot bump the version");
        (await CounterXminAsync(world)).ShouldBe(
            before, "and the stamp statement leaves an already-stamped counter unlocked and unwritten");
    }

    /// <summary>The counter's tuple version: an update writes a new tuple with a new <c>xmin</c>, so an equal value proves no write.</summary>
    private static Task<string> CounterXminAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>(
            $"select xmin::text from public.quota_counter where org_id = '{world.Org:D}' "
            + $"and project_id = '{world.Project:D}' and period = '{Period(world)}' and metric = 'results'");

    [Fact]
    public async Task IngestEvents_BlockedByTenantScope_StampsTheTenantCounter()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 0m, limit: 100m);
        await SeedTenantAsync(counters, world, used: 10m, limit: 10m);

        var refused = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L));

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);

        var tenant = (await ReadTenantAsync(counters, world)).ShouldNotBeNull();
        tenant.Used.ShouldBe(10m);
        tenant.BlockedAt.ShouldNotBeNull("the organization counter is the one that refused, so it carries the stamp");
        var project = (await ReadAsync(counters, world)).ShouldNotBeNull();
        project.Used.ShouldBe(0m);
        project.BlockedAt.ShouldBeNull("a counter with room is never stamped");
    }

    [Fact]
    public async Task IngestEvents_StampFailure_Still429()
    {
        await using var world = await StartIngestAsync(
            services: services => services.AddSingleton<IQuotaBlockStamp>(new ThrowingQuotaBlockStamp()));
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 10m, limit: 10m);

        var refused = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L));

        // The stamp is diagnostics of a refusal that already happened: losing it must not turn the 429 into a 500,
        // and it must not admit the batch either.
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("quota_exceeded");
        (await ReadAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldBeNull(
            "the broken seam is the only thing the stamp would have written");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task IngestEvents_RaiseClearsBlockAndAdmitsAgain()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 10m, limit: 10m);

        (await PostBatchAsync(world, (Guid.CreateVersion7(), 1L))).Status.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldNotBeNull();

        var raised = await RaiseAsync(world, limit: 20m);

        raised.IsSuccess.ShouldBeTrue($"the operator raise must apply: {raised.Error?.Message}");
        (await ReadAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldBeNull(
            "the raise is the one transition that clears the stamp");

        var admitted = await PostBatchAsync(world, (Guid.CreateVersion7(), 2L));

        admitted.Status.ShouldBe(HttpStatusCode.OK, admitted.ErrorMessage);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(11m);
    }

    /// <summary>The audited operator raise, run in process against the world's own composition.</summary>
    private static async Task<Result<QuotaRaiseResult>> RaiseAsync(IngestTestWorld world, decimal limit)
    {
        await using var services = world.Host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        // The addressed project must be the principal's own scope: the evaluator denies a service principal whose
        // project scope differs from the open tenant context's project.
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, world.Project);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Service(world.Project, [Permissions.Billing.Write]));

        return await provider.GetRequiredService<IMediator>().Send(
            new RaiseQuotaLimit(world.Project, Period(world), UsageMetric.Results, limit, RaiseReason),
            TestContext.Current.CancellationToken);
    }
}

/// <summary>A block stamp that cannot write, so the route's best-effort path is what the test observes.</summary>
internal sealed class ThrowingQuotaBlockStamp : IQuotaBlockStamp
{
    /// <inheritdoc />
    public Task StampAsync(Guid orgId, QuotaCheckRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the block stamp could not write");
}
