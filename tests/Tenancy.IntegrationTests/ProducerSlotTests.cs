using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.1.2-T4 through a real PostgreSQL: migration 0029's <c>producer_slot</c> table (FORCE RLS, the
/// tenant key and the two run-scoped uniques, the app-role grants and the bound CHECKs) and the
/// <see cref="IProducerRegistry"/> contract - the per-shard registration inside a caller's tenant
/// transaction (AC2) and the first-write binding (T9). The fixture is this project's own
/// <see cref="PostgresTenancyFixture"/> (one digest-pinned PostgreSQL 18 container per assembly, a
/// fresh database per test brought to head by the real <c>Evidgate.Migrator</c>), the 06.1.1-T1
/// precedent for a new table in this suite.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class ProducerSlotTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public ProducerSlotTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One migrated database with an org and a project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>One raw insert of a slot row as the app role; every bounded column is overridable.</summary>
    private static string InsertSlotSql(
        Guid org,
        Guid runId,
        int shardIndex = 1,
        string producerId = "shard-1",
        string? boundTokenJti = null,
        string? boundCiRunRef = null,
        long lastSequence = 0) =>
        "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref, last_sequence) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{runId}', {shardIndex}, '{producerId}', "
        + $"{(boundTokenJti is null ? "null" : $"'{boundTokenJti}'")}, {(boundCiRunRef is null ? "null" : $"'{boundCiRunRef}'")}, {lastSequence})";

    /// <summary>One committed slot row for <paramref name="world"/>'s org, written as the app role.</summary>
    private async Task InsertSlotAsync(World world, Guid runId, int shardIndex = 1, string producerId = "shard-1")
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertSlotSql(world.Org, runId, shardIndex, producerId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>One committed run row for <paramref name="world"/>'s org (0031's composite run key).</summary>
    private async Task<Guid> CreateRunAsync(World world) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    /// <summary>One committed run row for <paramref name="org"/> (0031's composite run key).</summary>
    private async Task<Guid> CreateRunAsync(World world, Guid org) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);

    /// <summary>An org and a project in it, created through the real routes.</summary>
    private static async Task<Guid> CreateOtherOrgAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var otherSlug = Unique.Slug(slug);
        var org = await CreateOrgAsync(world.Client, user, otherSlug);
        await CreateProjectAsync(world.Client, user, org, Unique.Slug($"{otherSlug}-project"));

        return org;
    }

    /// <summary>Runs one registry call inside a fresh ambient tenant scope for <paramref name="org"/>.</summary>
    private static async Task<T> InScopeAsync<T>(
        World world,
        Guid org,
        Func<IProducerRegistry, ValueTask<T>> call,
        bool commit = true)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var result = await call(services.ServiceProvider.GetRequiredService<IProducerRegistry>());

        if (commit)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    /// <summary>The slot count of one org, read as the app role inside a tenant scope.</summary>
    private async Task<long> CountAsync(World world, Guid org)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.ScalarAsync<long>("select count(*) from public.producer_slot");
    }

    // ------------------------------------------------------------------ (d) AC2: registration

    [Fact]
    public async Task RegisterSlots_PerShard_SameTx()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        // The slots are written inside the caller's transaction (T13): a rollback takes them away, so
        // the run row and its slots commit or not at all.
        await using (var services = world.Api.Services.CreateAsyncScope())
        {
            await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
                .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
            var registry = services.ServiceProvider.GetRequiredService<IProducerRegistry>();

            var result = await registry.RegisterSlotsAsync(runId, 3, TestContext.Current.CancellationToken);

            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            result.Value.Count.ShouldBe(3);
            result.Value.Select(slot => slot.ShardIndex).ShouldBe([1, 2, 3]);
            result.Value.Select(slot => slot.ProducerId).ShouldBe(["shard-1", "shard-2", "shard-3"]);

            // A separate connection cannot see the uncommitted rows, and the rollback leaves none.
            (await CountAsync(world, world.Org)).ShouldBe(0);
        }

        (await CountAsync(world, world.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterSlots_CommitsWithTheCaller()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        var result = await InScopeAsync(
            world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await CountAsync(world, world.Org)).ShouldBe(2);
    }

    [Fact]
    public async Task RegisterSlots_SameCount_IsIdempotent()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        var first = await InScopeAsync(
            world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));
        var second = await InScopeAsync(
            world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        second.Value.Select(slot => slot.Id).ShouldBe(first.Value.Select(slot => slot.Id));
        (await CountAsync(world, world.Org)).ShouldBe(2);
    }

    [Fact]
    public async Task RegisterSlots_DifferentCount_Conflicts()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);

        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));
        var second = await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 3, TestContext.Current.CancellationToken));

        second.IsSuccess.ShouldBeFalse();
        second.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotsConflict);
        second.Error.Status.ShouldBe(409);
        (await CountAsync(world, world.Org)).ShouldBe(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    public async Task RegisterSlots_OutOfRange_RejectedBeforeSql(int shardCount)
    {
        await using var world = await StartAsync();

        var result = await InScopeAsync(
            world, world.Org, registry => registry.RegisterSlotsAsync(Guid.CreateVersion7(), shardCount, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(422);
        (await CountAsync(world, world.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterSlots_OutsideTenantScope_FailsLoudly()
    {
        await using var world = await StartAsync();
        await using var services = world.Api.Services.CreateAsyncScope();

        await Should.ThrowAsync<TenantScopeMissingException>(async () =>
            await services.ServiceProvider.GetRequiredService<IProducerRegistry>()
                .RegisterSlotsAsync(Guid.CreateVersion7(), 1, TestContext.Current.CancellationToken));
    }
}
