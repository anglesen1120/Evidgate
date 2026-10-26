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
    /// fire: five expired rows at two per call is three calls (2, 2, then the short 1 that ends the loop). A single
    /// call would have left three rows behind.
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
    /// The 30-day delete is leaf first (§7, AC6): <c>session.rotated_from</c> is a self-FK with no
    /// <c>ON DELETE</c>, so one call that selected a whole chain must still be able to finish — it deletes the
    /// successor and leaves the predecessor, which the next call then removes. Nothing is lost and no batch fails on
    /// the foreign key.
    /// </summary>
    [Fact]
    public async Task Cleanup_DeletesLeafFirst_ARotatedChainTakesTwoCalls()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        var user = await SeedUserAsync(owner);

        var predecessor = await SeedSessionAsync(owner, user, Now.AddDays(-40), Now.AddDays(-39));
        var successor = await SeedSessionAsync(owner, user, Now.AddDays(-38), Now.AddDays(-37), rotatedFrom: predecessor);

        await using var host = Start(database);

        await FireAsync(owner, host, limit: 100, idempotencyKey: "session-cleanup-chain-first");

        (await ExistsAsync(owner, successor)).ShouldBeFalse("the leaf of the chain is deleted first");
        (await ExistsAsync(owner, predecessor)).ShouldBeTrue(
            "the row the delete statement selected still has a referring row in its own snapshot, so it waits");

        await FireAsync(owner, host, limit: 100, idempotencyKey: "session-cleanup-chain-second");

        (await ExistsAsync(owner, predecessor)).ShouldBeFalse("with the leaf gone the predecessor is deletable");
        (await SessionCountAsync(owner)).ShouldBe(0);
    }

    /// <summary>
    /// One fire is bounded by <see cref="SessionCleanupSweeps.MaxBatchesPerFire"/>: a backlog larger than the cap is
    /// left for the next daily fire instead of holding one long lock, and nothing is lost — the next fire finishes it.
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

        var afterFirstFire = await StampedCountAsync(owner);
        afterFirstFire.ShouldBeGreaterThan(0);
        afterFirstFire.ShouldBeLessThanOrEqualTo(SessionCleanupSweeps.MaxBatchesPerFire, "one fire never drains more than the cap");
        afterFirstFire.ShouldBeLessThan(rows, "the cap leaves the rest of the backlog for the next fire");

        await FireAsync(owner, host, limit: 1, idempotencyKey: "session-cleanup-capped-second");

        (await StampedCountAsync(owner)).ShouldBe(rows, "the next fire finishes the backlog the cap deferred");
    }

    /// <summary>
    /// §7: one platform fire per schedule time — the unique <c>(schedule_name, fire_at)</c> key is what makes a fleet
    /// of workers fire once — at the documented daily cadence, with the documented payload and task type.
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

        SessionCleanupSweeps.TaskType.ShouldBe("session.cleanup");
        SessionCleanupSweeps.ScheduleName.ShouldBe("session-cleanup");
        SessionCleanupSweeps.Cron.ShouldBe("40 3 * * *");
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
