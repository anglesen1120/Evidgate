using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Independent QA of 04.1.2-T4 (migration 0029 <c>producer_slot</c> and <see cref="IProducerRegistry"/>):
/// boundary, negative, race and privilege cases that the implementer's suite does not cover. Written
/// against the plan contract (AC2 per-shard registration, AC3 first-write binding, AC4 unknown producer,
/// T9/T54/T88 in plans/features/README.md), not against the implementation's internals.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ProducerSlotQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ProducerSlotQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid Org) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One migrated database with an org and a project created through the real routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, org);
    }

    /// <summary>Runs one registry call in a fresh writable ambient tenant scope and commits it.</summary>
    private static async Task<T> InScopeAsync<T>(World world, Func<IProducerRegistry, ValueTask<T>> call)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var result = await call(services.ServiceProvider.GetRequiredService<IProducerRegistry>());
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return result;
    }

    // ------------------------------------------------------------------ boundary: bound CHECKs vs the registry

    [Fact]
    public async Task BindOrCheck_TokenJtiAtMaxLength256_Binds()
    {
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var jti = new string('j', 256);
        var result = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", jti, null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Bound.ShouldBeTrue();
    }

    [Fact]
    public async Task BindOrCheck_TokenJtiOver256_ReturnsFailure_NotAnUnhandledDatabaseError()
    {
        // Plan contract: an invalid identity is refused as a result. The bound CHECK (1..256) must not
        // surface as an unhandled PostgresException (23514) escaping the registry.
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var jti = new string('j', 257);
        var result = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", jti, null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        result.Error.Status.ShouldBe(422);
    }

    [Fact]
    public async Task BindOrCheck_EmptyTokenJti_ReturnsFailure_NotAnUnhandledDatabaseError()
    {
        // bound_token_jti CHECK requires length >= 1. An empty jti must be refused as a result.
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", string.Empty, null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        result.Error.Status.ShouldBe(422);
    }

    [Fact]
    public async Task BindOrCheck_EmptyProducerId_IsRefusedBeforeSql()
    {
        // An empty producer_id is outside the column's 1..128 charset, so it is refused as a result
        // before any SQL; `unknown_producer` stays reserved for a well-formed id with no slot (AC4).
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r => r.BindOrCheckAsync(run, string.Empty, "jti-a", null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        result.Error.Status.ShouldBe(422);
    }

    // ------------------------------------------------------------------ shard-count boundary (T54 / ProducerSlotLimits)

    [Fact]
    public async Task RegisterSlots_AtMaxShardCount256_RegistersEveryShard()
    {
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();

        var result = await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 256, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Count.ShouldBe(256);
        result.Value.Select(slot => slot.ProducerId).Distinct().Count().ShouldBe(256);
        result.Value[0].ProducerId.ShouldBe("shard-1");
        result.Value[^1].ProducerId.ShouldBe("shard-256");
    }

    [Fact]
    public async Task RegisterSlots_AboveMaxShardCount_IsRefusedAndWritesNothing()
    {
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();

        var result = await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 257, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();

        var rows = await InScopeAsync(world, r => r.GetRunProducerSlotsAsync(run, TestContext.Current.CancellationToken));
        rows.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ isolation

    [Fact]
    public async Task SameProducerIdInTwoRuns_BindsIndependently()
    {
        // The run-scoped unique and the run-scoped bind mean "shard-1" in run A and in run B are two slots.
        await using var world = await StartAsync();
        var runA = Guid.CreateVersion7();
        var runB = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(runA, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, r => r.RegisterSlotsAsync(runB, 1, TestContext.Current.CancellationToken));

        var bindA = await InScopeAsync(world, r => r.BindOrCheckAsync(runA, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));
        var bindB = await InScopeAsync(world, r => r.BindOrCheckAsync(runB, "shard-1", "jti-b", null, TestContext.Current.CancellationToken));

        bindA.IsSuccess.ShouldBeTrue();
        bindA.Value.Bound.ShouldBeTrue();
        bindB.IsSuccess.ShouldBeTrue();
        bindB.Value.Bound.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ race

    [Fact]
    public async Task ConcurrentRegisterSlots_SameRun_NeverLeaksAnUnhandledDatabaseError()
    {
        // Two writers race on one run. The contract is: each call either succeeds (idempotent or first) or
        // returns a result failure. No unhandled 23505 may escape, and the run ends with exactly 3 slots.
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => RegisterOutcomeAsync(world, run)));

        outcomes.ShouldAllBe(outcome => outcome == "ok" || outcome == "failure");

        var rows = await InScopeAsync(world, r => r.GetRunProducerSlotsAsync(run, TestContext.Current.CancellationToken));
        rows.Count.ShouldBe(3);
    }

    private static async Task<string> RegisterOutcomeAsync(World world, Guid run)
    {
        try
        {
            var result = await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 3, TestContext.Current.CancellationToken));
            return result.IsSuccess ? "ok" : "failure";
        }
        catch (Exception ex)
        {
            return "exception:" + ex.GetType().Name;
        }
    }

    // ------------------------------------------------------------------ privilege (app role, migration 0029 grants)

    [Fact]
    public async Task AppRole_CannotUpdateShardIndex()
    {
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(
            "update public.producer_slot set shard_index = 2 where run_id = @run",
            ("run", run));

        error.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task AppRole_CannotDeleteSlots()
    {
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync("delete from public.producer_slot where run_id = @run", ("run", run));

        error.SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ ciRunRef identity (documented safe default)

    [Fact]
    public async Task BindOrCheck_NullCiRunRefThenCiRunRef_IsTaken()
    {
        // The comparison is exact, so adding a CI reference to a slot first bound without one is a
        // different identity: producer_slot_taken, never a silent re-bind (BindOrCheckAsync XML doc).
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        var retry = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        retry.IsSuccess.ShouldBeFalse();
        retry.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        retry.Error.Status.ShouldBe(403);
    }

    [Fact]
    public async Task BindOrCheck_CiRunRefThenNull_IsTaken()
    {
        // The other direction: dropping the CI reference is equally a different identity.
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        var retry = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        retry.IsSuccess.ShouldBeFalse();
        retry.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        retry.Error.Status.ShouldBe(403);
    }

    // ------------------------------------------------------------------ plan §8 P4/P3: the lookups are indexed

    [Fact]
    public async Task BindAndSlotRead_UseTheirIndexes()
    {
        // P4/P3: the bind lookup is by (org_id, run_id, producer_id) and the per-run slot read is by
        // (org_id, run_id). Sequential scans are disabled in this session (the WaiverTests precedent,
        // tests/Tenancy.IntegrationTests/WaiverTests.cs:504) so a three-row table cannot make the
        // planner prefer a scan, and the production statements are explained with their real parameters.
        await using var world = await StartAsync();
        var run = Guid.CreateVersion7();
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 3, TestContext.Current.CancellationToken));
        await _fixture.ExecuteAsync(world.Database, "analyze public.producer_slot");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.SetAsync("enable_seqscan", "off");

        var bind = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.BindIfUnbound}",
            ("org_id", world.Org),
            ("run_id", run),
            ("producer_id", "shard-1"),
            ("jti", "jti-a"),
            ("ci_run_ref", "1/7/2"));
        bind.ShouldContain("producer_slot_run_producer_key");

        var read = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.ReadRunSlots}",
            ("org_id", world.Org),
            ("run_id", run));
        read.ShouldContain("producer_slot_run");
    }
}
