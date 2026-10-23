using Evidgate.BuildingBlocks.Persistence.Tasks;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T6.2 / AC6: the `idempotency.purge` schedule, end to end — the cron fires
/// a platform task, the consumer claims it, and the handler calls the owner
/// `SECURITY DEFINER` function <c>evidgate.purge_expired_idempotency</c> as
/// <c>evidgate_worker_sched</c> (TL T80). Nothing is stubbed: the row is deleted by
/// the real function, on the real role.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class PurgeTaskTests
{
    /// <summary>Just after the registered `idempotency.purge` cron (03:00 UTC daily).</summary>
    private static readonly DateTimeOffset PurgeTime = new(2026, 9, 26, 3, 0, 30, TimeSpan.Zero);

    private readonly PostgresTasksFixture _fixture;

    public PurgeTaskTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The retention floor is the row's own `expires_at`: records past it are gone and
    /// live records stay, and the fire that did it ends `done` — which is also the
    /// proof that the `evidgate_worker_sched` call succeeded, because a role without
    /// `EXECUTE` (or a missing credential) would have left the task in `retry` with
    /// the error in `last_error`.
    /// </summary>
    [Fact]
    public async Task PurgeTask_DeletesOnlyExpired()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        var expired = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        foreach (var id in expired)
        {
            await SeedRecordAsync(owner, id, createdDaysAgo: 3, expiresHoursAgo: 24);
        }

        var live = Guid.CreateVersion7();
        await SeedRecordAsync(owner, live, createdDaysAgo: 0, expiresHoursAgo: -24);

        await using var host = TasksTestHost.Start(_fixture, database);

        // The cron fires the purge as a platform task (no org).
        (await host.Runner.RunOnceAsync(PurgeTime, Token)).ShouldBe(1);
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select org_id is null from public.task where schedule_name = @name",
            ("name", EvidgateSweeps.IdempotencyPurgeSchedule))).ShouldBeTrue();

        // The consumer claims and runs it; the handler needs the sweep role's EXECUTE.
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(
            owner,
            "select state from public.task where schedule_name = @name",
            ("name", EvidgateSweeps.IdempotencyPurgeSchedule))).ShouldBe("done");

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.idempotency_record where id = any(@ids)",
            ("ids", expired))).ShouldBe(0, "records past expires_at are purged");

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.idempotency_record where id = @id",
            ("id", live))).ShouldBe(1, "a record whose retention has not run out stays");
    }

    /// <summary>
    /// The row as the real schema defines it: `expires_at >= created_at + 24 h`, and
    /// `project_id` is NULL for an org-scoped record (T94), so no project has to exist.
    /// </summary>
    private static Task<int> SeedRecordAsync(string ownerConnectionString, Guid id, int createdDaysAgo, int expiresHoursAgo) =>
        TaskSql.ExecuteAsync(
            ownerConnectionString,
            """
            insert into public.idempotency_record
              (id, org_id, project_id, operation, key, request_hash, response_status, response_body,
               created_at, updated_at, expires_at)
            values
              (@id, @org_id, null, 'purge-test', @key, 'sha256:test',
               200, '{}'::jsonb,
               now() - make_interval(days => @created_days), now() - make_interval(days => @created_days),
               now() - make_interval(hours => @expires_hours))
            """,
            ("id", id),
            ("org_id", Guid.CreateVersion7()),
            ("key", id.ToString("N")),
            ("created_days", createdDaysAgo),
            ("expires_hours", expiresHoursAgo));

    /// <summary>
    /// The purge payload is a task row an operator can replay, so it is not trusted to
    /// stay in range. The owner function caps a batch at
    /// `least(greatest(p_limit, 0), 10000)` (0007): a payload above the cap must still
    /// drain every batch of the fire, and a non-positive one must still delete rather
    /// than ask for zero rows fifty times.
    /// </summary>
    [Fact]
    public async Task PurgeTask_ClampsTheLimitToTheFunctionCap()
    {
        const int expired = 10_050;
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredRecordsAsync(owner, expired);

        // 20000 is twice the function's own cap: the fire must take two batches
        // (10000 + 50), not read the capped first batch as a drained backlog.
        await TaskSql.SeedTaskAsync(
            owner,
            EvidgateSweeps.MaintenanceQueue,
            EvidgateSweeps.IdempotencyPurgeTaskType,
            payload: """{"limit":20000}""",
            idempotencyKey: "purge-clamp-high");

        await using var host = TasksTestHost.Start(_fixture, database);

        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await CountExpiredAsync(owner)).ShouldBe(0, "the fire drains past the function's per-call cap");
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'purge-clamp-high'"))
            .ShouldBe("done");
    }

    /// <summary>A non-positive limit is clamped to one record per batch instead of deleting nothing.</summary>
    [Fact]
    public async Task PurgeTask_NonPositiveLimitStillDeletes()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await SeedExpiredRecordsAsync(owner, 3);

        await TaskSql.SeedTaskAsync(
            owner,
            EvidgateSweeps.MaintenanceQueue,
            EvidgateSweeps.IdempotencyPurgeTaskType,
            payload: """{"limit":0}""",
            idempotencyKey: "purge-clamp-zero");

        await using var host = TasksTestHost.Start(_fixture, database);

        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await CountExpiredAsync(owner)).ShouldBe(0, "a zero limit must still purge, not spin 50 no-op batches");
    }

    private static Task<int> SeedExpiredRecordsAsync(string ownerConnectionString, int count) =>
        TaskSql.ExecuteAsync(
            ownerConnectionString,
            """
            insert into public.idempotency_record
              (id, org_id, project_id, operation, key, request_hash, created_at, expires_at)
            select gen_random_uuid(), '11111111-1111-7111-8111-111111111111', null, 'purge-clamp', 'k:' || i,
                   'sha256:test', now() - interval '3 days', now() - interval '1 hour'
              from generate_series(1, @count) i
            """,
            ("count", count));

    private static Task<long> CountExpiredAsync(string ownerConnectionString) =>
        TaskSql.ScalarAsync<long>(
            ownerConnectionString,
            "select count(*) from public.idempotency_record where operation = 'purge-clamp'");
}
