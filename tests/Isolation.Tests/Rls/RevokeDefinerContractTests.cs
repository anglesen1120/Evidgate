using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The security contract of migration 0101, read from the live catalogue: the administrative revoke and the audited
/// link resolvers are <c>SECURITY DEFINER</c> functions owned by <c>evidgate_owner</c>, pinned to an empty
/// <c>search_path</c> with the writer's 2000 ms bounded wait, and executable by <c>evidgate_app</c> alone. The two
/// chain helpers stay owner-only: <c>session_revoke_audit_append</c> names the revoke action and delegates, and
/// <c>audit_append_user_event</c> is the one copy of the chain append.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RevokeDefinerContractTests : HarnessTest
{
    /// <summary>The two resolvers 0101 creates or re-creates, with their exact identity arguments.</summary>
    private static readonly (string Schema, string Name, string IdentityArguments)[] Definers =
    [
        (
            "evidgate_resolve",
            "session_revoke_one",
            "p_cookie_hmacs bytea[], p_session_id uuid, p_org_id uuid, p_now timestamp with time zone, "
            + "p_request_id text, p_trace_id text"),
        (
            "evidgate_resolve",
            "login_link_identity_audited",
            "p_cookie_hmacs bytea[], p_now timestamp with time zone, p_provider text, p_subject text, "
            + "p_email text, p_identity_id uuid, p_request_id text, p_trace_id text"),
    ];

    private static readonly string[] HelperNames = ["audit_append_user_event", "session_revoke_audit_append"];

    private static readonly string[] PlatformRoles =
        ["evidgate_app", "evidgate_operator", "evidgate_worker_sched"];

    /// <summary>Binds the test to the reset-based harness (container, migrations, Respawn).</summary>
    public RevokeDefinerContractTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Both resolvers keep the definer posture: one function per name, <c>SECURITY DEFINER</c> as the owner, exactly
    /// the pinned empty <c>search_path</c> plus the 2000 ms <c>lock_timeout</c>, and EXECUTE only for the app role.
    /// </summary>
    [Fact]
    public async Task The_Revoke_And_Audited_Link_Resolvers_Are_Owner_Owned_Pinned_And_App_Granted()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        foreach (var (schema, name, identityArguments) in Definers)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select count(*) over ()::bigint,
                       p.prosecdef,
                       pg_catalog.pg_get_userbyid(p.proowner),
                       coalesce(array_to_string(p.proconfig, ','), ''),
                       coalesce((select string_agg(
                                           case when a.grantee = 0 then 'PUBLIC'
                                                else pg_catalog.pg_get_userbyid(a.grantee) end
                                           || ':' || a.privilege_type, ',' order by a.grantee)
                                   from aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                                  where a.privilege_type = 'EXECUTE'
                                    and a.grantee <> p.proowner), ''),
                       pg_catalog.pg_get_function_identity_arguments(p.oid)
                  from pg_catalog.pg_proc p
                  join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                 where n.nspname = @schema and p.proname = @name
                """;
            command.Parameters.AddWithValue("schema", schema);
            command.Parameters.AddWithValue("name", name);

            await using var reader = await command.ExecuteReaderAsync(Token);
            (await reader.ReadAsync(Token)).ShouldBeTrue($"{schema}.{name} is in the catalogue");

            var copies = reader.GetInt64(0);
            var securityDefiner = reader.GetBoolean(1);
            var owner = reader.GetString(2);
            var configuration = reader.GetString(3);
            var grantees = reader.GetString(4);
            var arguments = reader.GetString(5);

            copies.ShouldBe(1L, $"{schema}.{name}: one definition, never an overload a stale grant could reach");
            securityDefiner.ShouldBeTrue($"{schema}.{name}: the resolver reads rows the app role may not");
            owner.ShouldBe("evidgate_owner", $"{schema}.{name}: owner-owned");
            arguments.ShouldBe(identityArguments, $"{schema}.{name}: the wire signature did not drift");

            var settings = configuration
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.Split('=', 2))
                .Select(parts => (Name: parts[0], Value: parts.Length > 1 ? parts[1].Trim('"', '\'') : string.Empty))
                .ToArray();

            settings.Select(setting => setting.Name).ShouldBe(
                ["search_path", "lock_timeout"],
                $"{schema}.{name}: the pinned path and the bounded wait, and nothing else");
            settings[0].Value.ShouldBeEmpty($"{schema}.{name}: `search_path` is pinned to the empty string");
            settings[1].Value.ShouldBe(
                "2000", $"{schema}.{name}: the writer's 2000 ms bound (AuditTrail.cs AuditSql.LockTimeout)");

            grantees.ShouldBe(
                "evidgate_app:EXECUTE",
                $"{schema}.{name}: EXECUTE for the app role alone — never PUBLIC, the operator or the sweep role");
        }

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            foreach (var (schema, name, _) in Definers)
            {
                (await HasExecuteAsync(connection, role, schema, name)).ShouldBeFalse(
                    $"{role} must not call {schema}.{name}");
            }
        }
    }

    /// <summary>
    /// The two helpers are invoker-rights and reachable only from the owner-run definers: no EXECUTE for any platform
    /// role or PUBLIC, the revoke wrapper names its action and delegates, and the one insert, chain lock and id
    /// canonicalisation live in <c>audit_append_user_event</c>.
    /// </summary>
    [Fact]
    public async Task The_Chain_Helpers_Are_Owner_Only_And_The_Revoke_Wrapper_Delegates()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select p.proname,
                   p.prosecdef,
                   coalesce(array_to_string(p.proconfig, ','), ''),
                   coalesce((select string_agg(
                                       case when a.grantee = 0 then 'PUBLIC'
                                            else pg_catalog.pg_get_userbyid(a.grantee) end
                                       || ':' || a.privilege_type, ',' order by a.grantee)
                               from aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                              where a.grantee <> p.proowner), ''),
                   p.prosrc
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate' and p.proname::text = any(@names)
            """;
        command.Parameters.AddWithValue("names", HelperNames);

        var helpers = new Dictionary<string, (bool SecurityDefiner, string Configuration, string Grantees, string Body)>(
            StringComparer.Ordinal);

        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                helpers[reader.GetString(0)] =
                    (reader.GetBoolean(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            }
        }

        helpers.Keys.ShouldBe(HelperNames, ignoreOrder: true, "exactly the two helpers 0101 defines");

        foreach (var (name, helper) in helpers)
        {
            helper.SecurityDefiner.ShouldBeFalse($"{name}: a helper runs with its definer's rights, not its own");
            helper.Grantees.ShouldBeEmpty($"{name}: no non-owner role holds EXECUTE, PUBLIC included");
            helper.Configuration.ShouldBe(
                "search_path=\"\"", $"{name}: the empty search path is pinned, and nothing else");
        }

        foreach (var role in PlatformRoles)
        {
            foreach (var name in HelperNames)
            {
                (await HasExecuteAsync(connection, role, "evidgate", name)).ShouldBeFalse(
                    $"{role} must not execute {name}: it is reached through the definers alone");
            }
        }

        var wrapper = helpers["session_revoke_audit_append"].Body;
        wrapper.ShouldContain("evidgate.audit_append_user_event(", Case.Sensitive);
        wrapper.ShouldContain("'auth.session_revoke_admin'", Case.Sensitive);
        wrapper.ShouldNotContain("insert into public.audit_log", Case.Sensitive,
            "one copy of the chain append: the wrapper delegates");

        var append = helpers["audit_append_user_event"].Body;
        append.ShouldContain("insert into public.audit_log", Case.Sensitive);
        append.ShouldContain(
            "pg_advisory_xact_lock(pg_catalog.hashtext('audit:' || p_org_id::text))", Case.Sensitive);
        append.ShouldContain("p_request_id ~ '^[!-~]{1,128}$'", Case.Sensitive);
        append.ShouldContain("p_trace_id ~ '^[0-9a-f]{32}$'", Case.Sensitive);
        append.ShouldContain("v_request_id, v_trace_id", Case.Sensitive);
    }

    /// <summary>
    /// The audited definer is the app role's only link entry point: the inner writer is granted to nobody but its
    /// owner, so the role cannot link without the audit rows the same transaction owes.
    /// </summary>
    [Fact]
    public async Task The_Inner_Link_Writer_Is_Reachable_Only_Through_The_Audited_Definer()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        (await HasExecuteAsync(connection, "evidgate_app", "evidgate_resolve", "login_link_identity"))
            .ShouldBeFalse("the app role calls the audited definer, which runs the inner writer as its owner");
        (await HasExecuteAsync(connection, "evidgate_app", "evidgate_resolve", "login_link_identity_audited"))
            .ShouldBeTrue("the audited definer is the app role's link entry point");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            (await HasExecuteAsync(connection, role, "evidgate_resolve", "login_link_identity"))
                .ShouldBeFalse($"{role} must not call the inner link writer either");
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)
              from pg_catalog.pg_proc p
              cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'login_link_identity'
               and a.grantee <> p.proowner
            """;

        ((long)(await command.ExecuteScalarAsync(Token))!).ShouldBe(
            0L, "no role and not PUBLIC holds EXECUTE on the inner link writer");
    }

    /// <summary>Whether a role holds EXECUTE on one function, read from the catalogue.</summary>
    private static async Task<bool> HasExecuteAsync(
        NpgsqlConnection connection,
        string role,
        string schema,
        string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select pg_catalog.has_function_privilege(@role, p.oid, 'execute')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = @schema and p.proname = @name
            """;
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("name", name);

        return (bool)(await command.ExecuteScalarAsync(Token))!;
    }
}
