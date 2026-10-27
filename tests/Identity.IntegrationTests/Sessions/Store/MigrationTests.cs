using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// 02.3.2-T1.1's schema half: migration 0045 over the rows 0041/0042 already held (nothing to backfill), the
/// index swap that keeps the sliding update heap-only, and the grant hygiene every <c>evidgate_resolve</c>
/// definer follows (owner-owned, empty <c>search_path</c>, EXECUTE for the app role only).
/// </summary>
/// <remarks>
/// The harness migrates an empty database, so "applied over existing rows" is proven by inserting a row with
/// the 0042 column list — the shape every pre-0045 row has — and authenticating with it. The catalog
/// assertions pin what the migration decided, because a regression there is invisible in the store tests.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class MigrationTests : SessionHardeningTestBase
{
    /// <summary>The resolvers 0045 created or redefined, with the one that must be gone.</summary>
    private static readonly string[] SessionWriters =
    [
        "session_authenticate",
        "session_rotate",
        "session_revoke_all_for_user",
        "session_require_rotation",
        "session_create",
        "login_link_identity",
    ];

    /// <summary>0048's four route definers, which the app role and only the app role may call.</summary>
    private static readonly string[] SessionRouteWriters =
    [
        "session_list",
        "session_revoke_current",
        "session_revoke_one",
        "session_revoke_others",
    ];

    /// <summary>The spellings the catalogue may keep for the writer's 2000 ms bounded wait.</summary>
    private static readonly string[] BoundedWaitSettings = ["2000", "2000ms", "2s"];

    public MigrationTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The new column is nullable, has no default and no backfill: a row written with the 0042 column list is
    /// live and authenticates exactly as before, with nothing pending.
    /// </summary>
    [Fact]
    public async Task A_Pre_0045_Session_Row_Survives_And_Authenticates()
    {
        var definition = await OwnerScalarAsync<string>(
            """
            select is_nullable || '|' || coalesce(column_default, '~') || '|' || data_type
              from information_schema.columns
             where table_schema = 'public' and table_name = 'session' and column_name = 'rotation_required_at'
            """);

        definition.ShouldBe("YES|~|timestamp with time zone", "nullable, defaultless and timezone-aware");

        var userId = await SeedUserAsync("legacy-row@example.com");
        var clock = ClockAt();
        var sessionId = Guid.CreateVersion7();

        // A value of the shape 0042's mint produced — 43 base64url characters — because that is what a live row
        // written before 0045 holds: the store's SEC3 shape guard refuses anything else before it hashes, so an
        // arbitrary string here would prove the guard instead of the migration.
        var cookieValue = UnknownCookieValue();

        await OwnerExecuteAsync(
            """
            insert into public.session
                (id, user_id, cookie_hmac, pepper_version, created_at, last_seen_at, expires_at,
                 absolute_expires_at, user_agent)
            values (@id, @user_id, @hmac, @version, @created, @created, @expires, @absolute, 'legacy-agent')
            """,
            ("id", sessionId),
            ("user_id", userId),
            ("hmac", HmacOf(Pepper, cookieValue)),
            ("version", CurrentVersion),
            ("created", clock.UtcNow),
            ("expires", clock.UtcNow.AddHours(8)),
            ("absolute", clock.UtcNow.AddDays(7)));

        var result = await AuthenticateAsync(cookieValue, clock);

        result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "a row without rotation_required_at is still live");
        result.Value.Status.ShouldBe(SessionAuthStatus.Active);
        result.Value.SessionId.ShouldBe(sessionId);
        result.Value.UserId.ShouldBe(userId);
        result.Value.RotationRequired.ShouldBeFalse("nothing was pending before the column existed");
    }

    /// <summary>
    /// The index swap of plan §4 (recorded deviation): the full unique cookie key stays the hot path, the
    /// per-user index becomes partial on <c>revoked_at is null</c> and drops the sliding <c>expires_at</c>, and
    /// the table keeps room for the heap-only slide.
    /// </summary>
    [Fact]
    public async Task The_Session_Indexes_Serve_The_Request_Path_And_The_Slide_Stays_Heap_Only()
    {
        var indexes = await OwnerScalarAsync<string>(
            """
            select string_agg(indexname || '|' || indexdef, E'\n' order by indexname)
              from pg_catalog.pg_indexes
             where schemaname = 'public' and tablename = 'session'
            """);

        indexes.ShouldContain("session_cookie_hmac_key", Case.Sensitive);
        indexes.ShouldNotContain("session_user_revoked_expires_idx", Case.Sensitive,
            "the index carrying expires_at is what made every slide a non-HOT update");
        indexes.ShouldContain("session_user_live_idx", Case.Sensitive);

        (await OwnerScalarAsync<string>(
            "select indexdef from pg_catalog.pg_indexes where schemaname = 'public' "
            + "and tablename = 'session' and indexname = 'session_user_live_idx'"))
            .ShouldContain("WHERE (revoked_at IS NULL)", Case.Sensitive);

        (await OwnerScalarAsync<bool>(
            """
            select indisunique
              from pg_catalog.pg_index i
              join pg_catalog.pg_class c on c.oid = i.indexrelid
             where i.indrelid = 'public.session'::regclass and c.relname = 'session_cookie_hmac_key'
            """)).ShouldBeTrue("the cookie lookup relies on the key being unique");

        (await OwnerScalarAsync<string>(
            "select coalesce(array_to_string(reloptions, ','), '') from pg_catalog.pg_class "
            + "where oid = 'public.session'::regclass"))
            .ShouldContain("fillfactor=80", Case.Sensitive);
    }

    /// <summary>
    /// Every session writer is an owner definer with an empty <c>search_path</c> and EXECUTE for
    /// <c>evidgate_app</c> alone — never PUBLIC, never the operator or sweep role (TL T80).
    /// </summary>
    [Fact]
    public async Task Every_Session_Writer_Is_A_Hardened_Definer_Granted_To_The_Api_Role_Only()
    {
        foreach (var name in SessionWriters)
        {
            var definition = await OwnerScalarAsync<string>(
                """
                select p.prosecdef::text || '|' || pg_catalog.pg_get_userbyid(p.proowner) || '|'
                       || coalesce(array_to_string(p.proconfig, ','), '')
                  from pg_catalog.pg_proc p
                  join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                 where n.nspname = 'evidgate_resolve' and p.proname = @name
                """,
                ("name", name));

            definition.ShouldBe("true|evidgate_owner|search_path=\"\"", $"{name} keeps the definer hygiene");

            (await OwnerScalarAsync<bool>(
                """
                select pg_catalog.has_function_privilege('evidgate_app', p.oid, 'execute')
                  from pg_catalog.pg_proc p
                  join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                 where n.nspname = 'evidgate_resolve' and p.proname = @name
                """,
                ("name", name))).ShouldBeTrue($"evidgate_app calls {name}");

            foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
            {
                (await OwnerScalarAsync<bool>(
                    """
                    select pg_catalog.has_function_privilege(@role, p.oid, 'execute')
                      from pg_catalog.pg_proc p
                      join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                     where n.nspname = 'evidgate_resolve' and p.proname = @name
                    """,
                    ("name", name),
                    ("role", role))).ShouldBeFalse($"{role} must not run {name}");
            }
        }

        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from pg_catalog.pg_proc p
              cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = any(@names) and a.grantee = 0
               and a.privilege_type = 'EXECUTE'
            """,
            ("names", SessionWriters))).ShouldBe(0, "PUBLIC holds no EXECUTE on a session writer");
    }

    /// <summary>
    /// 0044's read-only <c>session_user</c> lookup is dropped with the second authentication path it served;
    /// the app role also holds no privilege on the table itself, so the definers are the only way in.
    /// </summary>
    [Fact]
    public async Task The_Old_Session_Lookup_Is_Gone_And_The_Table_Stays_Owner_Only()
    {
        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_user'
            """)).ShouldBe(0, "the clean cutover drops the superseded lookup");

        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
        {
            (await OwnerScalarAsync<bool>(
                $"select pg_catalog.has_table_privilege('evidgate_app', 'public.session', '{privilege}')"))
                .ShouldBeFalse($"evidgate_app holds no {privilege} on public.session");
        }

        (await OwnerScalarAsync<bool>(
            """
            select relrowsecurity and relforcerowsecurity
              from pg_catalog.pg_class
             where oid = 'public.session'::regclass
            """)).ShouldBeTrue("0041's owner-only policy is still forced, which is what lets the definers work");
    }

    /// <summary>
    /// 02.3.2-T2.1's five definers keep 0045's hygiene, and EXECUTE is scoped per function: the app role for
    /// the four routes, <c>evidgate_worker_sched</c> alone for the sweep (TL T80). None of them takes a
    /// caller-supplied user id — the acting user comes from the presented cookie hash inside the definer —
    /// and none returns a credential column (plan §8: the list is metadata only).
    /// </summary>
    [Fact]
    public async Task The_Session_Route_Definers_Are_Hardened_And_Granted_To_The_Role_That_Needs_Them()
    {
        foreach (var name in SessionRouteWriters)
        {
            if (!string.Equals(name, "session_revoke_one", StringComparison.Ordinal))
            {
                (await DefinerSignatureAsync("evidgate_resolve", name))
                    .ShouldBe("true|evidgate_owner|search_path=\"\"", $"{name} keeps the definer hygiene");
            }

            (await HasExecuteAsync("evidgate_app", "evidgate_resolve", name))
                .ShouldBeTrue($"evidgate_app calls {name}");

            foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
            {
                (await HasExecuteAsync(role, "evidgate_resolve", name))
                    .ShouldBeFalse($"{role} must not run {name}");
            }
        }

        // 0050 bounds the revoke's waits for the WHOLE call: `set lock_timeout = '2000'` in the header beside
        // the pinned `search_path`, the same 2000 ms as AuditTrail.cs AuditSql.LockTimeout, so the session
        // row lock and the org chain's advisory lock share one bound that PostgreSQL restores on return (S120
        // review S1). The other three routes take no lock of their own and keep the plain pinned path.
        await AssertRevokeDefinerAsync("the revoke keeps 0048's posture and adds the writer's bounded wait");

        // The sweep is the one function the app role may not run: an owner-defined bulk delete belongs to the
        // task mechanism's role, exactly like `evidgate.expire_pending_invites` (0043:208-211). It lives in
        // schema `evidgate`, not `evidgate_resolve`, because schema USAGE is the precondition of EXECUTE:
        // 0002:24 grants `evidgate` to the sweep role, 0002:57 grants `evidgate_resolve` to the app role alone.
        (await DefinerSignatureAsync("evidgate", "session_cleanup"))
            .ShouldBe("true|evidgate_owner|search_path=\"\"", "the sweep keeps the definer hygiene");

        (await HasExecuteAsync("evidgate_worker_sched", "evidgate", "session_cleanup"))
            .ShouldBeTrue("the session-cleanup task runs as the sweep role");

        foreach (var role in new[] { "evidgate_app", "evidgate_operator" })
        {
            (await HasExecuteAsync(role, "evidgate", "session_cleanup"))
                .ShouldBeFalse($"{role} must not run a bulk delete (TL T80)");
        }

        (await OwnerScalarAsync<long>(
            $"""
            select count(*)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where ({SessionDefiners})
            """)).ShouldBe(5, "0048 defines exactly these five functions");

        (await OwnerScalarAsync<long>(
            $"""
            select count(*)
              from pg_catalog.pg_proc p
              cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where ({SessionDefiners})
               and a.grantee = 0
               and a.privilege_type = 'EXECUTE'
            """)).ShouldBe(0, "PUBLIC holds no EXECUTE on a session definer");

        var results = await OwnerScalarAsync<string>(
            $"""
            select string_agg(pg_catalog.pg_get_function_result(p.oid), E'\n' order by p.proname)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where ({SessionDefiners})
            """);

        results.ShouldNotContain("cookie", Case.Sensitive, "no function may return the cookie value or its hash");
        results.ShouldNotContain("pepper", Case.Sensitive, "the pepper version is not part of the wire contract either");

        (await OwnerScalarAsync<string>(
            $"""
            select string_agg(coalesce(array_to_string(p.proargnames, ','), ''), E'\n')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where ({SessionDefiners})
            """)).ShouldNotContain("user_id", Case.Sensitive, "the acting user is never an argument (D-S85-D8)");
    }

    /// <summary>
    /// The five definers 0048 adds, as one SQL predicate over <c>pg_proc p</c> / <c>pg_namespace n</c>. It has a
    /// top-level <c>or</c>, so every place that ANDs another condition onto it must wrap it in parentheses —
    /// `where {SessionDefiners} and a.grantee = 0` would bind the <c>and</c> to the second branch alone and count
    /// every ACL item of the four route functions (8 instead of 0).
    /// </summary>
    private const string SessionDefiners = """
        (n.nspname = 'evidgate_resolve'
         and p.proname in ('session_list', 'session_revoke_current', 'session_revoke_one', 'session_revoke_others'))
        or (n.nspname = 'evidgate' and p.proname = 'session_cleanup')
        """;

    /// <summary>
    /// F55 (0050): the administrative revoke's audit row is appended inside the definer, and the
    /// SIGNATURE is unchanged — same arguments, same `table (status text)` — so 0048's owner and grants
    /// still apply. The file is additive and idempotent (`create or replace` plus re-stated grants), so
    /// applying it again is a no-op, and what the append depends on is still in force: `audit_log` keeps
    /// FORCE row level security (the owner INSERT policy of 0015 is what lets the definer append),
    /// PUBLIC holds no EXECUTE, and the body carries the audit statement itself.
    /// </summary>
    [Fact]
    public async Task The_Revoke_Audit_Definer_Keeps_Its_Grant_And_Reapplies_As_A_NoOp()
    {
        await AssertRevokeDefinerAsync("0050 re-creates the same function, not a new one");

        (await HasExecuteAsync("evidgate_app", "evidgate_resolve", "session_revoke_one"))
            .ShouldBeTrue("the app role calls the route");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            (await HasExecuteAsync(role, "evidgate_resolve", "session_revoke_one"))
                .ShouldBeFalse($"{role} must not revoke a session");
        }

        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from pg_catalog.pg_proc p
              cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
               and a.grantee = 0
               and a.privilege_type = 'EXECUTE'
            """)).ShouldBe(0, "PUBLIC holds no EXECUTE on the revoke");

        (await OwnerScalarAsync<string>(
            """
            select pg_catalog.pg_get_function_result(p.oid)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
            """)).ShouldBe("TABLE(status text)", "the route's wire contract did not change");

        (await OwnerScalarAsync<bool>(
            """
            select relrowsecurity and relforcerowsecurity
              from pg_catalog.pg_class
             where oid = 'public.audit_log'::regclass
            """)).ShouldBeTrue("the definer appends under FORCE row level security");

        var body = await OwnerScalarAsync<string>(RevokeBodySql);

        body.ShouldContain("insert into public.audit_log", Case.Sensitive);
        body.ShouldContain("'auth.session_revoke_admin'", Case.Sensitive);
        body.ShouldContain("pg_advisory_xact_lock(hashtext('audit:' || p_org_id::text))", Case.Sensitive);

        var migration = await File.ReadAllTextAsync(Path.Combine(DatabaseFixture.MigrationsPath, MigrationFile), Token);

        await Fixture.SeedAsync(migration, [], Token);
        await Fixture.SeedAsync(migration, [], Token);

        await AssertRevokeDefinerAsync("a re-apply keeps the definer hygiene and its bounded wait");
        (await HasExecuteAsync("evidgate_app", "evidgate_resolve", "session_revoke_one"))
            .ShouldBeTrue("the re-stated grant still names the app role");
    }

    /// <summary>
    /// QA-F3: the upgrade PATH, not only the file. A database that sat at 0049 already holds the org's
    /// application-written chain, so 0050 has to be a pure definition change over it: 0048's body (what a 0049
    /// database runs, re-stated from the shipped file because the harness migrates straight to head) is
    /// replaced, no privilege moves, every existing row still verifies, and the next admin revoke continues
    /// the SAME chain at <c>tip + 1</c>.
    /// </summary>
    [Fact]
    public async Task The_Upgrade_Over_A_Populated_Chain_Keeps_It_Verifying_And_Appends_At_The_Tip()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        // 1. The chain the APPLICATION wrote before the upgrade, through the real writer.
        var writer = CreateWriter();
        await writer.WriteAsync(new IdentityAuditEvent(IdentityAuditActions.Login, member, "google"), [org], Token);
        await writer.WriteAsync(new IdentityAuditEvent(IdentityAuditActions.Link, member, "github"), [org], Token);

        (await VerifyAsync(org)).ShouldBeNull("the application chain is whole before the upgrade");

        var before = await ChainAsync(org);
        before.Select(row => row.Seq).ShouldBe([1L, 2L], "two application rows are the pre-upgrade chain");

        // 2. 0049's state: the revoke definer still carries 0048's body, which appends nothing.
        await Fixture.SeedAsync(PredecessorRevokeBody(), [], Token);

        (await OwnerScalarAsync<string>(RevokeBodySql))
            .ShouldNotContain("insert into public.audit_log", Case.Sensitive,
                "the predecessor body re-stated above is what a database migrated to 0049 runs");

        var grantsAt0049 = await RevokeGrantsAsync();

        // 3. The upgrade itself, over the populated chain.
        await Fixture.SeedAsync(
            await File.ReadAllTextAsync(Path.Combine(DatabaseFixture.MigrationsPath, MigrationFile), Token),
            [],
            Token);

        (await RevokeGrantsAsync()).ShouldBe(grantsAt0049,
            "0050 re-states 0048's grants, so the upgrade moves no privilege");
        await AssertRevokeDefinerAsync("the upgraded definer carries 0050's posture");

        (await ChainAsync(org)).ShouldBe(before, "the upgrade touches no row");
        (await VerifyAsync(org)).ShouldBeNull("the chain written before the upgrade still verifies after it");

        // 4. The first admin revoke on the upgraded definer extends the SAME chain.
        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, Token))
            .ShouldBe(SessionRouteStatus.Done);

        var after = await ChainAsync(org);

        after.Select(row => row.Seq).ShouldBe([1L, 2L, 3L], "seq = tip + 1: the upgrade did not restart the chain");
        after[2].Action.ShouldBe("auth.session_revoke_admin", "the appended row is the administrative act");
        after[2].PrevHash.ShouldBe(after[1].EntryHash, "the new row links onto the tip the application wrote");
        (await VerifyAsync(org)).ShouldBeNull("the definer's row verifies with the production verifier");
    }

    /// <summary>
    /// 0050's posture of the revoke definer, in one place: still a <c>SECURITY DEFINER</c> owned by
    /// <c>evidgate_owner</c>, still pinned to the empty <c>search_path</c> (TL T80), plus exactly one more
    /// function setting — the writer's bounded wait (<c>AuditTrail.cs</c> <c>AuditSql.LockTimeout</c>, 2000 ms).
    /// The values come from <c>pg_proc.proconfig</c> and are compared quote-stripped, because PostgreSQL may
    /// spell a numeric value as <c>"2000"</c>.
    /// </summary>
    /// <param name="because">The failure message, phrased for the case that asserts the posture.</param>
    private async Task AssertRevokeDefinerAsync(string because)
    {
        (await DefinerSignatureAsync("evidgate_resolve", "session_revoke_one"))
            .ShouldStartWith("true|evidgate_owner|", Case.Sensitive, because);

        var settings = await RevokeSettingsAsync();

        settings.Select(setting => setting.Name).ShouldBe(["search_path", "lock_timeout"],
            $"{because}: the revoke carries the pinned search path and the bounded wait, and nothing else");
        settings[0].Value.ShouldBeEmpty($"{because}: `search_path` is pinned to the empty string");
        BoundedWaitSettings.ShouldContain(settings[1].Value,
            $"{because}: the writer's 2000 ms bound (AuditTrail.cs AuditSql.LockTimeout), whatever unit the "
            + "catalogue kept, so a busy org answers 55P03");
    }

    /// <summary>The revoke definer's configured settings, as name/value pairs read from <c>pg_proc.proconfig</c>.</summary>
    private async Task<IReadOnlyList<(string Name, string Value)>> RevokeSettingsAsync()
    {
        var configuration = await OwnerScalarAsync<string>(
            """
            select array_to_string(p.proconfig, ',')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
            """);

        return configuration
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split('=', 2))
            .Select(parts => (parts[0], parts.Length > 1 ? parts[1].Trim('"', '\'') : string.Empty))
            .ToArray();
    }

    /// <summary>0050, the file this case re-applies; it is the additive half of F55.</summary>
    private const string MigrationFile = "0050_identity_session_revoke_audit.sql";

    /// <summary>The migration 0050 supersedes for this one function: the body a database at 0049 runs.</summary>
    private const string PredecessorMigrationFile = "0048_identity_session_routes.sql";

    /// <summary>The revoke definer's stored source, as the upgrade cases read it.</summary>
    private const string RevokeBodySql = """
        select p.prosrc
          from pg_catalog.pg_proc p
          join pg_catalog.pg_namespace n on n.oid = p.pronamespace
         where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
        """;

    /// <summary>
    /// The text of 0048's <c>session_revoke_one</c>, extracted from the shipped migration file so the "before"
    /// side of the upgrade is the real definition and not a hand-kept copy. The harness migrates straight to
    /// head, so re-stating this body is how a case reaches the state a 0049 database is in; 0050 then replaces
    /// it exactly as the upgrade does.
    /// </summary>
    private static string PredecessorRevokeBody()
    {
        const string begin = "create or replace function evidgate_resolve.session_revoke_one(";
        var sql = File.ReadAllText(Path.Combine(DatabaseFixture.MigrationsPath, PredecessorMigrationFile));

        var start = sql.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"{PredecessorMigrationFile} no longer creates `{begin}`");
        }

        var bodyStart = sql.IndexOf("as $$", start, StringComparison.Ordinal) + "as $$".Length;
        var close = sql.IndexOf("$$;", bodyStart, StringComparison.Ordinal);
        if (bodyStart < "as $$".Length || close < 0)
        {
            throw new InvalidOperationException($"{PredecessorMigrationFile}: `{begin}` has no `$$;` terminator");
        }

        return sql[start..(close + "$$;".Length)];
    }

    /// <summary>
    /// Every EXECUTE grantee of the revoke definer as <c>role:privilege</c>, owner and PUBLIC included, so an
    /// upgrade that moves a privilege is caught by comparing the text before and after.
    /// </summary>
    private Task<string> RevokeGrantsAsync() =>
        OwnerScalarAsync<string>(
            """
            select coalesce(string_agg(
                     case when a.grantee = 0 then 'PUBLIC'
                          else pg_catalog.pg_get_userbyid(a.grantee) end
                     || ':' || a.privilege_type, ',' order by a.grantee, a.privilege_type), '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
              cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
            """);

    /// <summary>One org's audit chain, oldest first: <c>seq</c>, the two hashes as hex, and the action.</summary>
    /// <param name="orgId">The org whose chain is read.</param>
    private async Task<IReadOnlyList<(long Seq, string PrevHash, string EntryHash, string Action)>> ChainAsync(Guid orgId)
    {
        var rows = await OwnerScalarAsync<string>(
            """
            select coalesce(string_agg(
                     a.seq || '|' || encode(a.prev_hash, 'hex') || '|' || encode(a.entry_hash, 'hex')
                     || '|' || a.action, E'\n' order by a.seq), '')
              from public.audit_log a
             where a.org_id = @org_id
            """,
            ("org_id", orgId));

        if (rows.Length == 0)
        {
            return Array.Empty<(long Seq, string PrevHash, string EntryHash, string Action)>();
        }

        return rows.Split('\n')
            .Select(line =>
            {
                var parts = line.Split('|');
                return (long.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], parts[2], parts[3]);
            })
            .ToArray();
    }

    /// <summary>Recomputes the org's chain with the production verifier, inside that org's scope.</summary>
    /// <param name="orgId">The org whose chain is verified.</param>
    private async Task<AuditChainFinding?> VerifyAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        return await new AuditChainVerifier(scope.Db).VerifyAsync(orgId, Token);
    }

    /// <summary>The production identity writer over the harness's real scope factory and a fresh audit buffer.</summary>
    private IdentityAuditWriter CreateWriter() =>
        new(Fixture.Scopes, Fixture.Db, new AuditTrail());

    /// <summary>A store over the app-role data source; the route statements never open the sweep connection.</summary>
    /// <param name="clock">The clock the statements' <c>p_now</c> comes from.</param>
    private SessionRoutesStore CreateRoutes(TimeProvider clock) =>
        new(AppDataSource, new UnusedSweep(), clock);

    /// <summary>The proof a route passes: the HMAC of the live cookie value under the current pepper.</summary>
    /// <param name="ticket">The session the store minted.</param>
    private static byte[] ProofOf(SessionTicket ticket) => HmacOf(Pepper, ticket.CookieValue);

    /// <summary>
    /// The sweep connection the four route statements never open: reaching it means the store called the wrong
    /// role for a route, which is a defect and not a fallback.
    /// </summary>
    private sealed class UnusedSweep : IWorkerSchedDataSource
    {
        public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a route statement must never open the sweep connection");
    }

    /// <summary>One function's security posture: security-definer flag, owner, configured search path.</summary>
    /// <param name="schema">The schema the function lives in.</param>
    /// <param name="name">The function name.</param>
    private Task<string> DefinerSignatureAsync(string schema, string name) =>
        OwnerScalarAsync<string>(
            """
            select p.prosecdef::text || '|' || pg_catalog.pg_get_userbyid(p.proowner) || '|'
                   || coalesce(array_to_string(p.proconfig, ','), '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = @schema and p.proname = @name
            """,
            ("schema", schema),
            ("name", name));

    /// <summary>Whether a role holds EXECUTE on one function.</summary>
    /// <param name="role">The role name.</param>
    /// <param name="schema">The schema the function lives in.</param>
    /// <param name="name">The function name.</param>
    private Task<bool> HasExecuteAsync(string role, string schema, string name) =>
        OwnerScalarAsync<bool>(
            """
            select pg_catalog.has_function_privilege(@role, p.oid, 'execute')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = @schema and p.proname = @name
            """,
            ("role", role),
            ("schema", schema),
            ("name", name));

    /// <summary>
    /// 0048's stamp is derived reporting state: nullable, defaultless, unbackfilled, and in no index —
    /// the sweep is a bounded scan of a table that only holds live sessions and 30 days of dead ones,
    /// while an index on a deadline column would be rewritten by every slide or stamp.
    /// </summary>
    [Fact]
    public async Task Expired_At_Is_A_Nullable_Stamp_And_No_Index_Carries_It()
    {
        (await OwnerScalarAsync<string>(
            """
            select is_nullable || '|' || coalesce(column_default, '~') || '|' || data_type
              from information_schema.columns
             where table_schema = 'public' and table_name = 'session' and column_name = 'expired_at'
            """)).ShouldBe("YES|~|timestamp with time zone", "nullable and defaultless: expiry stays derived");

        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from pg_catalog.pg_indexes
             where schemaname = 'public' and tablename = 'session'
               and indexdef like '%expired_at%'
            """)).ShouldBe(0, "the sweep is bounded; a stamp index would move its cost onto every slide");
    }
}
