// The run-create hook: `POST /v1/projects/{p}/runs` naming a `release_id` binds the keys whose digests match inside
// the same transaction as the run, and never fails the run when some or all components do not match.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    /// <summary>Every <c>release_run</c> row of the world's org.</summary>
    private async Task<long> BindingCountAsync(RunCreationTests.World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where org_id = '{world.Org:D}'").ConfigureAwait(false);

    [Fact]
    public async Task BindAtRunCreate_BindsEveryMatchingComponent()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "run-create-exact", "active", ("web", 'a'), ("api", 'b'));

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a'), ("api", 'b')), releaseId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        (await BoundKeysOfAsync(world, releaseId, runId)).ShouldBe(["api", "web"]);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task BindAtRunCreate_PartialMatch_CreatesTheRunAndBindsTheMatchedKeys()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(
            world, "run-create-partial", "active", ("web", 'a'), ("api", 'b'), ("worker", 'c'));

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a'), ("api", 'd')), releaseId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (await BoundKeysOfAsync(world, releaseId, created.Id("run_id"))).ShouldBe(["web"]);
        (await BindingCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task BindAtRunCreate_ZeroMatch_StillCreatesTheRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "run-create-none", "active", ("web", 'a'));

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'e')), releaseId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await BindingCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task BindAtRunCreate_DraftRelease_422ReleaseNotActive_NoRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "run-create-draft", state: null, ("web", 'a'));

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a')), releaseId));

        created.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, created.ErrorMessage);
        created.ErrorCode.ShouldBe("release_not_active");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
        (await BindingCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task BindAtRunCreate_UnknownAndForeignRelease_404NoRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var (foreignOrg, foreignProject, foreignRevision) = await SeedForeignTenantAsync(world.Database);
        var foreign = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.release (id, org_id, project_id, name, created_by)
            values ('{foreign:D}', '{foreignOrg:D}', '{foreignProject:D}', 'run-create-foreign', 'probe');
            insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id)
            values ('{Guid.CreateVersion7():D}', '{foreignOrg:D}', '{foreign:D}', 'web', '{Digest('a')}', '{foreignRevision:D}');
            update public.release set state = 'active' where id = '{foreign:D}';
            """);

        var unknown = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a')), Guid.CreateVersion7()));
        var invisible = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a')), foreign));

        foreach (var refused in new[] { unknown, invisible })
        {
            refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
            refused.ErrorCode.ShouldBe("not_found");
        }

        Envelope(unknown).ShouldBe(Envelope(invisible));
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
        (await BindingCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// A fork run's ref cannot be trusted, so a create that names a release with a fork identity is refused before the
    /// release is read or a row is written: 422 <c>fork_run_not_bindable</c>, no run, plan, slot, binding or outbox
    /// row. The same body without the fork flag binds, which is what makes the refusal the identity's.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_ForkIdentity_422NoRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "run-create-fork", "active", ("web", 'a'));
        var forkBody = "{\"ref\":{\"is_fork\":true},"
            + RunBodyWith(world.ManifestId, Components(("web", 'a')), releaseId).TrimStart('{');

        var forkKey = NewKey();
        var refused = await RunCreationTests.CreateRunAsync(
            world, forkBody, caller: UserCaller(world), idempotencyKey: forkKey);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("fork_run_not_bindable");
        await AssertNothingSurvivedAsync(world, forkKey);

        var control = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a')), releaseId),
            caller: UserCaller(world));

        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
        (await BindingCountAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// The caller whose identity comes from the body rather than a verified CI claim: a member of the org with no
    /// claim, so <c>ref.is_fork</c> is the recorded value.
    /// </summary>
    /// <param name="world">The suite's tenant.</param>
    private static Action<HttpRequestHeaders> UserCaller(RunCreationTests.World world) =>
        TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);

    /// <summary>
    /// A failure after the binding was written rolls everything back together: the hook runs once the run, its plan
    /// and its slots exist, so the failing statement is the outbox enqueue that follows it - the binding has succeeded
    /// by then and must vanish with the rest.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_FailureAfterTheBind_RollsBackEverything()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "run-create-rollback", "active", ("web", 'a'), ("api", 'b'));
        await using var crashing = world.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITenantDb>();
            services.AddSingleton<TenantDb>();
            services.AddSingleton<ITenantDb>(provider =>
                new ThrowingTenantDb(provider.GetRequiredService<TenantDb>(), "insert into public.outbox"));
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
    }

    /// <summary>
    /// No row of the rejected create survives anywhere: the run, its plan, its slots, the bindings the hook wrote,
    /// the idempotency record and the audit row are one transaction, and the outbox event never reached its insert.
    /// </summary>
    /// <param name="world">The suite's tenant.</param>
    private async Task AssertNothingSurvivedAsync(RunCreationTests.World world, string idempotencyKey)
    {
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where org_id = '{world.Org:D}'")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.idempotency_record where org_id = '{world.Org:D}' "
            + $"and key = '{idempotencyKey}'")).ShouldBe(0, "the rejected create's key was never stored");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' "
            + "and action in ('run.create', 'release.bind')")).ShouldBe(0);
    }

    /// <summary>
    /// The replay compares the whole body: the same key with the same body returns the original run and its bindings,
    /// and the same key with another <c>release_id</c> is the shared idempotency conflict.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_ReplayKeepsOneRun_AndThePayloadHashCoversTheRelease()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var firstRelease = await ReleaseWithAsync(world, "run-create-key-a", "active", ("web", 'a'));
        var secondRelease = await ReleaseWithAsync(world, "run-create-key-b", "active", ("web", 'a'));
        var key = NewKey();
        var body = RunBodyWith(world.ManifestId, Components(("web", 'a')), firstRelease);

        var first = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        var replay = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Id("run_id").ShouldBe(first.Id("run_id"));
        (await BindingCountAsync(world)).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);

        var conflicting = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a')), secondRelease),
            idempotencyKey: key);

        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where org_id = '{world.Org:D}' and release_id = '{secondRelease:D}'"))
            .ShouldBe(0);
    }
}
