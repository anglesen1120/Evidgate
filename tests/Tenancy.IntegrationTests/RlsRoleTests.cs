using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC2 / 00 §5.4.3 / DECISIONS F5: the runtime role cannot bypass row level
/// security and does not own the schema it reads. Asserted from the database's own
/// catalogue on a database brought to head by the real migrations (0001 creates the
/// four roles), because the invariant is a property of the deployed roles, not of
/// application code — an `evidgate_app` that owned a table could `ALTER TABLE …
/// DISABLE ROW LEVEL SECURITY` and every policy on it would stop applying.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class RlsRoleTests
{
    /// <summary>The roles that must never be able to bypass or disable a policy.</summary>
    private static readonly string[] RuntimeRoles =
    [
        "evidgate_app",
        "evidgate_operator",
        "evidgate_worker_sched",
    ];

    private readonly PostgresTenancyFixture _fixture;

    public RlsRoleTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AppRole_HasNoBypassRls_AndOwnsNoTables()
    {
        var database = await _fixture.CreateDatabaseAsync();

        // 1. No role in the split carries an attribute that bypasses a policy:
        //    BYPASSRLS skips every policy and SUPERUSER skips them too (DECISIONS F5).
        //    The migration role is included on purpose — 0001 refuses to continue when
        //    it finds one of these set, and this is the independent half of that pair.
        var attributes = await _fixture.ColumnAsync<string>(
            database,
            """
            select rolname || ':' || rolbypassrls::text || ':' || rolsuper::text
              from pg_catalog.pg_roles
             where rolname in ('evidgate_owner', 'evidgate_app', 'evidgate_operator', 'evidgate_worker_sched')
             order by rolname
            """);

        attributes.ShouldBe(
            [
                "evidgate_app:false:false",
                "evidgate_operator:false:false",
                "evidgate_owner:false:false",
                "evidgate_worker_sched:false:false",
            ]);

        // 2. The three runtime roles own nothing: a `relowner` can always turn RLS off
        //    on its own table, so ownership is the back door the role split closes.
        //    Relations, sequences and views are all covered by relkind in ('r','p','v','m','S').
        var ownedRelations = await _fixture.ColumnAsync<string>(
            database,
            """
            select r.rolname || ':' || c.relkind::text || ':' || n.nspname || '.' || c.relname
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
              join pg_catalog.pg_roles r on r.oid = c.relowner
             where c.relkind in ('r', 'p', 'v', 'm', 'S')
               and r.rolname in ('evidgate_app', 'evidgate_operator', 'evidgate_worker_sched')
             order by 1
            """);

        ownedRelations.ShouldBeEmpty();

        var ownedSchemas = await _fixture.ColumnAsync<string>(
            database,
            """
            select r.rolname || ':' || n.nspname
              from pg_catalog.pg_namespace n
              join pg_catalog.pg_roles r on r.oid = n.nspowner
             where r.rolname in ('evidgate_app', 'evidgate_operator', 'evidgate_worker_sched')
             order by 1
            """);

        ownedSchemas.ShouldBeEmpty();

        // 3. The check is not vacuous: the migration role does own the migrated schema,
        //    so "no runtime role owns anything" is a statement about the split and not
        //    about an empty database.
        var ownerRelations = await _fixture.ScalarAsync<long>(
            database,
            """
            select count(*)
              from pg_catalog.pg_class c
              join pg_catalog.pg_roles r on r.oid = c.relowner
             where r.rolname = 'evidgate_owner'
               and c.relkind in ('r', 'p')
            """);

        ownerRelations.ShouldBeGreaterThan(0);

        // 4. A role that may not create in `public` can never become an owner there
        //    (0001 revokes CREATE from the three runtime roles; PostgreSQL 15+ makes
        //    pg_database_owner hold it instead).
        foreach (var role in RuntimeRoles)
        {
            var mayCreate = await _fixture.ScalarAsync<bool>(
                database,
                $"select has_schema_privilege('{role}', 'public', 'CREATE')");

            mayCreate.ShouldBeFalse($"{role} must not be able to create objects in public");
        }
    }
}
