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
/// The arrangement every administrative revoke-all case shares: the store over the app-role data source, the
/// definer statements themselves on caller-owned connections, the owner-seam reads of what they changed, and the
/// lock observations the concurrency cases decide by.
/// </summary>
/// <remarks>
/// <para>
/// Every write under test runs through the production statement — the store, or the same function the store calls —
/// while rows and grants are read as the owner, because the app role holds no privilege on <c>public.session</c>.
/// The row's hash is never re-implemented: a row is only accepted when the existing <c>AuditChainVerifier</c>
/// reads the chain as unbroken.
/// </para>
/// <para>
/// Concurrency is never waited out with a sleep: the cases poll <c>pg_locks</c> for the key they contend for, so
/// the order they assert is observed rather than assumed.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public abstract class SessionRevokeAllAdminTestBase : SessionHardeningTestBase
{
    /// <summary>How long a concurrency case waits for an observable lock state or a finished call.</summary>
    protected static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    /// <summary>The action string the definer writes; it lives in SQL, so the literal is pinned here.</summary>
    protected const string AdminRevokeAction = "auth.session_revoke_admin";

    /// <summary>A request id the edge accepts: visible ASCII, 1..128 characters.</summary>
    protected const string SampleRequestId = "req-admin-revoke-all";

    /// <summary>A W3C trace id: 32 lowercase hex digits, not the all-zero value the standard refuses.</summary>
    protected const string SampleTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    /// <summary>
    /// A request id carrying the two characters RFC 8785 escaping must handle, and nothing else — `"` and `\` —
    /// so a chain it appears in proves the SQL canonicalisation and the C# one agree.
    /// </summary>
    protected const string QuotedRequestId = "req\"\\id";

    /// <summary>Own database: the rollback case installs a trigger on <c>public.audit_log</c>.</summary>
    /// <param name="fixture">The assembly fixture.</param>
    protected SessionRevokeAllAdminTestBase(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>A store over the app-role data source; the route statements never open the sweep connection.</summary>
    /// <param name="clock">The clock the statements' <c>p_now</c> comes from.</param>
    private protected SessionRoutesStore CreateRoutes(TimeProvider clock) => new(AppDataSource, new NoSweep(), clock);

    /// <summary>The proof a route passes: the HMAC of the live cookie value under the current pepper.</summary>
    /// <param name="ticket">The session the store minted.</param>
    protected static byte[] ProofOf(SessionTicket ticket) => HmacOf(Pepper, ticket.CookieValue);

    /// <summary>The definer call itself, on a connection the caller owns (so its lock wait is observable).</summary>
    /// <param name="connection">The connection to run on.</param>
    /// <param name="transaction">The caller's transaction, or null.</param>
    /// <param name="proof">The HMAC of the caller's cookie value.</param>
    /// <param name="targetUserId">The account whose sessions are addressed.</param>
    /// <param name="orgId">The org the caller administers.</param>
    /// <param name="now">The instant passed as <c>p_now</c>.</param>
    protected static async Task<(string Status, int Revoked)> AdminRevokeAllSqlAsync(
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

    /// <summary>
    /// The member-removal definer, called bare: it takes the same per-user lock and ends every live row without
    /// authorising, recording or reading a caller — it is not the self-service revoke-all route.
    /// </summary>
    /// <param name="connection">The connection to run on.</param>
    /// <param name="userId">The account whose sessions are ended.</param>
    /// <param name="now">The instant passed as <c>p_now</c>.</param>
    protected static async Task<int> BareRevokeAllForUserSqlAsync(
        NpgsqlConnection connection,
        Guid userId,
        DateTimeOffset now)
    {
        await using var command = new NpgsqlCommand(
            "select revoked_count from evidgate_resolve.session_revoke_all_for_user(@user, @now)",
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());

        var revoked = (await command.ExecuteScalarAsync(Token)).ShouldNotBeNull(
            "the member-removal definer always answers one row");

        return (int)revoked;
    }

    /// <summary>The single administrative revoke: one session row, one audit row, the org's chain lock.</summary>
    /// <param name="connection">The connection to run on.</param>
    /// <param name="proof">The HMAC of the caller's cookie value.</param>
    /// <param name="sessionId">The session row addressed.</param>
    /// <param name="orgId">The org the caller administers.</param>
    /// <param name="now">The instant passed as <c>p_now</c>.</param>
    protected static async Task<string> SingleAdminRevokeSqlAsync(
        NpgsqlConnection connection,
        byte[] proof,
        Guid sessionId,
        Guid orgId,
        DateTimeOffset now)
    {
        await using var command = new NpgsqlCommand(
            "select status from evidgate_resolve.session_revoke_one("
            + "cast(@hmacs as bytea[]), @session, @org, @now, @request_id, @trace_id)",
            connection);
        command.Parameters.AddWithValue("hmacs", new[] { proof });
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("org", orgId);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("request_id", SampleRequestId);
        command.Parameters.AddWithValue("trace_id", SampleTraceId);

        var status = (await command.ExecuteScalarAsync(Token)).ShouldNotBeNull(
            "the single-revoke definer always answers one row");

        return (string)status;
    }

    /// <summary>Runs one statement on a caller-owned connection.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The connection's open transaction, or null.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="values">Its parameters.</param>
    protected static async Task ExecuteAsync(
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
    protected async Task WaitForUserLockWaitersAsync(Guid userId, int expected)
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

    /// <summary>Polls <c>pg_locks</c> until <paramref name="pid"/> is queued on the org's chain lock.</summary>
    /// <param name="pid">The backend whose wait is observed.</param>
    /// <param name="orgId">The org whose chain key the wait is on.</param>
    protected async Task WaitForChainLockWaitAsync(int pid, Guid orgId)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < WaitLimit)
        {
            var waiting = await OwnerScalarAsync<long>(
                "select count(*) from pg_locks where pid = @pid and locktype = 'advisory' and not granted "
                + "and objsubid = 1 "
                + "and objid::bigint = (hashtext('audit:' || @org)::bigint & 4294967295)",
                ("pid", pid),
                ("org", orgId.ToString("D")));

            if (waiting >= 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Token);
        }

        throw new TimeoutException($"the backend {pid} never parked on the org chain lock");
    }

    /// <summary>
    /// Polls <c>pg_locks</c> until <paramref name="pid"/> is blocked on ANY lock it cannot have yet — the signal a
    /// second writer has reached the row or the chain the first one holds.
    /// </summary>
    /// <param name="pid">The backend whose wait is observed.</param>
    protected async Task WaitForUngrantedLockAsync(int pid)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < WaitLimit)
        {
            var waiting = await OwnerScalarAsync<long>(
                "select count(*) from pg_locks where pid = @pid and not granted",
                ("pid", pid));

            if (waiting >= 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Token);
        }

        throw new TimeoutException($"the backend {pid} never queued on a lock");
    }

    /// <summary>Whether the session row is still live, read as the owner.</summary>
    /// <param name="sessionId">The row.</param>
    protected Task<bool> IsLiveAsync(Guid sessionId) =>
        OwnerScalarAsync<bool>(
            "select revoked_at is null and expires_at > now() from public.session where id = @id",
            ("id", sessionId));

    /// <summary>How many live rows the account holds.</summary>
    /// <param name="userId">The account.</param>
    protected Task<long> LiveCountAsync(Guid userId) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", userId));

    /// <summary>How many administrative-revoke rows the org's chain holds.</summary>
    /// <param name="orgId">The org.</param>
    protected Task<long> AdminRowCountAsync(Guid orgId) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = @action",
            ("org", orgId),
            ("action", AdminRevokeAction));

    /// <summary>The newest row of the org's chain with its stored correlation ids and metadata.</summary>
    /// <param name="orgId">The org whose chain is read.</param>
    protected Task<object?[]> NewestAdminRowAsync(Guid orgId) =>
        OwnerRowAsync(
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
            ("org", orgId));

    /// <summary>
    /// Opens a bootstrap connection switched to <paramref name="role"/>: the harness holds no password for the
    /// runtime roles, so assuming one is only possible from the superuser connection.
    /// </summary>
    /// <param name="role">The runtime role to assume — a fixed name in this suite, never caller input.</param>
    protected async Task<NpgsqlConnection> OpenAsRoleAsync(string role)
    {
        var database = new NpgsqlConnectionStringBuilder(Fixture.AppConnectionString).Database
            ?? throw new InvalidOperationException("the harness app connection string names no database");
        var connection = await Fixture.OpenSuperuserAsync(database, Token);

        await using var switchRole = connection.CreateCommand();
        switchRole.CommandText = $"set role {role}";
        await switchRole.ExecuteNonQueryAsync(Token);

        return connection;
    }

    /// <summary>Recomputes the org's chain with the production verifier, inside that org's scope.</summary>
    /// <param name="orgId">The org whose chain is verified.</param>
    protected async Task<AuditChainFinding?> VerifyAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(
            orgId,
            readOnlyTransaction: true,
            cancellationToken: Token);

        return await new AuditChainVerifier(scope.Db).VerifyAsync(orgId, Token);
    }

    /// <summary>
    /// One custom (org-owned) role of <paramref name="org"/> carrying a single tenant-assignable permission code,
    /// written through the owner seam the way the custom-role commands would write it.
    /// </summary>
    /// <param name="org">The org the role belongs to — the scope the definer has to honour.</param>
    /// <param name="code">The role code (lowercase, as <c>role_code_format_check</c> requires).</param>
    /// <param name="permission">The permission code the role grants.</param>
    protected async Task<Guid> SeedCustomRoleAsync(Guid org, string code, string permission = "membership:write")
    {
        var role = Guid.CreateVersion7();
        await OwnerExecuteAsync(
            """
            insert into public.role (id, org_id, code, name, is_builtin, kind)
            values (@id, @org_id, @code, @name, false, 'member')
            """,
            ("id", role),
            ("org_id", org),
            ("code", code),
            ("name", code));

        await OwnerExecuteAsync(
            """
            insert into public.role_permission (id, org_id, role_id, permission_code)
            values (@id, @org_id, @role_id, @code)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("role_id", role),
            ("code", permission));

        return role;
    }

    /// <summary>
    /// A membership in <paramref name="org"/> whose single role is named by id — the arrangement the commands
    /// do not expose directly. The role key refuses a missing, non-member or other-org role, so the id must be
    /// one <paramref name="org"/> may hold.
    /// </summary>
    /// <param name="org">The org the membership belongs to.</param>
    /// <param name="roleId">The role the membership holds.</param>
    /// <param name="status">The membership status it is left in.</param>
    /// <param name="email">The account's address.</param>
    protected async Task<Guid> SeedMemberWithRoleAsync(Guid org, Guid roleId, string status, string email)
    {
        var user = await SeedUserAsync(email);
        var membership = Guid.CreateVersion7();

        await OwnerExecuteAsync(
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org_id, @user_id, @email, @status, now(), now(), 1)
            """,
            ("id", membership),
            ("org_id", org),
            ("user_id", user),
            ("email", email),
            ("status", status));

        await OwnerExecuteAsync(
            """
            insert into public.membership_role
                (id, org_id, membership_id, role_id, granted_at, created_at, updated_at, version)
            values (@id, @org_id, @membership_id, @role_id, now(), now(), now(), 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("membership_id", membership),
            ("role_id", roleId));

        return user;
    }

    /// <summary>
    /// Installs the throwaway fault: a trigger on <c>audit_log</c> that raises on every insert, so the definer's
    /// append aborts inside its own transaction. Created as the owner under a name no production object uses.
    /// </summary>
    protected async Task InstallAuditFaultAsync()
    {
        await OwnerExecuteAsync("drop trigger if exists admin_revoke_all_fault on public.audit_log");

        await OwnerExecuteAsync(
            """
            create or replace function public.admin_revoke_all_fault() returns trigger
              language plpgsql
              set search_path = ''
              as $$
              begin
                raise exception 'admin revoke-all fault';
              end
              $$;
            """);

        await OwnerExecuteAsync(
            "create trigger admin_revoke_all_fault before insert on public.audit_log "
            + "for each row execute function public.admin_revoke_all_fault()");
    }

    /// <summary>Drops the throwaway trigger and its function, whatever the test's outcome was.</summary>
    protected async Task DropAuditFaultAsync()
    {
        await OwnerExecuteAsync("drop trigger if exists admin_revoke_all_fault on public.audit_log");
        await OwnerExecuteAsync("drop function if exists public.admin_revoke_all_fault()");
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
