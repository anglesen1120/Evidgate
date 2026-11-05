// A run create that names a release changes the very rows an explicit bind changes, so the two must leave the same
// evidence: one `release.bind` audit row about the release and one `orchestrator.release.bound` outbox event per
// applied binding, written in the run's own transaction. Nothing here trusts the response: it reads the stored rows.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    /// <summary>
    /// The run-create hook writes the binding's audit row and outbox event, and the run create's own row survives
    /// beside them: the chain names both actions, and the event carries the counts rather than the component keys.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_WritesTheSameAuditRowAndEventAsAnExplicitBind()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "run-create-evidence", "active", ("web", 'a'), ("api", 'b'));

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a'), ("api", 'b')), releaseId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' "
            + $"and action = 'release.bind' and object_type = 'release' and object_id = '{releaseId:D}'"))
            .ShouldBe(1, "the binding and its audit row are one write, whichever route made it");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'run.create'"))
            .ShouldBe(1, "the binding's row is an addition to the create's own, not a replacement");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select metadata ->> 'run_id' from public.audit_log where org_id = '{world.Org:D}' "
            + "and action = 'release.bind'"))
            .ShouldBe(runId.ToString("D"));

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' "
            + "and task_type = 'orchestrator.release.bound'"))
            .ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload ->> 'bound' from public.outbox where org_id = '{world.Org:D}' "
            + "and task_type = 'orchestrator.release.bound'"))
            .ShouldBe("2");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload ->> 'run_id' from public.outbox where org_id = '{world.Org:D}' "
            + "and task_type = 'orchestrator.release.bound'"))
            .ShouldBe(runId.ToString("D"));
    }

    /// <summary>
    /// A run that matched no component is not a binding: the create still succeeds, and neither a
    /// <c>release.bind</c> audit row nor a binding event exists, because no binding row was written.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_ZeroMatch_WritesNoBindEvidence()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "run-create-no-evidence", "active", ("web", 'a'));

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'e')), releaseId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (await BindingCountAsync(world)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'release.bind'"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' "
            + "and task_type = 'orchestrator.release.bound'"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'run.create'"))
            .ShouldBe(1, "the run itself was created and audited");
    }

    /// <summary>
    /// The partial case is the one that could get the counts wrong: the mismatched release components are counted
    /// in the event while only the matched keys reach the table, so a consumer reading the event learns the shape
    /// without learning a digest.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_PartialMatch_EventCarriesBothCounts()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(
            world, "run-create-evidence-partial", "active", ("web", 'a'), ("api", 'b'), ("worker", 'c'));

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a'), ("api", 'd')), releaseId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        (await BoundKeysOfAsync(world, releaseId, runId)).ShouldBe(["web"]);

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload ->> 'bound' from public.outbox where org_id = '{world.Org:D}' "
            + "and task_type = 'orchestrator.release.bound'"))
            .ShouldBe("1");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload ->> 'mismatched' from public.outbox where org_id = '{world.Org:D}' "
            + "and task_type = 'orchestrator.release.bound'"))
            .ShouldBe("2");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload::text from public.outbox where org_id = '{world.Org:D}' "
            + "and task_type = 'orchestrator.release.bound'"))
            .ShouldNotContain(Digest('a'), Case.Sensitive, "the event must not leak a digest");
    }

    /// <summary>
    /// The binding's audit row is buffered, not written, so the failure is aimed one statement later than the rollback
    /// case beside it: the audit flush runs before the commit, after both events were inserted. A throw there must leave
    /// no run, plan, slot, binding, idempotency record, audit row or event - the flush, the events and the business rows
    /// are one transaction.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_FailureFlushingTheAudit_RollsBackTheInsertedEventsToo()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(
            world, "run-create-audit-rollback", "active", ("web", 'a'), ("api", 'b'));
        await using var crashing = world.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITenantDb>();
            services.AddSingleton<TenantDb>();
            services.AddSingleton<ITenantDb>(provider =>
                new ThrowingTenantDb(provider.GetRequiredService<TenantDb>(), "insert into public.audit_log"));
        }));
        using var client = crashing.CreateClient();
        var key = NewKey();

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a'), ("api", 'b')), releaseId),
            client: client,
            idempotencyKey: key);

        created.Status.ShouldBe(HttpStatusCode.InternalServerError, created.ErrorMessage);
        await AssertNothingSurvivedAsync(world, key);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}'"))
            .ShouldBe(0, "both events were inserted before the flush failed, so the rollback must remove them");
    }
}
