using System.Globalization;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// AC5 and 01.4.1-T2/T3: the application, operator and sweep roles are what the
/// migrations say they are, and the app role provably cannot leave its tenant —
/// not by reading, not by writing, not by turning row security off, and not by
/// becoming the table owner.
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IsolationTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid RowA1 = Guid.Parse("aaaaaaaa-0000-7000-8000-000000000001");
    private static readonly Guid RowA2 = Guid.Parse("aaaaaaaa-0000-7000-8000-000000000002");
    private static readonly Guid RowB1 = Guid.Parse("bbbbbbbb-0000-7000-8000-000000000001");

    private readonly PostgresIsolationFixture _fixture;

    public IsolationTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    /// <summary>AC5: as the app role, another tenant's rows are neither readable nor writable, and its own org cannot be forged.</summary>
    [Fact]
    public async Task App_Role_Cannot_Cross_Tenant()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var app = _fixture.AppConnectionString(database);
        await SeedAsync(app);

        await using (var scope = await TenantScope.BeginAsync(app, OrgA))
        {
            (await scope.ScalarAsync<int>("select count(*)::int from public.rls_probe")).ShouldBe(2);
            (await scope.ScalarAsync<int>("select count(*)::int from public.rls_probe where org_id = @org", ("org", OrgB)))
                .ShouldBe(0);
            (await scope.ScalarAsync<int>("select count(*)::int from public.rls_probe where id = @id", ("id", RowB1)))
                .ShouldBe(0);
            (await scope.ExecuteAsync("update public.rls_probe set label = 'stolen' where id = @id", ("id", RowB1)))
                .ShouldBe(0);
            (await scope.ExecuteAsync("delete from public.rls_probe where id = @id", ("id", RowB1)))
                .ShouldBe(0);

            // The WITH CHECK half of the policy: a row for another org cannot be written.
            var forged = await scope.CaptureErrorAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'forged')",
                ("org", OrgB),
                ("id", Guid.NewGuid()));
            forged.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            forged.MessageText.ShouldContain("row-level security");

            // The other tenant's row is untouched.
        }

        await using (var ownerCheck = await TenantScope.BeginAsync(app, OrgB))
        {
            (await ownerCheck.ScalarAsync<string>("select label from public.rls_probe where id = @id", ("id", RowB1)))
                .ShouldBe("B-1");
        }

        // No tenant context at all: zero rows, never every row and never an error.
        await using (var noTenant = await TenantScope.BeginAsync(app))
        {
            (await noTenant.ScalarAsync<int>("select count(*)::int from public.rls_probe")).ShouldBe(0);
        }

        // Nor can a row be inserted without a tenant context (current_org_id() is NULL).
        await using (var noTenant = await TenantScope.BeginAsync(app))
        {
            var error = await noTenant.CaptureErrorAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'forged')",
                ("org", OrgA),
                ("id", Guid.NewGuid()));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    /// <summary>AC5: the app role is not the owner and cannot switch row security off.</summary>
    [Fact]
    public async Task App_Role_Cannot_Disable_RLS()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var app = _fixture.AppConnectionString(database);
        await SeedAsync(app);

        await using (var scope = await TenantScope.BeginAsync(app, OrgA))
        {
            await scope.ExecuteAsync("set local row_security = off");
            var error = await scope.CaptureErrorAsync("select count(*)::int from public.rls_probe");
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            error.MessageText.ShouldContain("row-level security");
        }

        await using (var scope = await TenantScope.BeginAsync(app, OrgA))
        {
            (await scope.CaptureErrorAsync("alter table public.rls_probe disable row level security"))
                .MessageText.ShouldContain("must be owner");
        }

        await using (var scope = await TenantScope.BeginAsync(app, OrgA))
        {
            (await scope.CaptureErrorAsync("alter table public.rls_probe no force row level security"))
                .MessageText.ShouldContain("must be owner");
        }

        await using (var scope = await TenantScope.BeginAsync(app, OrgA))
        {
            (await scope.CaptureErrorAsync("set role evidgate_owner"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using (var scope = await TenantScope.BeginAsync(app, OrgA))
        {
            (await scope.CaptureErrorAsync("set session authorization evidgate_owner"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // FORCE is still on afterwards, and the app role holds neither SUPERUSER nor BYPASSRLS.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select relforcerowsecurity from pg_class where oid = 'public.rls_probe'::regclass")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select rolsuper or rolbypassrls from pg_roles where rolname = 'evidgate_app'")).ShouldBeFalse();
    }

    /// <summary>01.4.1-T2.4: the app role cannot escalate into DDL, ownership or the write side of the journal.</summary>
    [Fact]
    public async Task Privilege_Escalation_Attempts_Fail()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var app = _fixture.AppConnectionString(database);
        await SeedAsync(app);

        var attempts = new[]
        {
            "create table public.escalation (id int)",
            "create schema escalation",
            "create table evidgate.escalation (id int)",
            "create table evidgate_resolve.escalation (id int)",
            "drop table public.rls_probe",
            "alter table public.rls_probe add column secret text",
            "truncate public.rls_probe",
            "create role escalation_role",
            "insert into public.schema_migration (name, checksum) values ('9999_fake.sql', 'x')",
            "update public.schema_migration set checksum = 'x'",
            "delete from public.schema_migration",
        };

        foreach (var sql in attempts)
        {
            await using var scope = await TenantScope.BeginAsync(app, OrgA);
            var error = await scope.CaptureErrorAsync(sql);
            error.SqlState.ShouldBe(
                PostgresErrorCodes.InsufficientPrivilege,
                $"'{sql}' must be refused for evidgate_app ({error.MessageText})");
        }

        // Reading the journal is the one thing the app role may do with it (01.1.2-T1.3 readiness check).
        // The head it reads must be the real history, whatever else the suite adds later.
        await using (var scope = await TenantScope.BeginAsync(app, OrgA))
        {
            (await scope.ScalarAsync<string>(
                "select coalesce(string_agg(name, ',' order by name), '') from public.schema_migration"))
                .ShouldContain("0001_foundation_roles.sql");
        }
    }

    /// <summary>01.4.1-T2.2: the operator path reads (audited, T76) and cannot write.</summary>
    [Fact]
    public async Task Operator_Role_Cannot_Write_Business_Rows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var app = _fixture.AppConnectionString(database);
        var operatorConnection = _fixture.OperatorConnectionString(database);
        await SeedAsync(app);

        await using (var scope = await TenantScope.BeginAsync(operatorConnection, OrgA))
        {
            (await scope.ScalarAsync<int>("select count(*)::int from public.rls_probe")).ShouldBe(2);
        }

        await using (var scope = await TenantScope.BeginAsync(operatorConnection))
        {
            (await scope.ScalarAsync<int>("select count(*)::int from public.rls_probe")).ShouldBe(0);
        }

        var attempts = new[]
        {
            "insert into public.rls_probe (org_id, id, label) values ('11111111-1111-7111-8111-111111111111', 'cccccccc-0000-7000-8000-000000000001', 'x')",
            "update public.rls_probe set label = 'x'",
            "delete from public.rls_probe",
            "create table public.operator_escalation (id int)",
            "alter table public.rls_probe drop column label",
            "insert into public.schema_migration (name, checksum) values ('9999_fake.sql', 'x')",
        };

        foreach (var sql in attempts)
        {
            await using var scope = await TenantScope.BeginAsync(operatorConnection, OrgA);
            (await scope.CaptureErrorAsync(sql))
                .SqlState.ShouldBe(
                    PostgresErrorCodes.InsufficientPrivilege,
                    $"'{sql}' must be refused for evidgate_operator");
        }
    }

    /// <summary>01.4.1-T2.1 and T80: role attributes and table privileges match the least-privilege contract.</summary>
    [Fact]
    public async Task Role_Attributes_Match_Least_Privilege()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.ColumnAsync<string>(
            database,
            "select rolname from pg_roles where rolname like 'evidgate\\_%' order by rolname"))
            .ShouldBe(["evidgate_app", "evidgate_operator", "evidgate_owner", "evidgate_worker", "evidgate_worker_sched"]);

        // No role may bypass the row-security system (DECISIONS F5, 00 §5.4.3).
        (await _fixture.ColumnAsync<string>(
            database,
            "select rolname from pg_roles where rolname like 'evidgate\\_%' and (rolsuper or rolbypassrls or rolcreatedb)"))
            .ShouldBeEmpty();

        // Only the migration role may create roles; the application roles never inherit memberships.
        (await _fixture.ColumnAsync<string>(
            database,
            "select rolname from pg_roles where rolname like 'evidgate\\_%' and rolcreaterole order by rolname"))
            .ShouldBe(["evidgate_owner"]);
        (await _fixture.ScalarAsync<bool>(
            database,
            "select bool_and(not rolinherit) from pg_roles where rolname in ('evidgate_app', 'evidgate_operator', 'evidgate_worker', 'evidgate_worker_sched')"))
            .ShouldBeTrue();

        // The template table is owned by the migration role and forced.
        (await _fixture.ScalarAsync<string>(
            database,
            "select pg_get_userbyid(relowner) from pg_class where oid = 'public.rls_probe'::regclass"))
            .ShouldBe("evidgate_owner");
        (await _fixture.ScalarAsync<bool>(
            database,
            "select relrowsecurity and relforcerowsecurity from pg_class where oid = 'public.rls_probe'::regclass"))
            .ShouldBeTrue();

        // Table privileges: app = DML without TRUNCATE/REFERENCES, operator = SELECT, sweep role = nothing.
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select p as privilege from unnest(array['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES']) as p
             where has_table_privilege('evidgate_app', 'public.rls_probe', p)
             order by 1
            """))
            .ShouldBe(["DELETE", "INSERT", "SELECT", "UPDATE"]);
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select p as privilege from unnest(array['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE']) as p
             where has_table_privilege('evidgate_operator', 'public.rls_probe', p)
             order by 1
            """))
            .ShouldBe(["SELECT"]);
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select p as privilege from unnest(array['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE']) as p
             where has_table_privilege('evidgate_worker_sched', 'public.rls_probe', p)
            """))
            .ShouldBeEmpty();

        // Schema privileges: USAGE where needed, CREATE nowhere (00 §5.4.8, TL T1).
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select s || ':' || p as entry
              from unnest(array['public', 'evidgate', 'evidgate_resolve']) as s,
                   unnest(array['USAGE', 'CREATE']) as p
             where has_schema_privilege('evidgate_app', s, p)
            """))
            .ShouldBe(["evidgate:USAGE", "evidgate_resolve:USAGE", "public:USAGE"], ignoreOrder: true);

        // The sweep role exists for EXECUTE-only SECURITY DEFINER calls (T80): no table privileges,
        // and not even the RLS helpers — a function must be granted to it explicitly.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('evidgate_worker_sched', 'evidgate.current_org_id()', 'EXECUTE')"))
            .ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('evidgate_app', 'evidgate.current_org_id()', 'EXECUTE')"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('evidgate_operator', 'evidgate.current_project_id()', 'EXECUTE')"))
            .ShouldBeTrue();
    }

    /// <summary>
    /// 01.4.1-T3.2 and TL T1: the <c>evidgate_resolve</c> grant pattern is
    /// proven on a freshly created function, and a new function in
    /// <c>evidgate</c> stays private — a per-schema default REVOKE could not do
    /// this, because default privileges are additive to the built-in PUBLIC
    /// EXECUTE and only the schema-less form subtracts from it.
    /// </summary>
    [Fact]
    public async Task Schema_And_Function_Grants_Are_Minimal()
    {
        var database = await _fixture.CreateDatabaseAsync();

        // The resolver schema is scripted but empty (functions arrive with 02.6.1/03.4.1).
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_class where relnamespace = 'evidgate_resolve'::regnamespace"))
            .ShouldBe(0);

        await using (var owner = new NpgsqlConnection(_fixture.OwnerConnectionString(database)))
        {
            await owner.OpenAsync(TestContext.Current.CancellationToken);
            await PostgresIsolationFixture.ExecuteAsync(owner, "create function evidgate.acl_probe() returns int language sql as $$ select 1 $$");
            await PostgresIsolationFixture.ExecuteAsync(owner, "create function evidgate_resolve.acl_probe() returns int language sql as $$ select 1 $$");
        }

        // A new helper in `evidgate` is owner-only: nobody gets EXECUTE implicitly.
        foreach (var role in new[] { "evidgate_app", "evidgate_operator", "evidgate_worker_sched" })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_function_privilege('{role}', 'evidgate.acl_probe()', 'EXECUTE')"))
                .ShouldBeFalse($"'{role}' must not execute a new evidgate function implicitly (TL T80)");
        }

        // A new resolver in `evidgate_resolve` is executable by the app role and by nobody else.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_function_privilege('evidgate_app', 'evidgate_resolve.acl_probe()', 'EXECUTE')"))
            .ShouldBeTrue();
        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_function_privilege('{role}', 'evidgate_resolve.acl_probe()', 'EXECUTE')"))
                .ShouldBeFalse($"'{role}' must not resolve tenants (TL T1)");
        }

        // PUBLIC holds EXECUTE on neither — the built-in grant was revoked for the migration role.
        (await _fixture.ScalarAsync<long>(
            database,
            """
            select count(*)
              from pg_proc p
              cross join lateral aclexplode(p.proacl) a
             where p.proname = 'acl_probe' and a.grantee = 0
            """))
            .ShouldBe(0);
    }

    private static async Task SeedAsync(string appConnectionString)
    {
        await using (var scope = await TenantScope.BeginAsync(appConnectionString, OrgA))
        {
            (await scope.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'A-1'), (@org, @id2, 'A-2')",
                ("org", OrgA),
                ("id", RowA1),
                ("id2", RowA2)))
                .ShouldBe(2);

            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(appConnectionString, OrgB))
        {
            (await scope.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, 'B-1')",
                ("org", OrgB),
                ("id", RowB1)))
                .ShouldBe(1);

            await scope.CommitAsync();
        }
    }
}
