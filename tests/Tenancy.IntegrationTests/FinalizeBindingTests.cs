using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.1.2-T5's finalize half (T68, AC9) against a real PostgreSQL: <see cref="IProducerRegistry.CloseSlotAsync"/>
/// sets <c>closed_at</c> and the two declared columns (migration 0035) exactly once, a repeat call with the
/// bound identity is an idempotent no-op returning the STORED values, a foreign identity is 403
/// <c>producer_slot_taken</c> and an unknown producer 422 <c>unknown_producer</c>. 05.3.2-T1's finalize handler
/// is the caller of this contract; these tests drive the contract directly, the plan's
/// <c>FinalizeBindingTests</c> row.
/// </summary>
[Trait("Category", "Docker")]
public sealed class FinalizeBindingTests
{
    private readonly PostgresTenancyFixture _fixture;

    public FinalizeBindingTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>Runs one registry call inside a fresh ambient tenant scope for the world's org.</summary>
    private static async Task<T> InScopeAsync<T>(World world, Func<IProducerRegistry, ValueTask<T>> call)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var result = await call(services.ServiceProvider.GetRequiredService<IProducerRegistry>());

        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return result;
    }

    /// <summary>One run row for the world's org (0031's composite run key), with no CI reference.</summary>
    private Task<Guid> CreateRunAsync(World world) =>
        RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    /// <summary>The slot's stored closure, read as the app role inside a tenant scope.</summary>
    private async Task<(DateTimeOffset? ClosedAt, long? DeclaredLastSequence, int? DeclaredCount, int Version)> ClosureAsync(World world)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // Npgsql maps `timestamptz` to a UTC DateTime, so reading it as DateTimeOffset? would throw
        // InvalidCastException on the unchecked cast in ScalarAsync. Read DateTime and keep the tuple's
        // API-facing DateTimeOffset? type.
        var closedAt = await scope.ScalarAsync<DateTime?>("select closed_at from public.producer_slot");

        return (
            closedAt is null
                ? (DateTimeOffset?)null
                : new DateTimeOffset(DateTime.SpecifyKind(closedAt.Value, DateTimeKind.Utc)),
            await scope.ScalarAsync<long?>("select declared_last_sequence from public.producer_slot"),
            await scope.ScalarAsync<int?>("select declared_count from public.producer_slot"),
            await scope.ScalarAsync<int>("select version from public.producer_slot"));
    }

    // ------------------------------------------------------------------ AC9: stores once, repeat is a no-op

    [Fact]
    public async Task close_slot_idempotent()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, registry => registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        var first = await InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-a", null, declaredLastSequence: 12, declaredCount: 11, TestContext.Current.CancellationToken));

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value.Closed.ShouldBeTrue();
        first.Value.DeclaredLastSequence.ShouldBe(12);
        first.Value.DeclaredCount.ShouldBe(11);
        var stored = await ClosureAsync(world);
        stored.DeclaredLastSequence.ShouldBe(12);
        stored.DeclaredCount.ShouldBe(11);
        stored.ClosedAt.ShouldNotBeNull();
        var versionAfterFirst = stored.Version;

        // The repeat carries different declared values on purpose: they are ignored, the STORED ones are
        // returned, nothing is written and the version does not move (T68/AC9).
        var repeat = await InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-a", null, declaredLastSequence: 99, declaredCount: 99, TestContext.Current.CancellationToken));

        repeat.IsSuccess.ShouldBeTrue(repeat.Error?.Message);
        repeat.Value.Closed.ShouldBeFalse();
        repeat.Value.DeclaredLastSequence.ShouldBe(12);
        repeat.Value.DeclaredCount.ShouldBe(11);
        repeat.Value.ClosedAt.ShouldBe(first.Value.ClosedAt);
        var afterRepeat = await ClosureAsync(world);
        afterRepeat.DeclaredLastSequence.ShouldBe(12);
        afterRepeat.DeclaredCount.ShouldBe(11);
        afterRepeat.ClosedAt.ShouldBe(stored.ClosedAt);
        afterRepeat.Version.ShouldBe(versionAfterFirst);
    }

    // ------------------------------------------------------------------ AC9: identity guards

    [Fact]
    public async Task close_slot_foreign_identity_403()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, registry => registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        var foreign = await InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-b", "1/7/2", 1, 1, TestContext.Current.CancellationToken));
        var mismatchedCiRun = await InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-a", "1/7/3", 1, 1, TestContext.Current.CancellationToken));

        foreign.IsSuccess.ShouldBeFalse();
        foreign.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        foreign.Error.Status.ShouldBe(403);
        mismatchedCiRun.IsSuccess.ShouldBeFalse();
        mismatchedCiRun.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        (await ClosureAsync(world)).ClosedAt.ShouldBeNull();
    }

    [Fact]
    public async Task close_slot_unbound_slot_403()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        // Nobody is the bound identity of an unbound slot, so no caller may finalize it.
        var unbound = await InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-a", null, 0, 0, TestContext.Current.CancellationToken));

        unbound.IsSuccess.ShouldBeFalse();
        unbound.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        (await ClosureAsync(world)).ClosedAt.ShouldBeNull();
    }

    [Fact]
    public async Task close_slot_unknown_producer_422()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        var unknown = await InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-9", "jti-a", null, 0, 0, TestContext.Current.CancellationToken));

        unknown.IsSuccess.ShouldBeFalse();
        unknown.Error!.Code.ShouldBe(ApiErrorCode.UnknownProducer);
        unknown.Error.Status.ShouldBe(422);
    }

    [Fact]
    public async Task close_slot_negative_declared_values_422_before_sql()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, registry => registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        var negative = await InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-a", null, -1, 0, TestContext.Current.CancellationToken));

        negative.IsSuccess.ShouldBeFalse();
        negative.Error!.Status.ShouldBe(422);
        (await ClosureAsync(world)).ClosedAt.ShouldBeNull();
    }

    // ------------------------------------------------------------------ AC9: the close race

    [Fact]
    public async Task close_slot_concurrent_calls_one_winner_returns_stored_values()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, registry => registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        // Two finalizes of the same bound slot race: the conditional UPDATE is the arbiter, so exactly one
        // closes and both answer the same stored values (T68, AC9).
        var first = InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-a", null, 7, 6, TestContext.Current.CancellationToken));
        var second = InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-a", null, 7, 6, TestContext.Current.CancellationToken));

        var results = await Task.WhenAll(first, second);

        results.Count(result => result.IsSuccess && result.Value.Closed).ShouldBe(1);
        results.Count(result => result.IsSuccess && !result.Value.Closed).ShouldBe(1);
        results.Select(result => result.Value.ClosedAt).Distinct().Count().ShouldBe(1);
        var stored = await ClosureAsync(world);
        stored.DeclaredLastSequence.ShouldBe(7);
        stored.DeclaredCount.ShouldBe(6);
    }

    // ------------------------------------------------------------------ closed-slot behaviour

    [Fact]
    public async Task closed_slot_still_binds_its_owner_and_refuses_others()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, registry => registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));
        await InScopeAsync(world, registry =>
            registry.CloseSlotAsync(runId, "shard-1", "jti-a", null, 3, 3, TestContext.Current.CancellationToken));

        // `closed_at` records the finalize; it is not a write gate of its own - the run's window closes the
        // run (T81) - so the bound identity still passes and a foreign one is still refused (T9).
        var owner = await InScopeAsync(world, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));
        var foreign = await InScopeAsync(world, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-b", null, TestContext.Current.CancellationToken));

        owner.IsSuccess.ShouldBeTrue(owner.Error?.Message);
        owner.Value.Bound.ShouldBeFalse();
        foreign.IsSuccess.ShouldBeFalse();
        foreign.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
    }
}
