using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 02.9.1-T3.2 (story 02.9.1 §8 T1, §9 "Resolver surface"; 00 §5.4.8, TL T1/T80):
/// the pre-tenant resolver surface. A request authenticates before any tenant exists, so the
/// resolvers of <c>evidgate_resolve</c> are the one sanctioned way across the tenant boundary —
/// and they must be exactly that: owner-owned, <c>SECURITY DEFINER</c>, pinned to an empty
/// <c>search_path</c>, executable by <c>evidgate_app</c> and nobody else, returning only the
/// documented tuple, while the tables they read stay org-scoped for a direct read.
/// <para>
/// The function set is read from <c>pg_proc</c> across every non-system schema, so a definer
/// added by a later migration is covered the moment it lands; the documented tuple per function
/// is the contract of 00 §5.4.8 ("return only <c>(org_id, project_id, binding_id, status)</c>"),
/// and a resolver that is not registered here fails the suite by name. A definer outside
/// <c>evidgate_resolve</c> is a sweep and must be on the sweep allowlist, so a new definer
/// cannot be added without being classified.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ResolverTests : HarnessTest
{
    /// <summary>
    /// The documented result tuple of every resolver, keyed by <c>schema.name</c>. A resolver is
    /// a <c>SECURITY DEFINER</c> function of <c>evidgate_resolve</c>; the tuple is the only
    /// thing it may return (00 §5.4.8, TL T1), never a column of the table it reads.
    /// </summary>
    private static readonly Dictionary<string, string[]> DocumentedResolvers = new(StringComparer.Ordinal)
    {
        ["evidgate_resolve.member_org"] = ["org_id", "project_id", "binding_id", "status"],
        ["evidgate_resolve.member_orgs"] = ["org_id", "project_id", "binding_id", "status"],
        ["evidgate_resolve.member_organizations"] = ["org_id", "slug", "name", "status"],
        ["evidgate_resolve.resolve_ci_binding"] = ["org_id", "project_id", "binding_id", "status"],
        ["evidgate_resolve.resolve_ingest_token"] = ["org_id", "project_id", "binding_id", "status"],
        ["evidgate_resolve.login_upsert_identity"] = ["user_id", "status"],
        ["evidgate_resolve.session_create"] = ["session_id", "expires_at", "status"],
        ["evidgate_resolve.invite_by_secret"] = ["org_id", "membership_id"],
        ["evidgate_resolve.invite_autobind_candidates"] = ["org_id", "membership_id"],
        ["evidgate_resolve.session_authenticate"] =
            ["status", "session_id", "user_id", "rotation_required", "slid", "rehashed"],
        ["evidgate_resolve.session_rotate"] = ["session_id", "expires_at", "absolute_expires_at", "status"],
        ["evidgate_resolve.session_revoke_all_for_user"] = ["revoked_count"],
        ["evidgate_resolve.session_require_rotation"] = ["marked_count"],
        ["evidgate_resolve.session_list"] =
        [
            "session_id", "is_current", "created_at", "last_seen_at", "expires_at", "absolute_expires_at",
            "ip", "user_agent", "status",
        ],
        ["evidgate_resolve.session_revoke_current"] = ["status"],
        ["evidgate_resolve.session_revoke_one"] = ["status"],
        ["evidgate_resolve.session_revoke_others"] = ["status", "revoked_count"],
        ["evidgate_resolve.login_link_identity"] = ["outcome", "moved_from_user_id"],
    };

    /// <summary>
    /// The resolvers that act on platform-scope rows (<c>user</c>, <c>user_identity</c>, <c>session</c>, 0041:
    /// no <c>org_id</c> column, owner-only policies): a federated login (02.3.1-T1.4) resolves a person, not an
    /// org, and the session lifecycle (02.3.2-T1.1) writes the credential of a person, so the tenant-tuple rule
    /// of 00 §5.4.8 does not apply to their first column — a session status, a rotation result or a row count is
    /// not a tenant tuple, and the two revocation writes run inside a caller's tenant transaction precisely
    /// because they are NOT a lookup. The session routes (02.3.2-T2.1, migration 0048) are the same shape: the
    /// actor comes from the presented cookie hash, so <c>session_list</c> projects the caller's own sign-in
    /// history (its first column is the session id, never a tenant) and the three route writes answer a status —
    /// the one cross-tenant act, an admin ending a member's session, is authorized INSIDE
    /// <c>session_revoke_one</c> against <c>p_org_id</c> and still answers a status, not a tenant tuple. Their
    /// documented tuple above is still pinned exactly, and every other resolver keeps the rule.
    /// </summary>
    private static readonly HashSet<string> PlatformScopeResolvers = new(StringComparer.Ordinal)
    {
        "evidgate_resolve.login_upsert_identity",
        "evidgate_resolve.session_create",
        "evidgate_resolve.session_authenticate",
        "evidgate_resolve.session_rotate",
        "evidgate_resolve.session_revoke_all_for_user",
        "evidgate_resolve.session_require_rotation",
        "evidgate_resolve.session_list",
        "evidgate_resolve.session_revoke_current",
        "evidgate_resolve.session_revoke_one",
        "evidgate_resolve.session_revoke_others",
        "evidgate_resolve.login_link_identity",
    };

    /// <summary>
    /// The <c>SECURITY DEFINER</c> functions outside <c>evidgate_resolve</c>: the owner sweeps of
    /// <c>evidgate</c> (TL T80), which return a count or a set of ids rather than the tenant
    /// tuple. Every definer in any non-system schema that is not in <c>evidgate_resolve</c> must
    /// be named here or the suite fails by name, so a new definer cannot be added without being
    /// classified. The value is the migration that creates it.
    /// </summary>
    private static readonly Dictionary<string, string> SweepAllowlist = new(StringComparer.Ordinal)
    {
        ["evidgate.purge_expired_idempotency"] = "0007_platform_idempotency_record.sql:137",
        ["evidgate.audit_org_ids"] = "0015_identity_audit_log.sql:132",
        ["evidgate.purge_expired_ci_token_issue"] =
            "0017_identity_ci_token_issue.sql:67 (redefined 0019_identity_ci_review_fixes.sql:8)",
        ["evidgate.expire_pending_invites"] = "0043_identity_invite_flow.sql:177",
        ["evidgate.session_cleanup"] = "0048_identity_session_routes.sql:479",
    };

    /// <summary>
    /// The tables the resolvers read (0014: <c>membership</c>/<c>organization</c>; 0016:
    /// <c>ci_binding</c>; 0018: <c>ingest_token</c>), with the policy predicate that keeps a
    /// direct read inside one org.
    /// </summary>
    private static readonly (string Table, string OrgPredicate)[] ResolvedTables =
    [
        ("public.membership", RlsTableCatalog.TenantPredicate),
        ("public.organization", RlsTableCatalog.OrganizationPredicate),
        ("public.ci_binding", RlsTableCatalog.TenantPredicate),
        ("public.ingest_token", RlsTableCatalog.TenantPredicate),
    ];

    /// <summary>
    /// The platform identity tables of AC5 (D12): they hold no customer business data and are
    /// reached only through a membership, never by a direct tenant read. The contract is
    /// therefore stronger than the resolved tables' — the app role must hold no direct SELECT
    /// on them at all, FORCE row level security or not (02.9.1 §8 T1). The exception list
    /// allows the entry before the migration that creates the table, so an absent one is reported
    /// as inert rather than skipped.
    /// </summary>
    private static readonly string[] PlatformIdentityTables = ["public.user", "public.user_identity", "public.session"];

    /// <summary>
    /// The table-level privileges a platform identity table must never grant the app role. A
    /// direct SELECT is the read hole of AC5; a write privilege is a hole too — it lets the app
    /// role mutate an identity row outside the resolver and the membership path (SEC5).
    /// </summary>
    private static readonly string[] PlatformIdentityWritePrivileges =
        ["INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES"];

    private readonly ITestOutputHelper _output;

    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public ResolverTests(DatabaseFixture fixture, ITestOutputHelper output)
        : base(fixture)
    {
        _output = output;
    }

    /// <summary>
    /// AC5 / §8 T1: every resolver is owner-owned, <c>SECURITY DEFINER</c>, <c>search_path</c>-pinned,
    /// PUBLIC-closed and app-executable, returns exactly its documented tuple, and the tables it
    /// reads are org-scoped for a direct read — so the resolver is the only path that crosses the
    /// tenant boundary.
    /// </summary>
    [Fact]
    public async Task AppRole_ExecuteOnly_NoDirectReads()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);

        var functions = await RlsTableCatalog.ReadResolversAsync(connection, Token);
        functions.ShouldNotBeEmpty(
            "no SECURITY DEFINER function was found in any non-system schema: the resolver surface is empty");

        var failures = new List<string>();

        // 1. Hygiene of every SECURITY DEFINER function (TL T80). The sweep functions of
        //    evidgate (purge_expired_*, audit_org_ids) are in this set too: they are owner-owned
        //    definers as well, and PUBLIC must not be able to call any of them.
        foreach (var function in functions)
        {
            if (!string.Equals(function.Owner, "evidgate_owner", StringComparison.Ordinal))
            {
                failures.Add($"{function.Signature}: owner is {function.Owner}, expected evidgate_owner");
            }

            if (!function.EmptySearchPath)
            {
                failures.Add(
                    $"{function.Signature}: search_path is not pinned to '' (proconfig = '{function.Configuration}') — "
                    + "a definer function must not resolve an object the caller could shadow (TL T80)");
            }

            if (function.PublicCanExecute)
            {
                failures.Add($"{function.Signature}: PUBLIC still has EXECUTE — every definer function is owner-only plus explicit grants");
            }
        }

        // 2. The resolver surface: exactly the documented set, each in evidgate_resolve, each
        //    executable by the app role, each returning only its documented tuple.
        var resolvers = functions.Where(function => function.IsResolver).ToList();
        resolvers.ShouldNotBeEmpty(
            "no SECURITY DEFINER function was found in evidgate_resolve: the pre-tenant path would have no sanctioned way to resolve a tenant");

        // 2b. Every definer outside evidgate_resolve is a sweep and must be allowlisted by name.
        //     The schema, not the result shape, decides (SEC3): a definer in evidgate_resolve
        //     returning a different tuple is a resolver and is compared below; a definer
        //     anywhere else is a sweep and must be registered here.
        var sweeps = functions.Where(function => !function.IsResolver).ToList();
        foreach (var sweep in sweeps)
        {
            var sweepKey = $"{sweep.Schema}.{sweep.Name}";
            if (!SweepAllowlist.ContainsKey(sweepKey))
            {
                failures.Add(
                    $"{sweep.Signature}: a SECURITY DEFINER function outside evidgate_resolve that is not on the sweep "
                    + "allowlist — a resolver belongs in evidgate_resolve, and a sweep must be registered with the "
                    + "migration that creates it");
            }
        }

        var allowlistWithoutFunction = SweepAllowlist.Keys
            .Except(sweeps.Select(sweep => $"{sweep.Schema}.{sweep.Name}"), StringComparer.Ordinal)
            .ToArray();
        if (allowlistWithoutFunction.Length > 0)
        {
            failures.Add(
                $"the sweep allowlist names [{string.Join(", ", allowlistWithoutFunction)}] but no such SECURITY DEFINER "
                + "function exists — remove the stale entry");
        }

        var discovered = resolvers.Select(resolver => $"{resolver.Schema}.{resolver.Name}").ToArray();
        var documented = DocumentedResolvers.Keys.ToArray();
        var missing = documented.Except(discovered, StringComparer.Ordinal).ToArray();
        var unregistered = discovered.Except(documented, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || unregistered.Length > 0)
        {
            failures.Add(
                $"the resolver set does not match the documented one — missing [{string.Join(", ", missing)}], "
                + $"unregistered [{string.Join(", ", unregistered)}]: register a new resolver with its documented tuple");
        }

        foreach (var resolver in resolvers)
        {
            var key = $"{resolver.Schema}.{resolver.Name}";

            if (!string.Equals(resolver.Schema, "evidgate_resolve", StringComparison.Ordinal))
            {
                failures.Add($"{resolver.Signature}: a resolver must live in evidgate_resolve (00 §5.4.8)");
            }

            if (!resolver.AppCanExecute)
            {
                failures.Add($"{resolver.Signature}: evidgate_app has no EXECUTE — the pre-tenant path cannot resolve a tenant");
            }

            if (!PlatformScopeResolvers.Contains(key)
                && (resolver.ResultColumns.Count == 0
                    || !string.Equals(resolver.ResultColumns[0], "org_id", StringComparison.Ordinal)))
            {
                failures.Add($"{resolver.Signature}: the first result column must be org_id (the tenant tuple of 00 §5.4.8)");
            }

            if (DocumentedResolvers.TryGetValue(key, out var expected)
                && !resolver.ResultColumns.SequenceEqual(expected, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{resolver.Signature}: returns ({string.Join(", ", resolver.ResultColumns)}), expected the documented tuple "
                    + $"({string.Join(", ", expected)}) — a resolver returns the tuple, never a column of the table it reads");
            }

            _output.WriteLine(
                "{0}: owner={1} search_path='' public_execute={2} app_execute={3} returns ({4})",
                resolver.Signature,
                resolver.Owner,
                resolver.PublicCanExecute,
                resolver.AppCanExecute,
                string.Join(", ", resolver.ResultColumns));
        }

        // 3. "No direct read on the resolved tables": the app role may hold SELECT on them, but
        //    only under FORCE row level security with an org-scoped policy — otherwise the
        //    resolver would not be the only path that crosses the tenant boundary.
        var tables = await RlsTableCatalog.ReadTablesAsync(connection, Token);
        var policies = await RlsTableCatalog.ReadPoliciesAsync(connection, Token);
        var tablesByName = tables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        var policiesByTable = policies
            .GroupBy(policy => policy.Table, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var (table, predicate) in ResolvedTables)
        {
            if (!tablesByName.TryGetValue(table, out var info))
            {
                failures.Add($"{table}: a resolver reads it but the table does not exist in the catalogue");
                continue;
            }

            var tableSelect = await RlsTableCatalog.HasTablePrivilegeAsync(connection, "evidgate_app", table, "SELECT", Token);
            var columnSelect = await RlsTableCatalog.HasAnyColumnPrivilegeAsync(connection, "evidgate_app", table, "SELECT", Token);
            if (!tableSelect && !columnSelect)
            {
                _output.WriteLine("{0}: evidgate_app has no direct SELECT — reached only through the resolver", table);
                continue;
            }

            if (!info.RowLevelSecurityForced)
            {
                failures.Add(
                    $"{table}: evidgate_app holds a direct SELECT and row level security is not FORCED — a direct read could cross orgs");
            }

            var scoped = (policiesByTable.GetValueOrDefault(table) ?? [])
                .Any(policy => RlsTableCatalog.Matches(policy, predicate));
            if (!scoped)
            {
                failures.Add(
                    $"{table}: evidgate_app holds a direct SELECT and no policy matches '{predicate}' — the resolver would not be "
                    + "the only path across the tenant boundary");
            }

            _output.WriteLine(
                "{0}: evidgate_app SELECT is org-scoped (table={1}, column={2}, force={3}, predicate matched={4})",
                table,
                tableSelect,
                columnSelect,
                info.RowLevelSecurityForced,
                scoped);
        }

        // 4. The platform identity tables: absent ones are inert (the exception list allows the
        //    entry first); a present one the app role can read or write at all is a hole. These
        //    tables are reached only through evidgate_resolve and membership, so no FORCE flag
        //    and no policy makes a direct SELECT acceptable, and no write privilege is either —
        //    the resolver must be the only path across the tenant boundary (00 §5.4.8, TL T1;
        //    02.9.1 §8 T1).
        foreach (var table in PlatformIdentityTables)
        {
            if (!await RlsTableCatalog.TableExistsAsync(connection, table, Token))
            {
                _output.WriteLine("{0}: inert — the table does not exist yet", table);
                continue;
            }

            var tableSelect = await RlsTableCatalog.HasTablePrivilegeAsync(connection, "evidgate_app", table, "SELECT", Token);
            var columnSelect = await RlsTableCatalog.HasAnyColumnPrivilegeAsync(connection, "evidgate_app", table, "SELECT", Token);
            if (tableSelect || columnSelect)
            {
                failures.Add(
                    $"{table}: evidgate_app holds a direct SELECT (table={tableSelect}, column={columnSelect}) on a platform "
                    + "identity table — it is reached only through evidgate_resolve and membership, so the resolver would not "
                    + "be the only path across the tenant boundary (00 §5.4.8, TL T1)");
            }

            foreach (var privilege in PlatformIdentityWritePrivileges)
            {
                if (await RlsTableCatalog.HasTablePrivilegeAsync(connection, "evidgate_app", table, privilege, Token))
                {
                    failures.Add(
                        $"{table}: evidgate_app holds {privilege} on a platform identity table — the app role must not write "
                        + "an identity row outside the resolver and the membership path (02.9.1 §8 T1)");
                }
            }

            _output.WriteLine(
                "{0}: evidgate_app SELECT table={1} column={2} force={3} policies={4}",
                table,
                tableSelect,
                columnSelect,
                tablesByName.GetValueOrDefault(table)?.RowLevelSecurityForced ?? false,
                (policiesByTable.GetValueOrDefault(table) ?? []).Count);
        }

        failures.ShouldBeEmpty(
            $"the resolver surface is not the only path across the tenant boundary:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// F55 (0050): the one resolver that ends ANOTHER member's session is also the one that records it,
    /// inside its own transaction — the <c>definer_audit</c> rule of <c>scripts/check-rls.py</c> is now a
    /// property of the live catalogue, not only of the file. The append uses the application writer's own
    /// advisory lock key (<c>AuditSql.AdvisoryLock</c>) and its bounded wait, so a SQL row and a C# row
    /// serialise on one chain, and only the admin branch writes: a self revoke records nothing.
    /// </summary>
    [Fact]
    public async Task The_Admin_Revoke_Resolver_Carries_Its_Own_Audit_Row()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select p.prosrc
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
            """;

        var body = (string)(await command.ExecuteScalarAsync(Token))!;

        body.ShouldContain("insert into public.audit_log", Case.Sensitive,
            "the row must be written inside the definer, so it commits or rolls back with the revoke");
        body.ShouldContain("'auth.session_revoke_admin'", Case.Sensitive,
            "the action 0048's handler wrote from a second transaction");
        body.ShouldContain("pg_advisory_xact_lock(hashtext('audit:' || p_org_id::text))", Case.Sensitive,
            "the same chain lock the application writer takes (AuditTrail.cs AuditSql.AdvisoryLock)");
        body.ShouldContain("set_config('lock_timeout', '2000'", Case.Sensitive,
            "the writer's bounded wait, so a busy org answers 55P03 instead of parking a pooled connection");
        body.ShouldContain("v_target_user <> v_actor", Case.Sensitive,
            "the row is the administrative act only: a self revoke writes nothing");
    }
}
