using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 02.3.2-T2.1 / §7 (AC6, retention): the <c>session-cleanup</c> schedule stamps <c>expired_at</c> on the rows past
/// their idle or absolute deadline, hard-deletes the rows whose last deadline passed more than 30 days ago — revoked
/// ones included — leaves every live row alone, and fires once per schedule time. Nothing is stubbed below the task
/// mechanism: the handler runs the real <see cref="ISessionRoutesStore"/> over the real definer of migration 0048.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SessionCleanupTaskTests(PostgresTasksFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTime Now = DateTime.UtcNow;

    /// <summary>
    /// The two state transitions of §6 the sweep owns: an unrevoked row past either deadline gets its
    /// <c>expired_at</c> stamp (once — expiry itself stays derived), a row whose greatest deadline is older than
    /// 30 days is gone (the revoked rows the retention rule also covers), and a live row plus a recently revoked one
    /// are untouched.
    /// </summary>
    [Fact]
    public async Task Cleanup_StampsExpiredRows_DeletesPastRetention_LeavesLiveRowsAlone()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        var user = await SeedUserAsync(owner);

        var live = await SeedSessionAsync(owner, user, Now.AddHours(1), Now.AddHours(2));
        var idleExpired = await SeedSessionAsync(owner, user, Now.AddHours(-1), Now.AddHours(2));
        var absoluteExpired = await SeedSessionAsync(owner, user, Now.AddHours(-2), Now.AddHours(-1));
        var recentlyRevoked = await SeedSessionAsync(owner, user, Now.AddHours(-1), Now.AddHours(2), revokedAt: Now.AddHours(-1));
        var pastRetention = await SeedSessionAsync(owner, user, Now.AddDays(-32), Now.AddDays(-31));
        var pastRetentionRevoked = await SeedSessionAsync(owner, user, Now.AddDays(-45), Now.AddDays(-40), revokedAt: Now.AddDays(-45));

        await using var host = Start(database);
        await FireAsync(owner, host, limit: 100, idempotencyKey: "session-cleanup-states");

        (await ExistsAsync(owner, live)).ShouldBeTrue("a live session is never touched");
        (await ExpiredStampAsync(owner, live)).ShouldBeFalse("a live session is not expired");

        (await ExistsAsync(owner, idleExpired)).ShouldBeTrue("an idle-expired row is stamped, not deleted, inside 30 days");
        (await ExpiredStampAsync(owner, idleExpired)).ShouldBeTrue("a row past its idle deadline is stamped");

        (await ExpiredStampAsync(owner, absoluteExpired)).ShouldBeTrue("a row past its absolute deadline is stamped");

        (await ExistsAsync(owner, recentlyRevoked)).ShouldBeTrue("a revocation inside the retention window survives");
        (await ExpiredStampAsync(owner, recentlyRevoked)).ShouldBeFalse("the stamping predicate is unrevoked-only");

        (await ExistsAsync(owner, pastRetention)).ShouldBeFalse("a row 30 days past its last deadline is deleted");
        (await ExistsAsync(owner, pastRetentionRevoked)).ShouldBeFalse("the 30-day delete covers revoked rows too");
    }

    /// <summary>
    /// The sweep is idempotent by its predicates: a stamped row is not stamped again (its instant stays identical)
    /// and the delete has nothing left to remove, so a rescued fire or a manual replay converges on the same state.
    /// </summary>
    [Fact]
    public async Task Cleanup_IsIdempotent_ASecondFireChangesNothing()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        var user = await SeedUserAsync(owner);

        var expired = await SeedSessionAsync(owner, user, Now.AddHours(-1), Now.AddHours(2));
        var deleted = await SeedSessionAsync(owner, user, Now.AddDays(-31), Now.AddDays(-30).AddHours(-1));

        await using var host = Start(database);

        await FireAsync(owner, host, limit: 100, idempotencyKey: "session-cleanup-idempotent-first");
        var stampAfterFirst = await ExpiredStampEpochAsync(owner, expired);
        stampAfterFirst.ShouldNotBeNull();
        (await ExistsAsync(owner, deleted)).ShouldBeFalse();

        await FireAsync(owner, host, limit: 100, idempotencyKey: "session-cleanup-idempotent-second");

        (await ExpiredStampEpochAsync(owner, expired)).ShouldBe(stampAfterFirst!.Value, "an already stamped row is not stamped again");
        (await ExistsAsync(owner, expired)).ShouldBeTrue();
        (await SessionCountAsync(owner)).ShouldBe(1, "the second fire deleted nothing new");
    }

    /// <summary>
    /// The payload is an operator-replayable task row, so a small batch must still drain the whole backlog of one
    /// fire: five expired rows at two per call is four calls (2, 2, the remaining 1, then the empty call that ends
    /// the loop). A single call would have left three rows behind. The per-call bound itself is pinned at the SQL
    /// level (<c>SessionCleanupSqlTests.Cleanup_Stamps_At_Most_The_Limit_Per_Call</c>), because the handler only
    /// exposes the totals.
    /// </summary>
    [Fact]
    public async Task Cleanup_HonoursTheBatchLimitAndDrainsTheBacklog()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        var user = await SeedUserAsync(owner);

        for (var i = 0; i < 5; i++)
        {
            await SeedSessionAsync(owner, user, Now.AddHours(-1), Now.AddHours(2));
        }

        await using var host = Start(database);
        await FireAsync(owner, host, limit: 2, idempotencyKey: "session-cleanup-batches");

        (await StampedCountAsync(owner)).ShouldBe(5, "the handler loops until a batch comes back short of the limit");
    }

    /// <summary>
    /// The 30-day delete is leaf first (§7, AC6) and ONE fire still removes the WHOLE chain: the self-FK
    /// has no <c>ON DELETE</c>, so each call can only remove the current tip, and the handler keeps calling
    /// until a call comes back empty. A three-row chain therefore leaves nothing behind after one fire — the
    /// retention rule holds for every row of the chain, not one layer per daily fire.
    /// </summary>
    [Fact]
    public async Task Cleanup_Deletes_A_Whole_Rotated_Chain_In_One_Fire()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        var user = await SeedUserAsync(owner);

        var live = await SeedSessionAsync(owner, user, Now.AddHours(1), Now.AddHours(2));
        var root = await SeedSessionAsync(owner, user, Now.AddDays(-42), Now.AddDays(-41));
        var middle = await SeedSessionAsync(owner, user, Now.AddDays(-40), Now.AddDays(-39), rotatedFrom: root);
        var tip = await SeedSessionAsync(owner, user, Now.AddDays(-38), Now.AddDays(-37), rotatedFrom: middle);

        await using var host = Start(database);

        await FireAsync(owner, host, limit: 100, idempotencyKey: "session-cleanup-chain");

        foreach (var ended in new[] { tip, middle, root })
        {
            (await ExistsAsync(owner, ended)).ShouldBeFalse(
                "one fire drains the chain: each call removes the new tip until a call comes back empty");
        }

        (await ExistsAsync(owner, live)).ShouldBeTrue("a live session is never deleted");
        (await SessionCountAsync(owner)).ShouldBe(1);
    }

    /// <summary>
    /// One fire spends exactly <see cref="SessionCleanupSweeps.MaxBatchesPerFire"/> batches and no more: with
    /// <c>limit: 1</c> every call stamps exactly one row, so the fire's total IS the batch count. A backlog
    /// larger than the cap is left for the next daily fire instead of holding one long lock, and nothing is
    /// lost — the next fire finishes it.
    /// </summary>
    [Fact]
    public async Task Cleanup_StopsAtMaxBatchesPerFire()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        var user = await SeedUserAsync(owner);

        const int rows = 60;
        for (var i = 0; i < rows; i++)
        {
            await SeedSessionAsync(owner, user, Now.AddHours(-1), Now.AddHours(2));
        }

        await using var host = Start(database);

        await FireAsync(owner, host, limit: 1, idempotencyKey: "session-cleanup-capped");

        (await StampedCountAsync(owner)).ShouldBe(
            SessionCleanupSweeps.MaxBatchesPerFire,
            "limit 1 stamps exactly one row per batch, so the fire stops at exactly the cap — not sooner, not later");

        await FireAsync(owner, host, limit: 1, idempotencyKey: "session-cleanup-capped-second");

        (await StampedCountAsync(owner)).ShouldBe(rows, "the next fire finishes the backlog the cap deferred");
    }

    /// <summary>
    /// §7: one platform fire per schedule time — the unique <c>(schedule_name, fire_at)</c> key is what makes a fleet
    /// of workers fire once — with the documented payload and task type, at the cadence
    /// <see cref="Cleanup_Schedule_FiresAtTheNextOccurrence_NotBefore"/> pins behaviourally. The constants are not
    /// compared to copies of their own literals here: the assertions below read what the task row really carries.
    /// </summary>
    [Fact]
    public async Task Cleanup_Schedule_FiresOncePerTime_AtTheDocumentedCadence()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        await using var host = Start(database);

        // Just after the registered daily fire (03:40 UTC).
        var at = new DateTimeOffset(2026, 10, 26, 3, 40, 30, TimeSpan.Zero);

        var fired = await Task.WhenAll(host.Runner.RunOnceAsync(at, Token), host.Runner.RunOnceAsync(at, Token));
        fired.Sum().ShouldBe(1);
        (await host.Runner.RunOnceAsync(at, Token)).ShouldBe(0);

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where schedule_name = @n",
            ("n", SessionCleanupSweeps.ScheduleName))).ShouldBe(1);
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select org_id is null from public.task where schedule_name = @n",
            ("n", SessionCleanupSweeps.ScheduleName))).ShouldBeTrue("the session sweep is a platform task");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select payload::jsonb ->> 'limit' from public.task where schedule_name = @n",
            ("n", SessionCleanupSweeps.ScheduleName))).ShouldBe("10000");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select task_type from public.task where schedule_name = @n",
            ("n", SessionCleanupSweeps.ScheduleName))).ShouldBe(SessionCleanupSweeps.TaskType);
    }

    /// <summary>
    /// §7 (QA G9): what decides a fire is the cron occurrence, not the poll. A pass at 03:39:59 is still before
    /// the 03:40 occurrence and inserts nothing; the pass that crosses it inserts one task row; and the NEXT
    /// day's occurrence is a different <c>fire_at</c>, so it inserts a second row instead of being collapsed by
    /// the first one's unique key.
    /// </summary>
    [Fact]
    public async Task Cleanup_Schedule_FiresAtTheNextOccurrence_NotBefore()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        await using var host = Start(database);

        var before = new DateTimeOffset(2026, 10, 26, 3, 39, 59, TimeSpan.Zero);
        (await host.Runner.RunOnceAsync(before, Token)).ShouldBe(0, "the 03:40 occurrence is not in the window yet");

        var first = new DateTimeOffset(2026, 10, 26, 3, 40, 30, TimeSpan.Zero);
        (await host.Runner.RunOnceAsync(first, Token)).ShouldBe(1, "the pass that crosses 03:40 fires");

        (await host.Runner.RunOnceAsync(first.AddDays(1), Token))
            .ShouldBe(1, "the next day's 03:40 is a different fire_at, so the key does not collapse it");

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where schedule_name = @n",
            ("n", SessionCleanupSweeps.ScheduleName))).ShouldBe(2);

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(distinct fire_at) from public.task where schedule_name = @n",
            ("n", SessionCleanupSweeps.ScheduleName))).ShouldBe(2, "one row per occurrence, one occurrence per row");
    }

    /// <summary>
    /// The handler is registered under the task type and declares itself a platform task, so the consumer never
    /// opens a tenant scope for a table that carries no <c>org_id</c>.
    /// </summary>
    [Fact]
    public async Task Cleanup_IsRegisteredAsAPlatformHandler()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var host = Start(database);

        host.Handlers.TryGet(SessionCleanupSweeps.TaskType, out var invoker).ShouldBeTrue();
        invoker!.TaskType.ShouldBe("session.cleanup");
        invoker.RequiresTenant.ShouldBeFalse("public.session is platform-scoped (02.3.2 D12)");
    }

    private TasksTestHost Start(string database) =>
        TasksTestHost.Start(
            fixture,
            database,
            services: services =>
            {
                // The production composition of the session store over the host's own data sources: the handler
                // reads rows only through it, never with SQL of its own. No secret provider is registered here:
                // TasksOptionsSecretPostConfigure would blank the in-memory sweep connection string.
                services.AddSessions(new ConfigurationBuilder().Build());
                services.AddTaskHandler<SessionCleanupPayload, SessionCleanupTaskHandler>();
                services.AddTaskSchedule(SessionCleanupSweeps.Cleanup);
            });

    private static async Task FireAsync(string owner, TasksTestHost host, int limit, string idempotencyKey)
    {
        await TaskSql.SeedTaskAsync(
            owner,
            EvidgateSweeps.MaintenanceQueue,
            SessionCleanupSweeps.TaskType,
            payload: $$"""{"limit":{{limit}}}""",
            orgId: null,
            idempotencyKey: idempotencyKey);

        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select state from public.task where idempotency_key = @key",
            ("key", idempotencyKey))).ShouldBe("done");
    }

    private static async Task<Guid> SeedUserAsync(string owner)
    {
        var user = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(
            owner,
            """insert into public."user" (id, email, status) values (@id, @email, 'active')""",
            ("id", user),
            ("email", $"session-sweep-{user:N}@example.test"));
        return user;
    }

    /// <summary>
    /// One <c>session</c> row with the whole deadline triple spelled out: <paramref name="expiresAt"/> (idle) must
    /// never exceed <paramref name="absoluteExpiresAt"/>, which the table's own check enforces.
    /// </summary>
    private static async Task<Guid> SeedSessionAsync(
        string owner,
        Guid user,
        DateTime expiresAt,
        DateTime absoluteExpiresAt,
        DateTime? revokedAt = null,
        Guid? rotatedFrom = null)
    {
        var id = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(
            owner,
            """
            insert into public.session
              (id, user_id, expires_at, absolute_expires_at, cookie_hmac, pepper_version, revoked_at, rotated_from)
            values (@id, @user, @expires, @absolute, @hmac, 1, @revoked, @rotated)
            """,
            ("id", id),
            ("user", user),
            ("expires", expiresAt),
            ("absolute", absoluteExpiresAt),
            ("hmac", RandomNumberGenerator.GetBytes(32)),
            ("revoked", revokedAt),
            ("rotated", rotatedFrom));
        return id;
    }

    private static Task<bool> ExistsAsync(string owner, Guid session) =>
        TaskSql.ScalarAsync<bool>(
            owner,
            "select exists (select 1 from public.session where id = @id)",
            ("id", session));

    private static Task<bool> ExpiredStampAsync(string owner, Guid session) =>
        TaskSql.ScalarAsync<bool>(
            owner,
            "select expired_at is not null from public.session where id = @id",
            ("id", session));

    private static Task<double?> ExpiredStampEpochAsync(string owner, Guid session) =>
        TaskSql.ScalarAsync<double?>(
            owner,
            "select extract(epoch from expired_at)::double precision from public.session where id = @id",
            ("id", session));

    private static Task<long> StampedCountAsync(string owner) =>
        TaskSql.ScalarAsync<long>(owner, "select count(*) from public.session where expired_at is not null");

    private static Task<long> SessionCountAsync(string owner) =>
        TaskSql.ScalarAsync<long>(owner, "select count(*) from public.session");
}
