using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Invites;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 02.1.2-T2.1 / §7 (AC6): the <c>invite-expiry</c> schedule expires the memberships
/// whose invite has lapsed — and only those — through the owner definer
/// <c>evidgate.expire_pending_invites</c> as <c>evidgate_worker_sched</c>, in bounded
/// batches, once per schedule time. Nothing is stubbed: the rows are flipped by the
/// real function, on the real sweep role.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class InviteExpiryTaskTests
{
    private const string ExpireFunction = "evidgate.expire_pending_invites(integer)";
    private const string ExpireCall = "select evidgate.expire_pending_invites(@limit)";

    private readonly PostgresTasksFixture _fixture;

    public InviteExpiryTaskTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The state machine of §6: a past-due <c>pending</c> invite becomes
    /// <c>expired</c>; a future-dated one, an <c>active</c> member and a
    /// <c>revoked</c> invite stay exactly as they are. The sweep is platform-wide, so
    /// the second org's past-due invite expires in the same fire.
    /// </summary>
    [Fact]
    public async Task Expiry_FlipsOnlyPendingPastDueInvites()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var (orgA, orgB) = await SeedOrgsAsync(owner);

        var pastDueA = Guid.CreateVersion7();
        var futureA = Guid.CreateVersion7();
        var activeA = Guid.CreateVersion7();
        var revokedA = Guid.CreateVersion7();
        var pastDueB = Guid.CreateVersion7();

        await SeedInviteAsync(owner, orgA, pastDueA, "past-due-a@example.test", "pending", "now() - interval '1 hour'");
        await SeedInviteAsync(owner, orgA, futureA, "future-a@example.test", "pending", "now() + interval '1 hour'");
        await SeedActiveAsync(owner, orgA, activeA, "active-a@example.test");
        await SeedInviteAsync(owner, orgA, revokedA, "revoked-a@example.test", "revoked", "now() - interval '2 hours'");
        await SeedInviteAsync(owner, orgB, pastDueB, "past-due-b@example.test", "pending", "now() - interval '7 days'");

        await TaskSql.SeedTaskAsync(
            owner,
            EvidgateSweeps.MaintenanceQueue,
            InviteExpirySweeps.TaskType,
            payload: """{"limit":100}""",
            orgId: null,
            idempotencyKey: "invite-expiry-flip");

        await using var host = Start(database);
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(
            owner, "select state from public.task where idempotency_key = 'invite-expiry-flip'")).ShouldBe("done");

        (await StatusOfAsync(owner, pastDueA)).ShouldBe("expired", "a pending invite past invite_expires_at expires");
        (await StatusOfAsync(owner, pastDueB)).ShouldBe("expired", "the sweep is platform-wide, not tenant-scoped");
        (await StatusOfAsync(owner, futureA)).ShouldBe("pending", "an invite before invite_expires_at stays pending");
        (await StatusOfAsync(owner, activeA)).ShouldBe("active", "a bound member is never expired");
        (await StatusOfAsync(owner, revokedA)).ShouldBe("revoked", "a revoked invite keeps its terminal state");
    }

    /// <summary>
    /// The fire key is the natural key of the work (A10 / 01.5.1 §5): a second run
    /// over the same rows flips nothing and still ends <c>done</c>, and the row keeps
    /// the version the first run left behind — the second call updated no row.
    /// </summary>
    [Fact]
    public async Task Expiry_IsIdempotent_SecondRunFlipsNothing()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var (org, _) = await SeedOrgsAsync(owner);

        var membership = Guid.CreateVersion7();
        await SeedInviteAsync(owner, org, membership, "twice@example.test", "pending", "now() - interval '1 hour'");

        await using var host = Start(database);

        await TaskSql.SeedTaskAsync(
            owner, EvidgateSweeps.MaintenanceQueue, InviteExpirySweeps.TaskType,
            payload: """{"limit":100}""", orgId: null, idempotencyKey: "invite-expiry-first");
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await StatusOfAsync(owner, membership)).ShouldBe("expired");
        var afterFirst = await VersionOfAsync(owner, membership);

        await TaskSql.SeedTaskAsync(
            owner, EvidgateSweeps.MaintenanceQueue, InviteExpirySweeps.TaskType,
            payload: """{"limit":100}""", orgId: null, idempotencyKey: "invite-expiry-second");
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(
            owner, "select state from public.task where idempotency_key = 'invite-expiry-second'")).ShouldBe("done");
        (await StatusOfAsync(owner, membership)).ShouldBe("expired");
        (await VersionOfAsync(owner, membership)).ShouldBe(afterFirst, "an idempotent second run updates no row");
    }

    /// <summary>
    /// The payload is an operator-replayable task row, so a small batch must still
    /// drain the whole backlog of one fire: five past-due invites at two per call is
    /// three calls (2, 2, then the short 1 that ends the loop).
    /// </summary>
    [Fact]
    public async Task Expiry_HonoursTheBatchLimitAndDrainsTheBacklog()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var (org, _) = await SeedOrgsAsync(owner);

        for (var index = 0; index < 5; index++)
        {
            await SeedInviteAsync(
                owner, org, Guid.CreateVersion7(), $"batch-{index}@example.test", "pending", "now() - interval '2 hours'");
        }

        await TaskSql.SeedTaskAsync(
            owner, EvidgateSweeps.MaintenanceQueue, InviteExpirySweeps.TaskType,
            payload: """{"limit":2}""", orgId: null, idempotencyKey: "invite-expiry-batches");

        await using var host = Start(database);
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<long>(
            owner, "select count(*) from public.membership where status = 'pending'")).ShouldBe(0, "the loop drains every batch");
        (await TaskSql.ScalarAsync<long>(
            owner, "select count(*) from public.membership where status = 'expired'")).ShouldBe(5);
        (await TaskSql.ScalarAsync<string>(
            owner, "select state from public.task where idempotency_key = 'invite-expiry-batches'")).ShouldBe("done");
    }

    /// <summary>
    /// A non-positive limit is clamped to one row per call instead of asking for zero
    /// rows fifty times: the function caps a batch at
    /// <c>least(greatest(p_limit, 0), 10000)</c>, and a payload of 0 must still expire
    /// what is due.
    /// </summary>
    [Fact]
    public async Task Expiry_NonPositiveLimitStillExpires()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var (org, _) = await SeedOrgsAsync(owner);

        for (var index = 0; index < 2; index++)
        {
            await SeedInviteAsync(
                owner, org, Guid.CreateVersion7(), $"clamp-{index}@example.test", "pending", "now() - interval '1 hour'");
        }

        await TaskSql.SeedTaskAsync(
            owner, EvidgateSweeps.MaintenanceQueue, InviteExpirySweeps.TaskType,
            payload: """{"limit":0}""", orgId: null, idempotencyKey: "invite-expiry-clamp");

        await using var host = Start(database);
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<long>(
            owner, "select count(*) from public.membership where status = 'pending'")).ShouldBe(0);
    }

    /// <summary>
    /// §6/§7: one platform fire per schedule time — the unique
    /// <c>(schedule_name, fire_at)</c> key is what makes a fleet of workers fire once —
    /// at the documented hourly cadence, with the documented payload.
    /// </summary>
    [Fact]
    public async Task Expiry_Schedule_FiresOncePerTime_AtTheDocumentedCadence()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = Start(database);

        // Just after the registered hourly fire (minute 5).
        var at = new DateTimeOffset(2026, 10, 25, 4, 5, 30, TimeSpan.Zero);

        var fired = await Task.WhenAll(host.Runner.RunOnceAsync(at, Token), host.Runner.RunOnceAsync(at, Token));
        fired.Sum().ShouldBe(1);
        (await host.Runner.RunOnceAsync(at, Token)).ShouldBe(0);

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where schedule_name = @n",
            ("n", InviteExpirySweeps.TaskType))).ShouldBe(1);
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select org_id is null from public.task where schedule_name = @n",
            ("n", InviteExpirySweeps.TaskType))).ShouldBeTrue("the expiry sweep is a platform task");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select payload::jsonb ->> 'limit' from public.task where schedule_name = @n",
            ("n", InviteExpirySweeps.TaskType))).ShouldBe("10000");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select task_type from public.task where schedule_name = @n",
            ("n", InviteExpirySweeps.TaskType))).ShouldBe(InviteExpirySweeps.TaskType);
    }

    /// <summary>
    /// T80 shape: the sweep is an owner <c>SECURITY DEFINER</c> function with an empty
    /// <c>search_path</c>, its <c>EXECUTE</c> grant is scoped to the sweep role, and
    /// the app role is refused by the database itself. The app role also cannot flip a
    /// past-due invite with a plain <c>UPDATE</c>: without a tenant scope it sees no
    /// rows, so the handler's effect can only have come from the sweep role.
    /// </summary>
    [Fact]
    public async Task Expiry_RunsOnlyAsTheSweepRole()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var (org, _) = await SeedOrgsAsync(owner);

        var membership = Guid.CreateVersion7();
        await SeedInviteAsync(owner, org, membership, "sweep-role@example.test", "pending", "now() - interval '1 hour'");

        await using var superuser = await _fixture.OpenSuperuserAsync(database);

        var definition = await OutboxIdempotencyTestHost.ScalarAsync<string>(
            superuser,
            """
            select p.prosecdef::text || '|' || pg_catalog.pg_get_userbyid(p.proowner) || '|'
                   || coalesce(array_to_string(p.proconfig, ','), '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate' and p.proname = 'expire_pending_invites'
            """,
            Token);
        definition.ShouldBe("true|evidgate_owner|search_path=\"\"");

        (await OutboxIdempotencyTestHost.ScalarAsync<bool>(
            superuser,
            $"select pg_catalog.has_function_privilege('evidgate_app', '{ExpireFunction}', 'execute')",
            Token)).ShouldBeFalse();
        (await OutboxIdempotencyTestHost.ScalarAsync<bool>(
            superuser,
            $"select pg_catalog.has_function_privilege('evidgate_worker_sched', '{ExpireFunction}', 'execute')",
            Token)).ShouldBeTrue();

        await using var app = await OutboxIdempotencyTestHost.OpenAsync(_fixture.AppConnectionString(database), Token);

        var denied = await Should.ThrowAsync<PostgresException>(
            () => OutboxIdempotencyTestHost.ScalarAsync<int>(app, ExpireCall, Token, ("limit", 10)));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var flippedByApp = await OutboxIdempotencyTestHost.ExecuteAsync(
            app,
            "update public.membership set status = 'expired' where id = @id",
            Token,
            ("id", membership));
        flippedByApp.ShouldBe(0, "without a tenant scope the app role sees no membership row");
        (await StatusOfAsync(owner, membership)).ShouldBe("pending");

        await using var host = Start(database);
        host.Handlers.TryGet(InviteExpirySweeps.TaskType, out var invoker).ShouldBeTrue();
        invoker!.RequiresTenant.ShouldBeFalse("the sweep is a platform task and needs no tenant scope");
        typeof(InviteExpiryTaskHandler)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .ShouldNotContain(
                parameter => parameter.ParameterType == typeof(NpgsqlDataSource),
                "the handler reads rows only through the sweep role, never the app data source");

        await TaskSql.SeedTaskAsync(
            owner, EvidgateSweeps.MaintenanceQueue, InviteExpirySweeps.TaskType,
            payload: """{"limit":100}""", orgId: null, idempotencyKey: "invite-expiry-role");
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);
        (await StatusOfAsync(owner, membership)).ShouldBe("expired");
    }

    private TasksTestHost Start(string database) =>
        TasksTestHost.Start(
            _fixture,
            database,
            services: services =>
            {
                services.AddTaskHandler<InviteExpiryPayload, InviteExpiryTaskHandler>();
                services.AddTaskSchedule(InviteExpirySweeps.Expire);
            });

    private static async Task<(Guid OrgA, Guid OrgB)> SeedOrgsAsync(string owner)
    {
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(
            owner,
            """
            insert into public.organization (id, name, slug) values (@org_a, 'Acme', 'acme');
            insert into public.organization (id, name, slug) values (@org_b, 'Beta', 'beta');
            """,
            ("org_a", orgA),
            ("org_b", orgB));

        return (orgA, orgB);
    }

    /// <summary>
    /// A membership row in the shape 0043 defines: the invite triple is all present
    /// (prefix, 32-byte hash, pepper version) or all absent, and a row carrying one
    /// carries <c>invite_expires_at</c> too.
    /// </summary>
    private static Task<int> SeedInviteAsync(
        string owner,
        Guid orgId,
        Guid membershipId,
        string email,
        string status,
        string expiresAtSql) =>
        TaskSql.ExecuteAsync(
            owner,
            $"""
            insert into public.membership (id, org_id, invited_email, status, invite_secret_prefix, invite_token_hash, invite_pepper_version, invite_expires_at)
            values (@id, @org, @email, @status, @prefix, @hash, 1, {expiresAtSql})
            """,
            ("id", membershipId),
            ("org", orgId),
            ("email", email),
            ("status", status),
            // Not derived from the v7 id: its first 12 hex characters are the
            // millisecond timestamp, so two rows seeded in the same millisecond would
            // collide on membership_invite_secret_prefix_key (0011).
            ("prefix", Guid.NewGuid().ToString("N")[..12]),
            ("hash", new byte[32]));

    private static Task<int> SeedActiveAsync(string owner, Guid orgId, Guid membershipId, string email) =>
        TaskSql.ExecuteAsync(
            owner,
            "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org, @user, @email, 'active')",
            ("id", membershipId),
            ("org", orgId),
            ("user", Guid.CreateVersion7()),
            ("email", email));

    private static Task<string?> StatusOfAsync(string owner, Guid membershipId) =>
        TaskSql.ScalarAsync<string>(
            owner, "select status from public.membership where id = @id", ("id", membershipId));

    private static Task<int> VersionOfAsync(string owner, Guid membershipId) =>
        TaskSql.ScalarAsync<int>(
            owner, "select version from public.membership where id = @id", ("id", membershipId));
}
