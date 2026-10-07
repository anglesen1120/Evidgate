using Evidgate.BuildingBlocks.Persistence.Isolation;
using Npgsql;

namespace Isolation.Tests;

/// <summary>One base table of the live catalogue, with the row level security facts 02.9.1-T3 asserts.</summary>
internal sealed record RlsTable(string Name, bool RowLevelSecurityEnabled, bool RowLevelSecurityForced, bool HasOrgId);

/// <summary>
/// One row level security policy, as <c>pg_get_expr</c> writes its two clauses. A clause the
/// policy does not declare is the empty string, never null, so the matcher never has to guess.
/// </summary>
internal sealed record RlsPolicy(string Table, string Using, string WithCheck);

/// <summary>One role of the live catalogue, with the two attributes that would defeat a policy (DECISIONS F5).</summary>
internal sealed record RlsRole(string Name, bool BypassRls, bool Superuser);

/// <summary>
/// One SECURITY DEFINER function of the resolver schemas, as <c>pg_proc</c> describes it.
/// <see cref="ResultColumns"/> is the OUT-column list (the <c>proargmodes</c> entries of mode
/// <c>t</c>): the only thing a resolver may return is the documented tuple (00 §5.4.8, TL T1).
/// </summary>
internal sealed record ResolverFunction(
    string Schema,
    string Name,
    string IdentityArguments,
    string Configuration,
    string Owner,
    IReadOnlyList<string> ResultColumns,
    bool AppCanExecute,
    bool PublicCanExecute)
{
    /// <summary>The function as a failure message names it: <c>schema.name(args)</c>.</summary>
    internal string Signature => $"{Schema}.{Name}({IdentityArguments})";

    /// <summary>TL T80: every SECURITY DEFINER function pins an empty <c>search_path</c>.</summary>
    internal bool EmptySearchPath =>
        Configuration.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(setting => string.Equals(setting, "search_path=\"\"", StringComparison.Ordinal));

    /// <summary>
    /// A resolver is the pre-tenant function that returns the tenant tuple: it has an
    /// <c>org_id</c> OUT column. The owner sweeps of <c>evidgate</c> return a count or a set of
    /// ids and are not resolvers, so the two are told apart by the catalogue, not by a list.
    /// </summary>
    internal bool IsResolver => ResultColumns.Contains("org_id", StringComparer.Ordinal);
}

/// <summary>
/// 02.9.1-T3: the catalogue reads the RLS suite shares. Every read is a query against
/// <c>pg_catalog</c> on the live database — the same relations <c>information_schema</c> is a
/// view over — so the suite asserts the deployed schema, never a list inside the test.
/// <para>
/// The predicate match goes through <see cref="IsolationSelfCheck.MatchesPredicate"/>, the one
/// implementation the boot check and the 02.6.1-T2 CI lint share: two copies of a structural
/// match are exactly how <c>(pred AND x) OR true</c> passed the boot gate and the lint at the
/// same time (W7-SEC-01). What is independent here is the evidence — the catalogue read — not
/// the rule.
/// </para>
/// </summary>
internal static class RlsTableCatalog
{
    /// <summary>The tenant setting the parameterised <c>set_config</c> of the tenant scope writes (00 §5.4.2).</summary>
    internal const string TenantSetting = "evidgate.org_id";

    /// <summary>The org-scoped policy predicate of a tenant table (00 §5.4.7/T2).</summary>
    internal const string TenantPredicate = "org_id = evidgate.current_org_id()";

    /// <summary>
    /// The all-org read shape a catalogue table must carry (02 §4.5, the exception list's
    /// <c>catalogue</c> kind): the built-in rows (<c>org_id</c> NULL) plus the caller's own.
    /// </summary>
    internal const string CataloguePredicate = "org_id IS NULL OR org_id = evidgate.current_org_id()";

    /// <summary>The self-scope predicate of the tenant root (TL T4): <c>organization</c> has no <c>org_id</c>.</summary>
    internal const string OrganizationPredicate = "id = evidgate.current_org_id()";

