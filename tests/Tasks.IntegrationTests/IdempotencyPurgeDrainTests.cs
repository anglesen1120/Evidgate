using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The `idempotency.purge` handler on real PostgreSQL: one fire drains until the backlog
/// is gone or the time budget is spent, and every fire leaves exactly one
/// `platform_sweep_audit` row written through the owner definer the sweep role alone may
/// execute. The handler is built the way the worker composes it — the real
/// <c>IWorkerSchedDataSource</c> from <c>AddEvidgateTasks</c> — so the grants and the
/// connection are the production ones rather than a stand-in.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IdempotencyPurgeDrainTests
{
    /// <summary>Distinct from the sibling purge tests so a count can never see their rows.</summary>
    private const string ExpiredOperation = "idempotency-drain";

    /// <summary>The two removals the two fires in <c>Purge_WritesOneAuditRowPerCall</c> record.</summary>
    private static readonly int[] ExpectedRemovals = [5, 0];

    private readonly PostgresTasksFixture _fixture;

    public IdempotencyPurgeDrainTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A backlog larger than the removed batch ceiling still drains in one fire: 600 expired
    /// rows at limit 10 is 60 full batches, past the 50 the handler used to stop at. The live
    /// row is the negative control — the drain must stop at `expires_at`, not at the table.
    /// </summary>
    [Fact]
    public async Task Purge_DrainsPastTheOldBatchCeiling()
    {
        const int expired = 600;
        const int limit = 10;

        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredAsync(owner, expired);
        await SeedLiveAsync(owner);

        await using var services = BuildServices(database);
        var handler = Handler(services, TimeProvider.System);

        await handler.HandleAsync(new IdempotencyPurgePayload(limit), scope: null, Token);

        (await CountExpiredAsync(owner)).ShouldBe(0, "the fire must drain past the old 50-batch ceiling");
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.idempotency_record where operation = @operation",
            ("operation", ExpiredOperation + "-live"))).ShouldBe(1, "a record whose retention has not run out stays");

        (await AuditCountAsync(owner)).ShouldBe(1);
        var counts = await AuditCountsAsync(owner);
        counts.Removed.ShouldBe(expired);
        counts.BatchLimit.ShouldBe(limit);

        var flags = await AuditFlagsAsync(owner);
        flags.BudgetExhausted.ShouldBeFalse("the backlog ended the drain, not the budget");
        flags.Actor.ShouldBe("evidgate_worker_sched");
        (await AuditActionAsync(owner)).ShouldBe(EvidgateSweeps.IdempotencyPurgeTaskType);
    }

    /// <summary>
    /// The time budget stops the drain with rows still due and says so in the audit row: with a
    /// clock that advances a minute per reading, four full batches fit in the four-minute budget
    /// and the fifth is never asked for.
    /// </summary>
    [Fact]
    public async Task Purge_StopsAtTheTimeBudget_WithRowsLeft()
    {
        const int limit = 10;
        var expectedBatches = (int)EvidgateSweeps.PurgeTimeBudget.TotalMinutes;

        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredAsync(owner, 100);

        await using var services = BuildServices(database);
        var handler = Handler(services, new SteppingTimeProvider(TimeSpan.FromMinutes(1)));

        await handler.HandleAsync(new IdempotencyPurgePayload(limit), scope: null, Token);

        (await CountExpiredAsync(owner)).ShouldBe(
            100 - (expectedBatches * limit),
            "the budget stops the loop after the batches that fit, and the rest stays for the next fire");
        (await AuditCountAsync(owner)).ShouldBe(1);
        var counts = await AuditCountsAsync(owner);
        counts.Removed.ShouldBe(expectedBatches * limit);
        counts.BatchLimit.ShouldBe(limit);
        (await AuditFlagsAsync(owner)).BudgetExhausted.ShouldBeTrue(
            "a full batch hit the budget with rows still due");
    }

    /// <summary>Two fires leave two rows, each carrying what that fire removed — including a fire that found nothing.</summary>
    [Fact]
    public async Task Purge_WritesOneAuditRowPerCall()
    {
        const int limit = 10;

        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredAsync(owner, 5);

        await using var services = BuildServices(database);
        var handler = Handler(services, TimeProvider.System);
        var payload = new IdempotencyPurgePayload(limit);

        await handler.HandleAsync(payload, scope: null, Token);

        (await CountExpiredAsync(owner)).ShouldBe(0);
        (await AuditCountAsync(owner)).ShouldBe(1);
        (await AuditCountsAsync(owner)).Removed.ShouldBe(5);

        await handler.HandleAsync(payload, scope: null, Token);

        (await AuditCountAsync(owner)).ShouldBe(2, "each call appends exactly one row, never updates one");
        (await TaskSql.QueryAsync<int>(
            owner,
            "select removed from public.platform_sweep_audit order by removed desc"))
            .ShouldBe(ExpectedRemovals, "the second fire found nothing and still attested that it ran");
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.platform_sweep_audit where budget_exhausted")).ShouldBe(0);
    }

    /// <summary>A fire on an empty table still leaves its row, with zero removed and no budget stop.</summary>
    [Fact]
    public async Task Purge_EmptyTableStillWritesTheAuditRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await using var services = BuildServices(database);
        var handler = Handler(services, TimeProvider.System);

        await handler.HandleAsync(new IdempotencyPurgePayload(10), scope: null, Token);

        (await AuditCountAsync(owner)).ShouldBe(1);
        var counts = await AuditCountsAsync(owner);
        counts.Removed.ShouldBe(0);
        counts.BatchLimit.ShouldBe(10);
        var flags = await AuditFlagsAsync(owner);
        flags.BudgetExhausted.ShouldBeFalse();
        flags.Actor.ShouldBe("evidgate_worker_sched");
    }

    /// <summary>
    /// The table has one write path and no direct access: the sweep role inserts and reads only
    /// through the definer (which stamps its own actor), the app role holds nothing and cannot
    /// execute the definer, the operator reads, and UPDATE/DELETE are refused for the owner too.
    /// </summary>
    [Fact]
    public async Task PlatformSweepAudit_IsWritableOnlyThroughTheDefiner()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var worker = _fixture.WorkerSchedConnectionString(database);
        var app = _fixture.AppConnectionString(database);
        var @operator = _fixture.OperatorConnectionString(database);

        var id = await TaskSql.ScalarAsync<Guid>(
            worker,
            "select evidgate.record_sweep_audit(@action, @removed, @limit, @budget_exhausted)",
            ("action", EvidgateSweeps.IdempotencyPurgeTaskType),
            ("removed", 1),
            ("limit", 10),
            ("budget_exhausted", false));

        id.ShouldNotBe(Guid.Empty);
        (await TaskSql.ScalarAsync<string>(
            @operator,
            "select actor from public.platform_sweep_audit where id = @id",
            ("id", id))).ShouldBe("evidgate_worker_sched", "the definer stamps the actor; the caller cannot");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select action from public.platform_sweep_audit where id = @id",
            ("id", id))).ShouldBe(EvidgateSweeps.IdempotencyPurgeTaskType);

        // The sweep role's write arrives through EXECUTE only: it holds no table privilege.
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ExecuteAsync(
                worker,
                """
                insert into public.platform_sweep_audit (actor, action, removed, batch_limit, budget_exhausted)
                values ('evidgate_worker_sched', 'idempotency.purge', 0, 10, false)
                """));
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ScalarAsync<long>(worker, "select count(*) from public.platform_sweep_audit"));

        // The app role holds nothing on the table and cannot reach the definer.
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ScalarAsync<long>(app, "select count(*) from public.platform_sweep_audit"));
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ExecuteAsync(
                app,
                """
                insert into public.platform_sweep_audit (actor, action, removed, batch_limit, budget_exhausted)
                values ('evidgate_worker_sched', 'idempotency.purge', 0, 10, false)
                """));
        await AssertSqlStateAsync(
            "42501",
            async () => await TaskSql.ScalarAsync<Guid>(
                app,
                "select evidgate.record_sweep_audit('idempotency.purge', 0, 10, false)"));

        // Append-only for every role, the owner included: the guard trigger refuses both commands.
        await AssertSqlStateAsync("23514", async () => await TaskSql.ExecuteAsync(
            owner,
            "update public.platform_sweep_audit set removed = 0"));
        await AssertSqlStateAsync("23514", async () => await TaskSql.ExecuteAsync(
            owner,
            "delete from public.platform_sweep_audit"));

        (await AuditCountAsync(owner)).ShouldBe(1, "the refused statements left the row untouched");
    }

    /// <summary>
    /// Cancelling a fire after at least one batch committed still leaves its audit row, on a
    /// one-connection pool: the failed batch's connection must be released before the recovery
    /// audit opens its own, or that open waits for the pool while the task failure is pending.
    /// The token is cancelled only once the count has dropped, so `removed` is never zero.
    /// </summary>
    [Fact]
    public async Task Purge_CancelledMidDrain_StillAuditsOnAOneConnectionPool()
    {
        const int seeded = 5000;
        const int limit = 1;

        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredAsync(owner, seeded);

        await using var services = BuildServices(database, sweepPoolSize: 1);
        var handler = Handler(services, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();

        var cancelAfterFirstCommit = Task.Run(
            async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    if (await CountExpiredAsync(owner) < seeded)
                    {
                        await cancellation.CancelAsync();
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(1), CancellationToken.None);
                }
            },
            CancellationToken.None);

        Exception failure;
        try
        {
            failure = await Should.ThrowAsync<Exception>(async () =>
                await handler.HandleAsync(new IdempotencyPurgePayload(limit), scope: null, cancellation.Token));
        }
        finally
        {
            await cancelAfterFirstCommit;
        }

        (failure is OperationCanceledException || failure.InnerException is OperationCanceledException)
            .ShouldBeTrue($"cancellation must surface as an OperationCanceledException, got {failure.GetType().Name}: {failure.Message}");

        (await AuditCountAsync(owner)).ShouldBe(1, "a cancelled fire still attests the rows it already removed");
        var counts = await AuditCountsAsync(owner);
        counts.Removed.ShouldBeGreaterThan(0);
        counts.Removed.ShouldBeLessThan(seeded);
        counts.BatchLimit.ShouldBe(limit);
        (await AuditFlagsAsync(owner)).BudgetExhausted.ShouldBeFalse("the budget did not end this drain");
        (await CountExpiredAsync(owner)).ShouldBeGreaterThan(0, "the rest of the backlog stays for the next fire");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The worker's own composition for the sweep connection: <c>AddEvidgateTasks</c> resolves
    /// `IWorkerSchedDataSource` from the same configuration key the worker host uses. No hosted
    /// service starts here, so nothing polls.
    /// </summary>
    private ServiceProvider BuildServices(string database, int sweepPoolSize = 100)
    {
        // The same keys the sibling task tests set, so options validation behaves as in the
        // worker; the sweep connection is the production one for the scheduler role.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{TasksOptions.SectionName}:PollIntervalMilliseconds"] = "50",
                [$"{TasksOptions.SectionName}:BatchSize"] = "50",
                [$"{TasksOptions.SectionName}:BackoffBaseSeconds"] = "1",
                [$"{TasksOptions.SectionName}:BackoffMaxSeconds"] = "4",
                [$"{TasksOptions.SectionName}:ScheduleLookbackSeconds"] = "300",
                [$"{TasksOptions.SectionName}:SweepConnectionString"] =
                    new NpgsqlConnectionStringBuilder(_fixture.WorkerSchedConnectionString(database))
                    {
                        MaxPoolSize = sweepPoolSize,
                    }.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(_fixture.OpenAppDataSource(database));
        services.AddLogging();
        services.AddEvidgateTenancy();
        services.AddEvidgateTasks(configuration);

        return services.BuildServiceProvider();
    }

    private static IdempotencyPurgeTaskHandler Handler(ServiceProvider services, TimeProvider time) =>
        new(
            services.GetRequiredService<IWorkerSchedDataSource>(),
            time,
            NullLogger<IdempotencyPurgeTaskHandler>.Instance);

    private static async Task SeedExpiredAsync(string ownerConnectionString, int count) =>
        await TaskSql.ExecuteAsync(
            ownerConnectionString,
            """
            insert into public.idempotency_record
              (id, org_id, project_id, operation, key, request_hash, created_at, expires_at)
            select gen_random_uuid(), '11111111-1111-7111-8111-111111111111', null, @operation, 'k:' || i,
                   'sha256:test', now() - interval '3 days', now() - interval '1 hour'
              from generate_series(1, @count) i
            """,
            ("operation", ExpiredOperation),
            ("count", count));

    private static async Task SeedLiveAsync(string ownerConnectionString) =>
        await TaskSql.ExecuteAsync(
            ownerConnectionString,
            """
            insert into public.idempotency_record
              (id, org_id, project_id, operation, key, request_hash, created_at, expires_at)
            values
              (gen_random_uuid(), '11111111-1111-7111-8111-111111111111', null, @operation, 'live',
               'sha256:test', now(), now() + interval '1 day')
            """,
            ("operation", ExpiredOperation + "-live"));

    private static Task<long> CountExpiredAsync(string ownerConnectionString) =>
        TaskSql.ScalarAsync<long>(
            ownerConnectionString,
            "select count(*) from public.idempotency_record where operation = @operation",
            ("operation", ExpiredOperation));

    private static Task<long> AuditCountAsync(string ownerConnectionString) =>
        TaskSql.ScalarAsync<long>(ownerConnectionString, "select count(*) from public.platform_sweep_audit");

    private static Task<(int Removed, int BatchLimit)> AuditCountsAsync(string ownerConnectionString) =>
        TaskSql.QueryRowAsync<int, int>(
            ownerConnectionString,
            "select removed, batch_limit from public.platform_sweep_audit order by created_at desc limit 1");

    private static Task<(bool BudgetExhausted, string Actor)> AuditFlagsAsync(string ownerConnectionString) =>
        TaskSql.QueryRowAsync<bool, string>(
            ownerConnectionString,
            "select budget_exhausted, actor from public.platform_sweep_audit order by created_at desc limit 1");

    private static Task<string?> AuditActionAsync(string ownerConnectionString) =>
        TaskSql.ScalarAsync<string>(
            ownerConnectionString,
            "select action from public.platform_sweep_audit order by created_at desc limit 1");

    private static async Task AssertSqlStateAsync(string expected, Func<Task> statement)
    {
        var exception = await Should.ThrowAsync<PostgresException>(statement);
        exception.SqlState.ShouldBe(expected);
    }

    /// <summary>
    /// A clock that advances by one step on every reading. The handler reads the start once and
    /// then once per full batch, so the step decides how many batches fit in the budget without
    /// any sleep. The counter is expressed in the runtime's timestamp units — the base
    /// <c>GetElapsedTime</c> divides their difference by <see cref="TimeProvider.TimestampFrequency"/>,
    /// which is not overridable.
    /// </summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private readonly long _stepTicks;
        private long _ticks;

        public SteppingTimeProvider(TimeSpan step) => _stepTicks = (long)(step.TotalSeconds * TimestampFrequency);

        public override long GetTimestamp()
        {
            var current = Interlocked.Read(ref _ticks);
            Interlocked.Add(ref _ticks, _stepTicks);
            return current;
        }
    }
}
