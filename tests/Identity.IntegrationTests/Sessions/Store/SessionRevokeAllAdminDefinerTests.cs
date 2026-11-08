using System.Diagnostics;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// The administrative revoke-all at the seam the route uses: one call ends every live session of a target member
/// and appends ONE audit row inside the same transaction, in the target's org chain.
/// </summary>
/// <remarks>
/// <para>
/// Every write under test runs through the production statement — the store over the app-role data source — while
/// rows and grants are read as the owner, because the app role holds no privilege on <c>public.session</c>. The
/// row's hash is never re-implemented: a row is only accepted when the existing <c>AuditChainVerifier</c> reads
/// the chain as unbroken.
/// </para>
/// <para>
/// The concurrency cases hold the per-user lock explicitly and prove from <c>pg_locks</c> that the call parks on
/// it before it touches a session row or the org's chain lock, so the order is observed rather than assumed.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionRevokeAllAdminDefinerTests : SessionHardeningTestBase
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    /// <summary>The action string the definer writes; it lives in SQL, so the literal is pinned here.</summary>
    private const string AdminRevokeAction = "auth.session_revoke_admin";

    /// <summary>A request id the edge accepts: visible ASCII, 1..128 characters.</summary>
    private const string SampleRequestId = "req-s302-admin-revoke-all";

    /// <summary>A W3C trace id: 32 lowercase hex digits, not the all-zero value the standard refuses.</summary>
    private const string SampleTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    /// <summary>The argument list the definer must keep, in order.</summary>
    private const string ExpectedArguments =
        "p_cookie_hmacs bytea[], p_target_user_id uuid, p_org_id uuid, p_now timestamp with time zone, "
        + "p_request_id text, p_trace_id text";

    /// <summary>Own database: the rollback case installs a trigger on <c>public.audit_log</c>.</summary>
    public SessionRevokeAllAdminDefinerTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// The administered call: both live rows of the target are revoked, the caller's own row is not, and exactly
    /// one row is appended — the admin as actor, the TARGET user as object, the count and the target in its
    /// metadata, and the call's correlation ids on the row.
    /// </summary>
    [Fact]
    public async Task An_Admin_Call_Writes_One_Row_For_The_Target_Member()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var first = await CreateSessionAsync(member, clock);
        var second = await CreateSessionAsync(member, clock);

        var (status, revoked) = await CreateRoutes(clock)
            .RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), member, org, SampleRequestId, SampleTraceId, Token);

        status.ShouldBe(SessionRouteStatus.Done);
        revoked.ShouldBe(2, "both live rows of the target are ended by one call");
        (await LiveCountAsync(member)).ShouldBe(0L);
        (await IsLiveAsync(adminTicket.SessionId)).ShouldBeTrue("the caller's own session is never the target");
        (await IsLiveAsync(first.SessionId)).ShouldBeFalse();
        (await IsLiveAsync(second.SessionId)).ShouldBeFalse();

        (await AdminRowCountAsync(org)).ShouldBe(1L, "exactly one row per call, not one per revoked session");

        var row = await OwnerRowAsync(
            """
            select a.actor_type
                 , a.actor_id
                 , a.action
                 , a.object_type
                 , a.object_id
                 , a.request_id
                 , a.trace_id
                 , coalesce(a.metadata, '{}'::jsonb)::text
              from public.audit_log a
             where a.org_id = @org
             order by a.seq desc
             limit 1
            """,
            ("org", org));

        row[0].ShouldBe("user");
        row[1].ShouldBe(admin.ToString("D"), "the actor is the admin whose proof made the call");
        row[2].ShouldBe(AdminRevokeAction);
        row[3].ShouldBe("user", "the object is the account whose sessions ended");
        row[4].ShouldBe(member.ToString("D"));
        row[5].ShouldBe(SampleRequestId);
        row[6].ShouldBe(SampleTraceId);

        var metadata = (string)row[7]!;
        using var document = System.Text.Json.JsonDocument.Parse(metadata);
        string.Join(",", document.RootElement.EnumerateObject().Select(property => property.Name))
            .ShouldBe(
                "revoked_count,target_user_id",
                "the canonical string sorts the keys by UTF-16 code unit, which the row's hash depends on");
        document.RootElement.GetProperty("revoked_count").GetString().ShouldBe("2", "metadata values are strings, never numbers");
        document.RootElement.GetProperty("target_user_id").GetString().ShouldBe(member.ToString("D"));
    }

    /// <summary>
    /// Two calls leave two linked rows — the second built on the first's entry hash — and the production verifier
    /// reads the chain as unbroken, so a row this definer writes is what the <c>audit.verify_chain</c> task
    /// accepts, not a second hash implementation's opinion.
    /// </summary>
    [Fact]
    public async Task The_Definer_Rows_Chain_And_The_Audit_Verifier_Accepts_Them()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var first = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var second = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        await CreateSessionAsync(first, clock);
        await CreateSessionAsync(second, clock);

        var routes = CreateRoutes(clock);

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), first, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.Done);
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), second, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.Done);

        (await AdminRowCountAsync(org)).ShouldBe(2L, "one row per call, never one per revoked session");

        // Every row after the first continues from the one before it: one chain, not two independent rows.
        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from (
                    select prev_hash
                         , lag(entry_hash) over (order by seq) as expected_prev
                      from public.audit_log
                     where org_id = @org
                   ) r
             where r.expected_prev is not null
               and r.prev_hash <> r.expected_prev
            """,
            ("org", org))).ShouldBe(0L, "each row is built on its predecessor's entry hash");

        (await VerifyAsync(org)).ShouldBeNull("the existing verifier accepts what the definer hashed");
    }

    /// <summary>
    /// Every refusal is <c>not_found</c> and writes nothing: a caller holding no <c>membership:write</c>, a caller
    /// naming an org it does not administer, an unknown user, a target that is not an ACTIVE member of the org, and
    /// the caller addressing itself (the self route is the only way to end your own sessions).
    /// </summary>
    [Fact]
    public async Task A_Caller_Without_The_Permission_Foreign_Org_Unknown_User_Or_Own_Id_Answers_NotFound()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var foreignOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var plain = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var removed = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, org, "viewer", status: "removed", cancellationToken: Token);
        var foreignAdmin = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, foreignOrg, "tenant_admin", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var plainTicket = await CreateSessionAsync(plain, clock);
        var foreignTicket = await CreateSessionAsync(foreignAdmin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);
        var removedTicket = await CreateSessionAsync(removed, clock);

        var routes = CreateRoutes(clock);

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(plainTicket), member, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "a membership is not the permission");
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(foreignTicket), member, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "the caller administers no membership in this org");
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), Guid.CreateVersion7(), org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "an id that never existed");
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), removed, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "a removed membership is not an administered member");
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), admin, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "the self route is the only way to end your own sessions");

        (await IsLiveAsync(memberTicket.SessionId)).ShouldBeTrue("a refused call revokes nothing");
        (await IsLiveAsync(removedTicket.SessionId)).ShouldBeTrue();
        (await IsLiveAsync(adminTicket.SessionId)).ShouldBeTrue();
        (await AdminRowCountAsync(org)).ShouldBe(0L, "no refusal writes a row");
        (await AdminRowCountAsync(foreignOrg)).ShouldBe(0L);
    }

    /// <summary>
    /// A proof that names no live session answers <c>unauthenticated</c> — an absent proof without a statement and
    /// a revoked session's proof alike — and nothing is revoked or recorded.
    /// </summary>
    [Fact]
    public async Task A_Dead_Proof_Answers_Unauthenticated_And_Revokes_Nothing()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeAllForUserAsAdminAsync([], member, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.Unauthenticated, "no cookie is no caller");

        await OwnerExecuteAsync(
            "update public.session set revoked_at = @now where id = @id",
            ("now", clock.GetUtcNow().ToUniversalTime()),
            ("id", adminTicket.SessionId));

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), member, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.Unauthenticated, "a dead row proves no caller either");

        (await IsLiveAsync(memberTicket.SessionId)).ShouldBeTrue();
        (await AdminRowCountAsync(org)).ShouldBe(0L);
    }

    /// <summary>
    /// The append and the revoke are one transaction: a throwaway trigger on <c>audit_log</c> that aborts the
    /// insert makes the whole call fail, and EVERY session of the target is still live afterwards — a revocation
    /// without its record cannot exist.
    /// </summary>
    [Fact]
    public async Task A_Fault_In_The_Audit_Append_Rolls_Every_Revoke_Back()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var first = await CreateSessionAsync(member, clock);
        var second = await CreateSessionAsync(member, clock);

        await InstallAuditFaultAsync();

        try
        {
            var refusal = await Should.ThrowAsync<PostgresException>(
                () => CreateRoutes(clock)
                    .RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), member, org, null, null, Token)
                    .AsTask());

            refusal.SqlState.ShouldBe(PostgresErrorCodes.RaiseException, "the append aborted the whole statement");
        }
        finally
        {
            await DropAuditFaultAsync();
        }

        (await IsLiveAsync(first.SessionId)).ShouldBeTrue("the revokes share the definer's transaction");
        (await IsLiveAsync(second.SessionId)).ShouldBeTrue();
        (await LiveCountAsync(member)).ShouldBe(2L);
        (await AdminRowCountAsync(org)).ShouldBe(0L);
    }

    /// <summary>
    /// The defined privilege: one function of that name, owner-owned with an empty <c>search_path</c> and the
    /// bounded wait, EXECUTE for <c>evidgate_app</c> alone — and the operator, the sweep role and PUBLIC are
    /// refused by the server when they try to run it.
    /// </summary>
    [Fact]
    public async Task Only_The_App_Role_May_Execute_The_Admin_Revoke_All_Definer()
    {
        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
            """)).ShouldBe(1L, "one definition, never a stale overload a grant could still reach");

        (await OwnerScalarAsync<string>(
            """
            select pg_catalog.pg_get_function_identity_arguments(p.oid)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
            """)).ShouldBe(ExpectedArguments);

        (await OwnerScalarAsync<string>(
            """
            select array_to_string(p.proargnames[1:p.pronargs], ',')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
            """)).ShouldBe(
            "p_cookie_hmacs,p_target_user_id,p_org_id,p_now,p_request_id,p_trace_id",
            "the target is an argument, the ACTOR never is: it is resolved from the cookie proof inside the definer");

        var posture = await OwnerRowAsync(
            """
            select p.prosecdef
                 , pg_catalog.pg_get_userbyid(p.proowner)
                 , coalesce(array_to_string(p.proconfig, ','), '')
                 , p.proretset
                 , pg_catalog.pg_get_function_result(p.oid)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
            """);

        posture[0].ShouldBe(true, "a definer, so the app role reaches the owner-only session table through it");
        posture[1].ShouldBe("evidgate_owner");
        ((string)posture[2]!).ShouldContain("search_path=\"\"");
        ((string)posture[2]!).ShouldContain("lock_timeout", customMessage: "the whole call is bounded");
        posture[3].ShouldBe(true, "it returns a set: status and the count");
        ((string)posture[4]!).ShouldContain("status");
        ((string)posture[4]!).ShouldContain("revoked_count");

        (await OwnerScalarAsync<string>(
            """
            select coalesce(string_agg(
                     case when a.grantee = 0 then 'PUBLIC'
                          else pg_catalog.pg_get_userbyid(a.grantee) end
                     || ':' || a.privilege_type, ',' order by a.grantee)
                   , '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
              cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
               and a.privilege_type = 'EXECUTE'
               and a.grantee <> p.proowner
            """)).ShouldBe("evidgate_app:EXECUTE", "the app role, and never PUBLIC or another runtime role");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            (await OwnerScalarAsync<bool>(
                """
                select exists (
                  select 1
                    from pg_catalog.pg_proc p
                    join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
                     and pg_catalog.has_function_privilege(@role, p.oid, 'execute'))
                """,
                ("role", role))).ShouldBeFalse($"{role} holds no EXECUTE on the admin revoke-all");

            // The grant is only real when the server refuses the call: switch identity and try it. The harness
            // holds no password for these roles, so the switch runs on the bootstrap connection.
            await using var connection = await OpenAsRoleAsync(role);

            var refusal = await Should.ThrowAsync<PostgresException>(
                () => AdminRevokeAllSqlAsync(connection, null, [], Guid.CreateVersion7(), Guid.CreateVersion7(),
                    ClockAt().GetUtcNow()));

            refusal.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{role} must not run the function");
        }
    }

    /// <summary>
    /// The lock order is the invariant: the call parks on the target's per-user lock FIRST, holding no lock on a
    /// session row and none on the org's chain, and only after that lock is free does it revoke and append.
    /// </summary>
    [Fact]
    public async Task An_Admin_Revoke_All_Waits_For_The_Users_Lock_Before_It_Touches_A_Session_Row()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        await CreateSessionAsync(member, clock);
        await CreateSessionAsync(member, clock);

        var proof = ProofOf(adminTicket);
        var now = clock.GetUtcNow();

        // The holder takes the same per-user key the writers take, and keeps it open.
        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, held, "select evidgate.session_user_lock(@user)", ("user", member));

        await using var waiter = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await waiter.OpenAsync(Token);

        var pending = Task.Run(
            () => AdminRevokeAllSqlAsync(waiter, null, proof, member, org, now),
            Token);

        await WaitForUserLockWaitersAsync(member, expected: 1);
        pending.IsCompleted.ShouldBeFalse("the call parks on the target's user lock");

        (await OwnerScalarAsync<long>(
            "select count(*) from pg_locks where pid = @pid and locktype = 'relation' and granted "
            + "and relation = 'public.session'::regclass and mode = 'RowExclusiveLock'",
            ("pid", waiter.ProcessID))).ShouldBe(0L, "no session row is touched before the user lock is held");

        (await OwnerScalarAsync<long>(
            "select count(*) from pg_locks where pid = @pid and locktype = 'advisory' and granted and objsubid = 1 "
            + "and objid::bigint = (hashtext('audit:' || @org)::bigint & 4294967295)",
            ("pid", waiter.ProcessID),
            ("org", org.ToString("D")))).ShouldBe(0L, "the org's chain lock comes after the user lock");

        await held.CommitAsync(Token);

        var (status, revoked) = await pending.WaitAsync(WaitLimit, Token);

        status.ShouldBe("revoked");
        revoked.ShouldBe(2);
        (await LiveCountAsync(member)).ShouldBe(0L);
        (await AdminRowCountAsync(org)).ShouldBe(1L);
    }

    /// <summary>
    /// Two writers of one user — the administrative call and the self revoke-all — queue on the same per-user lock
    /// and BOTH complete whatever order they were started in: no deadlock (<c>40P01</c>) in either direction, the
    /// two counts add up to the rows that existed, and the row is written only when the administrative call
    /// revoked something.
    /// </summary>
    /// <param name="first">The writer started first; the other starts once the first is queued.</param>
    [Theory]
    [InlineData("admin")]
    [InlineData("self")]
    public async Task Admin_And_Self_Revoke_All_Of_One_User_Complete_In_Either_Order_Without_A_Deadlock(string first)
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        await CreateSessionAsync(member, clock);
        await CreateSessionAsync(member, clock);

        var proof = ProofOf(adminTicket);
        var now = clock.GetUtcNow();

        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, held, "select evidgate.session_user_lock(@user)", ("user", member));

        await using var adminConnection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await adminConnection.OpenAsync(Token);
        await using var selfConnection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await selfConnection.OpenAsync(Token);

        Task<(string Status, int Revoked)> adminCall;
        Task<int> selfCall;

        if (string.Equals(first, "admin", StringComparison.Ordinal))
        {
            adminCall = Task.Run(() => AdminRevokeAllSqlAsync(adminConnection, null, proof, member, org, now), Token);
            await WaitForUserLockWaitersAsync(member, expected: 1);
            selfCall = Task.Run(() => SelfRevokeAllSqlAsync(selfConnection, member, now), Token);
        }
        else
        {
            selfCall = Task.Run(() => SelfRevokeAllSqlAsync(selfConnection, member, now), Token);
            await WaitForUserLockWaitersAsync(member, expected: 1);
            adminCall = Task.Run(() => AdminRevokeAllSqlAsync(adminConnection, null, proof, member, org, now), Token);
        }

        // Both are queued on ONE key before anything is let through: the serialisation is observed, not hoped for.
        await WaitForUserLockWaitersAsync(member, expected: 2);
        adminCall.IsCompleted.ShouldBeFalse();
        selfCall.IsCompleted.ShouldBeFalse();

        await held.RollbackAsync(Token);

        var adminResult = await adminCall.WaitAsync(WaitLimit, Token);
        var selfRevoked = await selfCall.WaitAsync(WaitLimit, Token);

        adminResult.Status.ShouldBe("revoked", "the administrative call completes after the self writer released the lock");
        (adminResult.Revoked + selfRevoked).ShouldBe(
            2,
            "the two writers saw the same rows in sequence: between them they revoked every live session once");
        (await LiveCountAsync(member)).ShouldBe(0L);
        (await AdminRowCountAsync(org)).ShouldBe(
            adminResult.Revoked > 0 ? 1L : 0L,
            "the row exists exactly when the administrative call itself revoked something");
        (await IsLiveAsync(adminTicket.SessionId)).ShouldBeTrue("neither writer targets the caller's own session");
    }

    /// <summary>A store over the app-role data source; the route statements never open the sweep connection.</summary>
    /// <param name="clock">The clock the statements' <c>p_now</c> comes from.</param>
    private SessionRoutesStore CreateRoutes(TimeProvider clock) => new(AppDataSource, new NoSweep(), clock);

    /// <summary>The proof a route passes: the HMAC of the live cookie value under the current pepper.</summary>
    /// <param name="ticket">The session the store minted.</param>
    private static byte[] ProofOf(SessionTicket ticket) => HmacOf(Pepper, ticket.CookieValue);

    /// <summary>The definer call itself, on a connection the caller owns (so its lock wait is observable).</summary>
    /// <param name="connection">The connection to run on.</param>
    /// <param name="transaction">The caller's transaction, or null.</param>
    /// <param name="proof">The HMAC of the caller's cookie value.</param>
    /// <param name="targetUserId">The account whose sessions are addressed.</param>
    /// <param name="orgId">The org the caller administers.</param>
    /// <param name="now">The instant passed as <c>p_now</c>.</param>
    private static async Task<(string Status, int Revoked)> AdminRevokeAllSqlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        byte[] proof,
        Guid targetUserId,
        Guid orgId,
        DateTimeOffset now)
    {
        await using var command = new NpgsqlCommand(
            "select status, revoked_count from evidgate_resolve.session_revoke_all_admin("
            + "cast(@hmacs as bytea[]), @target, @org, @now, @request_id, @trace_id)",
            connection,
            transaction);
        command.Parameters.AddWithValue("hmacs", new[] { proof });
        command.Parameters.AddWithValue("target", targetUserId);
        command.Parameters.AddWithValue("org", orgId);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("request_id", SampleRequestId);
        command.Parameters.AddWithValue("trace_id", SampleTraceId);

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue("the definer always answers one row");

        return (reader.GetString(0), reader.GetInt32(1));
    }

    /// <summary>The self revoke-all of one account, on a connection the caller owns.</summary>
    /// <param name="connection">The connection to run on.</param>
    /// <param name="userId">The account whose sessions are ended.</param>
    /// <param name="now">The instant passed as <c>p_now</c>.</param>
    private static async Task<int> SelfRevokeAllSqlAsync(NpgsqlConnection connection, Guid userId, DateTimeOffset now)
    {
        await using var command = new NpgsqlCommand(
            "select revoked_count from evidgate_resolve.session_revoke_all_for_user(@user, @now)",
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());

        return (int)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>Runs one statement on a caller-owned connection.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The connection's open transaction, or null.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="values">Its parameters.</param>
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>
    /// Polls <c>pg_locks</c> until <paramref name="expected"/> backends are parked on this user's advisory key —
    /// an explicit ordering signal, never a sleep that guesses.
    /// </summary>
    /// <param name="userId">The user whose per-user lock the writers contend for.</param>
    /// <param name="expected">How many queued backends to wait for.</param>
    private async Task WaitForUserLockWaitersAsync(Guid userId, int expected)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < WaitLimit)
        {
            var waiting = await OwnerScalarAsync<long>(
                "select count(*) from pg_locks "
                + "where locktype = 'advisory' and not granted and objsubid = 2 "
                + "and classid::bigint = (hashtext('evidgate.session_user')::bigint & 4294967295) "
                + "and objid::bigint = (hashtext(@user)::bigint & 4294967295)",
                ("user", userId.ToString("D")));

            if (waiting >= expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Token);
        }

        throw new TimeoutException($"no backend parked on the per-user session lock (expected {expected})");
    }

    /// <summary>Whether the session row is still live, read as the owner.</summary>
    /// <param name="sessionId">The row.</param>
    private Task<bool> IsLiveAsync(Guid sessionId) =>
        OwnerScalarAsync<bool>(
            "select revoked_at is null and expires_at > now() from public.session where id = @id",
            ("id", sessionId));

    /// <summary>How many live rows the account holds.</summary>
    /// <param name="userId">The account.</param>
    private Task<long> LiveCountAsync(Guid userId) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", userId));

    /// <summary>How many administrative-revoke rows the org's chain holds.</summary>
    /// <param name="orgId">The org.</param>
    private Task<long> AdminRowCountAsync(Guid orgId) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = @action",
            ("org", orgId),
            ("action", AdminRevokeAction));

    /// <summary>
    /// Opens a bootstrap connection switched to <paramref name="role"/>: the harness holds no password for the
    /// runtime roles, so assuming one is only possible from the superuser connection.
    /// </summary>
    /// <param name="role">The runtime role to assume — a fixed name in this suite, never caller input.</param>
    private async Task<NpgsqlConnection> OpenAsRoleAsync(string role)
    {
        var database = new NpgsqlConnectionStringBuilder(Fixture.AppConnectionString).Database!;
        var connection = await Fixture.OpenSuperuserAsync(database, Token);

        await using var switchRole = connection.CreateCommand();
        switchRole.CommandText = $"set role {role}";
        await switchRole.ExecuteNonQueryAsync(Token);

        return connection;
    }

    /// <summary>Recomputes the org's chain with the production verifier, inside that org's scope.</summary>
    /// <param name="orgId">The org whose chain is verified.</param>
    private async Task<AuditChainFinding?> VerifyAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(
            orgId,
            readOnlyTransaction: true,
            cancellationToken: Token);

        return await new AuditChainVerifier(scope.Db).VerifyAsync(orgId, Token);
    }

    /// <summary>
    /// Installs the throwaway fault: a trigger on <c>audit_log</c> that raises on every insert, so the definer's
    /// append aborts inside its own transaction. Created as the owner under a name no production object uses.
    /// </summary>
    private async Task InstallAuditFaultAsync()
    {
        await OwnerExecuteAsync("drop trigger if exists s302_admin_revoke_all_fault on public.audit_log");

        await OwnerExecuteAsync(
            """
            create or replace function public.s302_admin_revoke_all_fault() returns trigger
              language plpgsql
              set search_path = ''
              as $$
              begin
                raise exception 's302 admin revoke-all fault';
              end
              $$;
            """);

        await OwnerExecuteAsync(
            "create trigger s302_admin_revoke_all_fault before insert on public.audit_log "
            + "for each row execute function public.s302_admin_revoke_all_fault()");
    }

    /// <summary>Drops the throwaway trigger and its function, whatever the test's outcome was.</summary>
    private async Task DropAuditFaultAsync()
    {
        await OwnerExecuteAsync("drop trigger if exists s302_admin_revoke_all_fault on public.audit_log");
        await OwnerExecuteAsync("drop function if exists public.s302_admin_revoke_all_fault()");
    }

    /// <summary>
    /// The sweep connection the route statements never open: reaching it means the store called the wrong role for
    /// a route, which is a defect and not a fallback.
    /// </summary>
    private sealed class NoSweep : IWorkerSchedDataSource
    {
        public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a route statement must never open the sweep connection");
    }
}