    /// <summary>Every base table in a non-system schema, with its RLS flags and whether it carries <c>org_id</c>.</summary>
    internal static async Task<IReadOnlyList<RlsTable>> ReadTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   exists (select 1
                             from pg_catalog.pg_attribute a
                            where a.attrelid = c.oid
                              and a.attname = 'org_id'
                              and a.attnum > 0
                              and not a.attisdropped)
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        var tables = new List<RlsTable>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new RlsTable(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3)));
        }

        return tables;
    }

    /// <summary>Every policy of every base table, with both clauses as <c>pg_get_expr</c> writes them.</summary>
    internal static async Task<IReadOnlyList<RlsPolicy>> ReadPoliciesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   coalesce(pg_catalog.pg_get_expr(p.polqual, p.polrelid), ''),
                   coalesce(pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid), '')
              from pg_catalog.pg_policy p
              join pg_catalog.pg_class c on c.oid = p.polrelid
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        var policies = new List<RlsPolicy>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            policies.Add(new RlsPolicy(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return policies;
    }

    /// <summary>Every role of the cluster, with the two attributes that would defeat a policy.</summary>
    internal static async Task<IReadOnlyList<RlsRole>> ReadRolesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "select rolname, rolbypassrls, rolsuper from pg_catalog.pg_roles order by rolname";

        var roles = new List<RlsRole>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new RlsRole(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return roles;
    }

    /// <summary>
    /// Every SECURITY DEFINER function of the resolver schemas (<c>evidgate_resolve</c>) and of
    /// the helper/table schemas (<c>evidgate</c>, <c>public</c>), with its owner, its pinned
    /// configuration, its OUT columns and the two EXECUTE facts the suite asserts. The
    /// enumeration is catalogue-driven, so a resolver added by a later migration is covered the
    /// moment it lands.
    /// </summary>
    internal static async Task<IReadOnlyList<ResolverFunction>> ReadResolversAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname,
                   p.proname,
                   pg_catalog.pg_get_function_identity_arguments(p.oid),
                   coalesce(array_to_string(p.proconfig, ','), ''),
                   r.rolname,
                   coalesce(array_to_string(p.proargnames, ','), ''),
                   coalesce(array_to_string(p.proargmodes, ','), ''),
                   has_function_privilege('evidgate_app'::name, p.oid, 'execute'),
                   exists (select 1
                             from aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                            where a.grantee = 0
                              and a.privilege_type = 'EXECUTE')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
              join pg_catalog.pg_roles r on r.oid = p.proowner
             where p.prosecdef
               and n.nspname in ('evidgate_resolve', 'evidgate', 'public')
             order by n.nspname, p.proname
            """;

        var functions = new List<ResolverFunction>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var names = Split(reader.GetString(5));
            var modes = Split(reader.GetString(6));
            var resultColumns = new List<string>();
            for (var index = 0; index < names.Length && index < modes.Length; index++)
            {
                if (string.Equals(modes[index], "t", StringComparison.Ordinal))
                {
                    resultColumns.Add(names[index]);
                }
            }

            functions.Add(new ResolverFunction(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                resultColumns,
                reader.GetBoolean(7),
                reader.GetBoolean(8)));
        }

        return functions;
    }

    /// <summary>True when the relation exists (a table the exception list names may not exist yet).</summary>
    internal static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select to_regclass(@table) is not null";
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The table-level privilege of one role on one existing relation.</summary>
    internal static async Task<bool> HasTablePrivilegeAsync(
        NpgsqlConnection connection,
        string role,
        string table,
        string privilege,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select has_table_privilege(@role::name, @table, @privilege)";
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Resets the tenant setting to its default inside the scope's transaction. PostgreSQL 18
    /// (<c>functions-admin</c>, <c>set_config</c>): "set_config accepts the NULL value for
    /// new_value, but as settings cannot be null, it is interpreted as a request to reset the
    /// setting to its default value." For a placeholder setting that has been touched in a
    /// session the reset value is the empty string rather than SQL NULL — and
    /// <c>evidgate.current_org_id()</c> maps both to NULL through its <c>NULLIF(…, '')</c>, so
    /// the fail-closed read is the same one a never-set session gets.
    /// </summary>
    internal static async Task ResetTenantAsync(TenantScope scope)
    {
        await scope.ScalarAsync<string?>("select set_config(@key, null, true)", ("key", TenantSetting));
    }

    /// <summary>True when either clause of the policy carries the predicate (the catalogue rule of 02 §4.5).</summary>
    internal static bool Matches(RlsPolicy policy, string predicate) =>
        (policy.Using.Length > 0 && IsolationSelfCheck.MatchesPredicate(policy.Using, predicate))
        || (policy.WithCheck.Length > 0 && IsolationSelfCheck.MatchesPredicate(policy.WithCheck, predicate));

    private static string[] Split(string commaSeparated) =>
        commaSeparated.Length == 0
            ? []
            : commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
