using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
[Trait("Category", "Docker")]
public sealed partial class ProducerSlotQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ProducerSlotQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid Org,
        DedicatedDatabase Dedicated) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await Api.DisposeAsync();
            }
            finally
            {
                // The database is this world's to drop even when the host's own disposal throws.
                await Dedicated.DisposeAsync();
            }
        }
    }

    /// <summary>One migrated database with an org and a project created through the real routes.</summary>
    // The blocked-insert probe counts every transactionid wait in the current database, and the
    // EXPLAIN cases pin an index on a three-row table: both need a database no other test touches.
    private Task<World> StartAsync() =>
        _fixture.StartDedicatedAsync(async dedicated =>
        {
            var database = dedicated.Name;
            var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
            var client = api.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
            await CreateProjectAsync(client, user, org, "checkout");

            return new World(api, client, database, org, dedicated);
        });

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

    /// <summary>One committed run row for <paramref name="world"/>'s org (0031's composite run key).</summary>
    private async Task<Guid> CreateRunAsync(World world) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    // ------------------------------------------------------------------ boundary: bound CHECKs vs the registry

    [Fact]
    public async Task BindOrCheck_TokenJtiAtMaxLength256_Binds()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
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
        var run = await CreateRunAsync(world);
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
        var run = await CreateRunAsync(world);
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
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r => r.BindOrCheckAsync(run, string.Empty, "jti-a", null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        result.Error.Status.ShouldBe(422);
    }

    [Fact]
    public async Task BindOrCheck_ProducerIdWithTrailingNewline_IsRefused()
    {
        // .NET's `$` also matches before a trailing newline while PostgreSQL's `~` does not, so the
        // validator anchors with `\z`: "shard-1\n" must be refused, not accepted here and rejected there.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1\n", "jti-a", null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
    }

    [Fact]
    public async Task BindOrCheck_NulInTokenJti_IsRefused()
    {
        // PostgreSQL rejects U+0000 in text (22021), so the validator must refuse it as a result.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti\0a", null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        result.Error.Status.ShouldBe(422);
    }

    [Fact]
    public async Task BindOrCheck_NulInCiRunRef_IsRefused()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        var result = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", "1/7\0/2", TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        result.Error.Status.ShouldBe(422);
    }

    // ------------------------------------------------------------------ shard-count boundary (T54 / ProducerSlotLimits)

    [Fact]
    public async Task RegisterSlots_AtMaxShardCount256_RegistersEveryShard()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);

        var result = await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 256, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Count.ShouldBe(256);
        result.Value.Select(slot => slot.ProducerId).Distinct().Count().ShouldBe(256);
        result.Value[0].ProducerId.ShouldBe("shard-1");
        result.Value[^1].ProducerId.ShouldBe("shard-256");
    }

    // ------------------------------------------------------------------ isolation

    [Fact]
    public async Task SameProducerIdInTwoRuns_BindsIndependently()
    {
        // The run-scoped unique and the run-scoped bind mean "shard-1" in run A and in run B are two slots.
        await using var world = await StartAsync();
        var runA = await CreateRunAsync(world);
        var runB = await CreateRunAsync(world);
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
    public async Task ConcurrentRegisterSlots_SameCount_BothSucceedWithTheSameSlots()
    {
        // Two same-count racers: one inserts, the other's `on conflict do nothing` inserts nothing and
        // its re-read finds the same count, so BOTH succeed and both return the stored slot ids. No
        // unhandled 23505 may escape, and the run ends with exactly 3 rows.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            InScopeAsync(world, r => r.RegisterSlotsAsync(run, 3, TestContext.Current.CancellationToken))));

        results.ShouldAllBe(result => result.IsSuccess);
        results[0].Value.Select(slot => slot.Id).ShouldBe(results[1].Value.Select(slot => slot.Id));

        var rows = await InScopeAsync(world, r => r.GetRunProducerSlotsAsync(run, TestContext.Current.CancellationToken));
        rows.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RegisterSlots_DifferentCount_Sequential_IsConflictAndLeavesTheOriginalRows()
    {
        // (a) sequential: 2 slots exist, a call with 4 is the 409 and the original 2 rows are untouched.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        var first = await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 2, TestContext.Current.CancellationToken));

        var second = await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 4, TestContext.Current.CancellationToken));

        second.IsSuccess.ShouldBeFalse();
        second.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotsConflict);
        second.Error.Status.ShouldBe(409);

        var rows = await InScopeAsync(world, r => r.GetRunProducerSlotsAsync(run, TestContext.Current.CancellationToken));
        rows.Select(slot => slot.Id).ShouldBe(first.Value.Select(slot => slot.Id));
    }

    [Fact]
    public async Task RegisterSlots_DifferentCount_Interleaved_IsConflictNeverSuccess()
    {
        // (b) the F1 interleave: tx A registers 2 and stays uncommitted; tx B calls with 4 on another
        // connection, so its insert blocks on A's rows. Committing A lets B's `on conflict do nothing`
        // skip A's two shard-1/shard-2 rows and insert shard-3/shard-4 - a partial insert (2 of 4), which
        // must be the 409 and never a success. B then rolls back, so the run keeps A's 2 slots.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);

        // B runs in its own task. Tenant scopes are AsyncLocal and a second BeginAsync in one flow throws,
        // so B's flow must never see A's scope: the task is started before A's scope opens (its execution
        // context is captured here, with no ambient scope), and it holds its own scope open until the gate opens.
        var gateB = new SemaphoreSlim(0, 1);
        var callB = Task.Run(async () =>
        {
            await using var servicesB = world.Api.Services.CreateAsyncScope();
            await using var scopeB = await servicesB.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
                .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
            await gateB.WaitAsync(TestContext.Current.CancellationToken);

            return await servicesB.ServiceProvider.GetRequiredService<IProducerRegistry>()
                .RegisterSlotsAsync(run, 4, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await using var servicesA = world.Api.Services.CreateAsyncScope();
        await using var scopeA = await servicesA.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var registeredA = await servicesA.ServiceProvider.GetRequiredService<IProducerRegistry>()
            .RegisterSlotsAsync(run, 2, TestContext.Current.CancellationToken);
        gateB.Release();
        registeredA.IsSuccess.ShouldBeTrue(registeredA.Error?.Message);

        // B's insert is waiting on A's uncommitted rows: observe the wait through pg_locks rather than
        // sleeping, then commit A so B can proceed.
        await WaitForBlockedInsertAsync(world, callB);
        await scopeA.CommitAsync(TestContext.Current.CancellationToken);

        var resultB = await callB;

        resultB.IsSuccess.ShouldBeFalse();
        resultB.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotsConflict);
        resultB.Error.Status.ShouldBe(409);

        // B's task disposed its scope without a commit (rollback), so only A's 2 slots remain.

        var rows = await InScopeAsync(world, r => r.GetRunProducerSlotsAsync(run, TestContext.Current.CancellationToken));
        rows.Select(slot => slot.Id).ShouldBe(registeredA.Value.Select(slot => slot.Id));
    }

    /// <summary>
    /// Waits until a backend of <em>this test's database</em> is blocked waiting for another
    /// transaction (B's insert waiting on A's uncommitted conflicting rows: a unique-index conflict
    /// waits on the other transaction's <c>transactionid</c> lock), polling
    /// <c>pg_locks</c> joined to <c>pg_stat_activity</c> with a bounded budget - an explicit ordering
    /// signal, not a sleep. The join and the <c>datname</c>/<c>locktype</c> filters keep the probe
    /// specific: a cluster-wide <c>not granted</c> count would also see another test's blocked backend
    /// and could return before B is actually blocked, leaving the partial-insert branch unexercised.
    /// </summary>
    private async Task WaitForBlockedInsertAsync(World world, Task other)
    {
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        for (var attempt = 0; attempt < 400; attempt++)
        {
            // wait_event_type/state of another role's backend are hidden from the owner connection
            // (pg_read_all_stats is not granted), so the probe keys on the ungranted transactionid lock
            // in this test's own database instead.
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select count(*)
                  from pg_locks l
                  join pg_stat_activity a on a.pid = l.pid
                 where not l.granted
                   and a.datname = current_database()
                   and l.locktype = 'transactionid'
                """;
            var waiting = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            if (waiting > 0)
            {
                return;
            }

            if (other.IsCompleted)
            {
                var state = other.IsFaulted
                    ? $"faulted: {other.Exception!.Flatten().Message}"
                    : "completed without blocking";
                throw new InvalidOperationException($"the second registration {state}");
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        await using var dump = connection.CreateCommand();
        dump.CommandText = """
            select string_agg(format('pid=%s state=%s wait=%s/%s q=%s', pid, state, wait_event_type, wait_event, left(query, 70)), ' | ')
              from pg_stat_activity
             where datname = current_database()
            """;
        var backends = await dump.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
        throw new InvalidOperationException(
            $"no backend of {world.Database} was blocked on a transactionid lock within the budget; "
            + $"the second registration is still running and the interleave did not happen. backends: {backends}");
    }
}
