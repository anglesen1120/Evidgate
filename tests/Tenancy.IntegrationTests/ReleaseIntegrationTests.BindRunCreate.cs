// The run-create hook: `POST /v1/projects/{p}/runs` naming a `release_id` binds the keys whose digests match inside
// the same transaction as the run, and never fails the run when some or all components do not match.
#pragma warning disable CA1861
using System.Net;
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
    /// A failure at the bind's own insert rolls the run, its plan, its slots and every binding back together: the hook
    /// runs after the run and slots are written, so nothing may survive it.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_FailureAtTheBind_RollsBackRunAndBindings()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "run-create-rollback", "active", ("web", 'a'), ("api", 'b'));
        await using var crashing = world.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITenantDb>();
            services.AddSingleton<TenantDb>();
            services.AddSingleton<ITenantDb>(provider =>
                new ThrowingTenantDb(provider.GetRequiredService<TenantDb>(), "insert into public.release_run"));
        }));
        using var client = crashing.CreateClient();

        var created = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, Components(("web", 'a'), ("api", 'b')), releaseId),
            client: client);

        created.Status.ShouldBe(HttpStatusCode.InternalServerError, created.ErrorMessage);
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
        (await BindingCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// The replay compares the whole body: the same key with the same body returns the original run and its bindings,
    /// and the same key with another <c>release_id</c> is the shared idempotency conflict.
    /// </summary>
    [Fact]
    public async Task BindAtRunCreate_ReplayKeepsOneRun_AndThePayloadHashCoversTheRelease()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
