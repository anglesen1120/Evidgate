using System.Globalization;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

public sealed partial class RlsIsolationTests
{
    /// <summary>One relation of the live catalogue, with the facts the SEC6 scan needs.</summary>
    private sealed record RelationFacts(string Name, string Kind, string Options, bool TableSelect, bool ColumnSelect);

    /// <summary>
    /// The tenant set of the live database, discovered exactly as the composite-FK suite discovers
    /// it, with the floor asserted so a broken discovery fails here rather than passing vacuously.
    /// </summary>
    private async Task<TenantFkScanResult> DiscoverAsync()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        var scan = await TenantFkScan.RunAsync(connection, Token);

        scan.Exceptions.ShouldNotBeEmpty(
            $"{RlsExceptionList.PathInRepo} listed no table — the suite cannot derive the tenant set without it");
        scan.TenantTables.ShouldNotBeEmpty(
            "the catalogue yielded no tenant table (a table with org_id minus the exception list): the suite would prove nothing");
        foreach (var floor in FloorTables)
        {
            scan.TenantTables.ShouldContain(
                floor,
                $"the M1 business table {floor} is missing from the discovered tenant set [{string.Join(", ", scan.TenantTables)}]");
        }

        return scan;
    }

    /// <summary>
    /// AC6 / 00 §5.7 S-suite: the suite runs as <c>evidgate_app</c>, never the owner, and the role
    /// carries no <c>BYPASSRLS</c> — otherwise every per-table case below could pass for the wrong
    /// reason.
    /// </summary>
    private async Task AssertAppRoleAsync()
    {
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString);

        (await scope.ScalarAsync<string>("select current_user"))
            .ShouldBe("evidgate_app", "the isolation suite must run as the application role, never the owner");
        (await scope.ScalarAsync<bool>("select rolbypassrls from pg_catalog.pg_roles where rolname = current_user"))
            .ShouldBeFalse("evidgate_app must not carry BYPASSRLS (DECISIONS F5)");
    }

    /// <summary>The ids of one table visible to the scope's current tenant setting.</summary>
    private static async Task<IReadOnlyList<Guid>> VisibleIdsAsync(TenantScope scope, string table) =>
        await scope.ScalarAsync<Guid[]>($"select coalesce(array_agg(id order by id), '{{}}'::uuid[]) from {table}");

    private static string Ids(IReadOnlyList<Guid> ids) =>
        string.Join(", ", ids.Select(id => id.ToString("D", CultureInfo.InvariantCulture)));

    /// <summary>
    /// The <c>org_id</c> nullability of every base table that carries the column, keyed by
    /// <c>schema.table</c>; the value is <c>true</c> when the column is NOT NULL. A table without
    /// the column is absent. Read from <c>pg_attribute</c>, so the exception list's kinds are
    /// checked against the deployed schema rather than trusted.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, bool>> ReadOrgIdNullabilityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   a.attnotnull
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
              join pg_catalog.pg_attribute a on a.attrelid = c.oid
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
               and a.attname = 'org_id'
               and a.attnum > 0
               and not a.attisdropped
            """;

        var nullability = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            nullability[reader.GetString(0)] = reader.GetBoolean(1);
        }

        return nullability;
    }

    /// <summary>
    /// Every <c>evidgate_*</c> role that is a member, directly or transitively, of a role carrying
    /// <c>SUPERUSER</c> or <c>BYPASSRLS</c>, as <c>member -&gt; target</c>. The recursion walks
    /// <c>pg_auth_members</c> so a two-hop membership cannot hide behind a role whose own
    /// <c>rolbypassrls</c> is false (0001_foundation_roles.sql:42-79).
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadBypassRoleReachAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            with recursive reach(roleid, member) as (
              select m.roleid, m.member from pg_catalog.pg_auth_members m
              union
              select r.roleid, a.member
                from pg_catalog.pg_auth_members r
                join reach a on a.roleid = r.member
            )
            select distinct mem.rolname || ' -> ' || tgt.rolname
              from reach
              join pg_catalog.pg_roles mem on mem.oid = reach.member
              join pg_catalog.pg_roles tgt on tgt.oid = reach.roleid
             where mem.rolname like 'evidgate\_%'
               and (tgt.rolsuper or tgt.rolbypassrls)
             order by 1
            """;

        var reach = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reach.Add(reader.GetString(0));
        }

        return reach;
    }

    /// <summary>The number of roles that carry <c>SUPERUSER</c> or <c>BYPASSRLS</c> — the non-vacuity guard of the membership scan.</summary>
    private static async Task<int> CountBypassRolesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*)::int from pg_catalog.pg_roles where rolsuper or rolbypassrls";
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// SEC2: every (base table, runtime role, privilege) triple where the role holds
    /// <c>TRUNCATE</c>, <c>REFERENCES</c> or <c>TRIGGER</c>. The scan is catalogue-driven, so a
    /// table added by a later migration is covered the moment it lands.
    /// </summary>
    private static async Task<IReadOnlyList<(string Table, string Role, string Privilege)>> ReadTablePrivilegeViolationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   r.role_name,
                   p.privilege
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
              cross join (values ('evidgate_app'), ('evidgate_operator')) as r(role_name)
              cross join (values ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) as p(privilege)
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
               and has_table_privilege(r.role_name::name, c.oid, p.privilege)
             order by 1, 2, 3
            """;

        var violations = new List<(string Table, string Role, string Privilege)>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            violations.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return violations;
    }

    /// <summary>
    /// SEC6: every relation of a non-system schema with its kind, its <c>reloptions</c> and
    /// whether <c>evidgate_app</c> can SELECT it (table-level, or any column for a view or
    /// foreign table). The scan is catalogue-driven, so a view added by a later migration is
    /// covered the moment it lands.
    /// </summary>
    private static async Task<IReadOnlyList<RelationFacts>> ReadRelationFactsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   c.relkind::text,
                   coalesce(array_to_string(c.reloptions, ','), ''),
                   has_table_privilege('evidgate_app', c.oid, 'SELECT'),
                   case when c.relkind in ('v', 'f')
                        then has_any_column_privilege('evidgate_app', c.oid, 'SELECT')
                        else false
                   end
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p', 'v', 'm', 'f')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        var relations = new List<RelationFacts>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            relations.Add(new RelationFacts(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return relations;
    }
}
