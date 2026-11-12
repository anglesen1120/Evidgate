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

    /// <summary>The two orgs the cross-tenant test seeds; neither needs an `organization` row.</summary>
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");


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
    /// Cancelling a fire after at least one batch committed still leaves its audit row, on a
    /// one-connection pool: the failed batch's connection must be released before the recovery
    /// audit opens its own, or that open waits for the pool while the task failure is pending.
    /// The clock cancels the token on its second reading, the one the first full batch's budget
    /// check makes, so the next batch is issued with a cancelled token and nothing races.
    /// </summary>
    [Fact]
    public async Task Purge_CancelledMidDrain_StillAuditsOnAOneConnectionPool()
    {
        const int seeded = 5;
        const int limit = 1;

        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredAsync(owner, seeded);

        await using var services = BuildServices(database, sweepPoolSize: 1);
        using var cancellation = new CancellationTokenSource();
        var handler = Handler(services, new CancelOnReadTimeProvider(cancellation, cancelAtRead: 2));

        var failure = await Should.ThrowAsync<Exception>(async () =>
            await handler.HandleAsync(new IdempotencyPurgePayload(limit), scope: null, cancellation.Token));

        (failure is OperationCanceledException || failure.InnerException is OperationCanceledException)
            .ShouldBeTrue($"cancellation must surface as an OperationCanceledException, got {failure.GetType().Name}: {failure.Message}");

        var remaining = await CountExpiredAsync(owner);
        remaining.ShouldBeGreaterThan(0, "the rest of the backlog stays for the next fire");
        (await AuditCountAsync(owner)).ShouldBe(1, "a cancelled fire still attests the rows it already removed");
        var counts = await AuditCountsAsync(owner);
        counts.Removed.ShouldBe((int)(seeded - remaining), "the row carries exactly the batches that committed");
        counts.BatchLimit.ShouldBe(limit);
        (await AuditFlagsAsync(owner)).BudgetExhausted.ShouldBeFalse("the budget did not end this drain");
    }

    /// <summary>
    /// The budget has to stay under the lease the consumer enforces, or a reclaiming worker could
    /// take the row while this fire is still deleting. The consumer cancels a handler at the lease
    /// less `min(5 s, lease / 10)` (TaskConsumer), so four minutes leaves the margin spare.
    /// </summary>
    [Fact]
    public void PurgeTimeBudget_StaysUnderTheDefaultLeaseWindow()
    {
        EvidgateSweeps.PurgeTimeBudget.ShouldBe(TimeSpan.FromMinutes(4));

        var lease = TimeSpan.FromSeconds(new TasksOptions().VisibilityTimeoutSeconds);
        EvidgateSweeps.PurgeTimeBudget.ShouldBeLessThan(
            lease - TimeSpan.FromSeconds(5),
            "the handler window is the 300 s default lease less the consumer's 5 s margin");
    }

    /// <summary>
    /// The purge is cross-tenant: one fire removes the expired rows of two orgs, keeps both orgs'
    /// live rows, and writes one audit row carrying the sum — the table has no org column at all,
    /// so it cannot become a per-tenant copy of what was deleted.
    /// </summary>
    [Fact]
    public async Task Purge_DeletesEveryOrgsExpiredRowsAndAuditsTheSum()
    {
        const int perOrg = 3;
        const int limit = 100;

        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredAsync(owner, perOrg, OrgA);
        await SeedExpiredAsync(owner, perOrg, OrgB);
        await SeedLiveAsync(owner, OrgA, "live-a");
        await SeedLiveAsync(owner, OrgB, "live-b");

        await using var services = BuildServices(database);
        var handler = Handler(services, TimeProvider.System);

        await handler.HandleAsync(new IdempotencyPurgePayload(limit), scope: null, Token);

        foreach (var org in new[] { OrgA, OrgB })
        {
            (await CountExpiredForOrgAsync(owner, org)).ShouldBe(0, "expired rows of every org are purgeable");
            (await TaskSql.ScalarAsync<long>(
                owner,
                """
                select count(*) from public.idempotency_record
                where operation = @operation and org_id = @org
                """,
                ("operation", ExpiredOperation + "-live"),
                ("org", org))).ShouldBe(1, "a live row of either org stays");
        }

        (await AuditCountAsync(owner)).ShouldBe(1, "one row per fire, not one per org");
        (await AuditCountsAsync(owner)).Removed.ShouldBe(2 * perOrg, "the row carries the summed removal");

        string[] expectedColumns = ["action", "actor", "batch_limit", "budget_exhausted", "created_at", "id", "removed"];
        (await TaskSql.QueryAsync<string>(
            owner,
            """
            select attname::text from pg_attribute
            where attrelid = 'public.platform_sweep_audit'::regclass and attnum > 0 and not attisdropped
            order by attname
            """)).ShouldBe(expectedColumns, "the record holds no tenant dimension and no payload");
    }

    /// <summary>
    /// A payload outside the function's range is clamped, not trusted. The owner function reads
    /// `least(greatest(p_limit, 0), 10000)`, so a non-positive limit would delete nothing while a
    /// too-large one would read a capped batch as the backlog drained. The stepping clock bounds
    /// both cases: a loop that never ends on its own stops on the budget instead of hanging.
    /// </summary>
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(10_001, EvidgateSweeps.PurgeMaxBatchSize)]
    [InlineData(int.MaxValue, EvidgateSweeps.PurgeMaxBatchSize)]
    public async Task Purge_ClampsThePayloadLimitIntoTheFunctionRange(int payloadLimit, int expectedLimit)
    {
        var seeded = expectedLimit == 1 ? 3 : expectedLimit + 1;

        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredAsync(owner, seeded);

        await using var services = BuildServices(database);
        var handler = Handler(services, new SteppingTimeProvider(TimeSpan.FromMinutes(1)));

        await handler.HandleAsync(new IdempotencyPurgePayload(payloadLimit), scope: null, Token);

        (await CountExpiredAsync(owner)).ShouldBe(0, "the clamped limit still drains the backlog");
        (await AuditCountAsync(owner)).ShouldBe(1);
        var counts = await AuditCountsAsync(owner);
        counts.Removed.ShouldBe(seeded);
        counts.BatchLimit.ShouldBe(expectedLimit, "the audit row records the limit the function received");
        (await AuditFlagsAsync(owner)).BudgetExhausted.ShouldBeFalse("the backlog ended this drain");
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
                [$"{TasksOptions.SectionName}:WorkerConnectionString"] = _fixture.WorkerConnectionString(database),
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

    private static async Task SeedExpiredAsync(string ownerConnectionString, int count, Guid? org = null) =>
        await TaskSql.ExecuteAsync(
            ownerConnectionString,
            """
            insert into public.idempotency_record
              (id, org_id, project_id, operation, key, request_hash, created_at, expires_at)
            select gen_random_uuid(), @org, null, @operation, 'k:' || i,
                   'sha256:test', now() - interval '3 days', now() - interval '1 hour'
              from generate_series(1, @count) i
            """,
            ("org", org ?? OrgA),
            ("operation", ExpiredOperation),
            ("count", count));

    private static async Task SeedLiveAsync(string ownerConnectionString, Guid? org = null, string key = "live") =>
        await TaskSql.ExecuteAsync(
            ownerConnectionString,
            """
            insert into public.idempotency_record
              (id, org_id, project_id, operation, key, request_hash, created_at, expires_at)
            values
              (gen_random_uuid(), @org, null, @operation, @key,
               'sha256:test', now(), now() + interval '1 day')
            """,
            ("org", org ?? OrgA),
            ("operation", ExpiredOperation + "-live"),
            ("key", key));

    private static Task<long> CountExpiredAsync(string ownerConnectionString) =>
        TaskSql.ScalarAsync<long>(
            ownerConnectionString,
            "select count(*) from public.idempotency_record where operation = @operation",
            ("operation", ExpiredOperation));

    private static Task<long> CountExpiredForOrgAsync(string ownerConnectionString, Guid org) =>
        TaskSql.ScalarAsync<long>(
            ownerConnectionString,
            "select count(*) from public.idempotency_record where operation = @operation and org_id = @org",
            ("operation", ExpiredOperation),
            ("org", org));

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

    /// <summary>
    /// The system clock, except that the reading numbered <paramref name="cancelAtRead"/> cancels
    /// the fire first. The handler reads the clock once at the drain's start and once per full
    /// batch, so cancelling on the second reading lands just after the first batch commits.
    /// </summary>
    private sealed class CancelOnReadTimeProvider(CancellationTokenSource cancellation, int cancelAtRead) : TimeProvider
    {
        private int _reads;

        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _reads) == cancelAtRead)
            {
                cancellation.Cancel();
            }

            return base.GetTimestamp();
        }
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
