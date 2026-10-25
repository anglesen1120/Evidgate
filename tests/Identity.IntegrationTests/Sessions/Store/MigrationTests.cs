using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
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
        const string CookieValue = "a-cookie-minted-before-0045";

        await OwnerExecuteAsync(
            """
            insert into public.session
                (id, user_id, cookie_hmac, pepper_version, created_at, last_seen_at, expires_at,
                 absolute_expires_at, user_agent)
            values (@id, @user_id, @hmac, @version, @created, @created, @expires, @absolute, 'legacy-agent')
            """,
            ("id", sessionId),
            ("user_id", userId),
            ("hmac", HmacOf(Pepper, CookieValue)),
            ("version", CurrentVersion),
            ("created", clock.UtcNow),
            ("expires", clock.UtcNow.AddHours(8)),
            ("absolute", clock.UtcNow.AddDays(7)));

        var result = await AuthenticateAsync(CookieValue, clock);

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
              cross join lateral aclexplode(p.proacl) a
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = any(@names) and a.grantee = 0
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
}
