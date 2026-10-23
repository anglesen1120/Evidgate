using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.6.1-T3 (migration 0014): the pre-tenant resolvers of <c>evidgate_resolve</c>.
/// The app role sees one org at a time under RLS, so "which orgs is this user a member
/// of" is answered by owner-owned <c>SECURITY DEFINER</c> functions that return only
/// the T1 tuple, take parameters only and cannot be redirected by the caller's
/// <c>search_path</c> (TL T80).
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ResolverTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ResolverTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MemberOrgs_ReturnsOnlyTheUsersActiveMemberships_AsTheContractTuple()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var user = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var orgA = await SeedOrgAsync(database, "alpha", "active");
        var orgB = await SeedOrgAsync(database, "bravo", "suspended");
        var orgGone = await SeedOrgAsync(database, "gone", "deleted");
        var orgRemoved = await SeedOrgAsync(database, "removed", "active");
        var orgPending = await SeedOrgAsync(database, "pending", "active");
        var orgOther = await SeedOrgAsync(database, "other", "active");

        await SeedMembershipAsync(database, orgA, user, "active");
        await SeedMembershipAsync(database, orgB, user, "active");
        await SeedMembershipAsync(database, orgGone, user, "active");
        await SeedMembershipAsync(database, orgRemoved, user, "removed");
        await SeedMembershipAsync(database, orgPending, user, "pending");
        await SeedMembershipAsync(database, orgOther, other, "active");

        var rows = await QueryAppAsync(
            database,
            "select org_id, project_id, binding_id, status from evidgate_resolve.member_orgs(@u)",
            ("u", user));

        rows.Select(row => (row[0], row[3])).OrderBy(row => (Guid)row.Item1!).ToList().ShouldBe(
            new[] { (orgA, "active"), (orgB, "suspended") }
                .OrderBy(row => row.Item1)
                .Select(row => ((object?)row.Item1, (object?)row.Item2))
                .ToList());
        rows.ShouldAllBe(row => row[1] == null && row[2] == null);
    }

    [Fact]
    public async Task MemberOrg_PointLookup_IsNotTruncatedByTheListLimit()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var user = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        // 205 memberships: the list function caps at 200, the point lookup must not.
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug)
              select gen_random_uuid(), 'o' || i, 'org-' || i from generate_series(1, 205) i;
            insert into public.membership (id, org_id, user_id, invited_email, status)
              select gen_random_uuid(), o.id, '{user}', 'u@example.test', 'active' from public.organization o;
            """);

        var orgs = await _fixture.ColumnAsync<Guid>(database, "select id from public.organization order by id");
        (await QueryAppAsync(database, "select * from evidgate_resolve.member_orgs(@u)", ("u", user))).Count.ShouldBe(200);

        foreach (var org in new[] { orgs[0], orgs[204] })
        {
            (await QueryAppAsync(database, "select org_id, status from evidgate_resolve.member_org(@u, @o)", ("u", user), ("o", org)))
                .ShouldHaveSingleItem()[0].ShouldBe(org);
        }

        (await QueryAppAsync(database, "select * from evidgate_resolve.member_org(@u, @o)", ("u", other), ("o", orgs[0])))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task MemberOrgs_UnknownUser_ReturnsNothing()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var org = await SeedOrgAsync(database, "alpha", "active");
        await SeedMembershipAsync(database, org, Guid.CreateVersion7(), "active");

        (await QueryAppAsync(database, "select * from evidgate_resolve.member_orgs(@u)", ("u", Guid.CreateVersion7())))
            .ShouldBeEmpty();
        (await QueryAppAsync(database, "select * from evidgate_resolve.member_orgs(null)")).ShouldBeEmpty();
    }

    [Fact]
    public async Task MemberOrganizations_ReturnsSlugAndName_BoundedByTheLimit()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var user = Guid.CreateVersion7();
        for (var i = 0; i < 5; i++)
        {
            var org = await SeedOrgAsync(database, $"org-{i}", "active");
            await SeedMembershipAsync(database, org, user, "active");
        }

        var two = await QueryAppAsync(
            database, "select org_id, slug, name, status from evidgate_resolve.member_organizations(@u, 2)", ("u", user));
        two.Count.ShouldBe(2);
        two[0][1].ShouldBe("org-0");

        // A non-positive or null limit is clamped, never unbounded and never an error.
        (await QueryAppAsync(database, "select * from evidgate_resolve.member_organizations(@u, 0)", ("u", user)))
            .Count.ShouldBe(1);
        (await QueryAppAsync(database, "select * from evidgate_resolve.member_organizations(@u, null)", ("u", user)))
            .Count.ShouldBe(5);
        (await QueryAppAsync(database, "select * from evidgate_resolve.member_organizations(@u, 100000)", ("u", user)))
            .Count.ShouldBe(5);
    }

    [Fact]
    public async Task Resolver_Immune_ToSearchPathHijack()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var user = Guid.CreateVersion7();
        var real = await SeedOrgAsync(database, "real", "active");
        await SeedMembershipAsync(database, real, user, "active");

        // An attacker-shaped schema that shadows both tables with a forged membership.
        var forged = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            create schema evil;
            create table evil.organization (id uuid, status text);
            create table evil.membership (org_id uuid, user_id uuid, status text);
            insert into evil.organization values ('{forged}', 'active');
            insert into evil.membership values ('{forged}', '{user}', 'active');
            grant usage on schema evil to evidgate_app;
            grant select on evil.organization, evil.membership to evidgate_app;
            """);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await Exec(connection, "set search_path = evil, public");

        await using var command = new NpgsqlCommand("select org_id from evidgate_resolve.member_orgs(@u)", connection);
        command.Parameters.AddWithValue("u", user);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var seen = new List<Guid>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            seen.Add(reader.GetGuid(0));
        }

        seen.ShouldBe([real]);
    }

    [Fact]
    public async Task EveryResolver_IsOwnerOwnedDefiner_WithEmptySearchPath_ExecutableOnlyByTheAppRole()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var functions = await _fixture.ColumnAsync<string>(
            database,
            """
            select p.proname || '|' || r.rolname || '|' || p.prosecdef || '|' || coalesce(array_to_string(p.proconfig, ','), '')
              from pg_proc p
              join pg_namespace n on n.oid = p.pronamespace
              join pg_roles r on r.oid = p.proowner
             where n.nspname = 'evidgate_resolve'
             order by p.proname
            """);

        functions.ShouldNotBeEmpty();
        foreach (var function in functions)
        {
            var parts = function.Split('|');
            parts[1].ShouldBe("evidgate_owner", $"{parts[0]} must be owner-owned");
            parts[2].ShouldBe("true", $"{parts[0]} must be SECURITY DEFINER");
            parts[3].ShouldBe("search_path=\"\"", $"{parts[0]} must pin an empty search_path");
        }

        foreach (var signature in new[]
                 {
                     "evidgate_resolve.member_orgs(uuid)",
                     "evidgate_resolve.member_org(uuid, uuid)",
                     "evidgate_resolve.member_organizations(uuid, integer)",
                     "evidgate_resolve.resolve_ci_binding(text, bigint, bytea[])",
                     "evidgate_resolve.resolve_ingest_token(text)",
                 })
        {
            (await _fixture.ScalarAsync<bool>(database, $"select has_function_privilege('evidgate_app', '{signature}', 'execute')"))
                .ShouldBeTrue(signature);
            foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched", "public" })
            {
                var sql = role == "public"
                    ? $"select exists (select 1 from pg_proc p, aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a where p.oid = '{signature}'::regprocedure and a.grantee = 0)"
                    : $"select has_function_privilege('{role}', '{signature}', 'execute')";
                (await _fixture.ScalarAsync<bool>(database, sql)).ShouldBeFalse($"{role} must not execute {signature}");
            }
        }
    }

    [Fact]
    public async Task AppRole_CannotReadMembershipsOfOtherOrgs_Directly()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var user = Guid.CreateVersion7();
        var org = await SeedOrgAsync(database, "alpha", "active");
        await SeedMembershipAsync(database, org, user, "active");

        // The resolver sees the membership; the same app connection reading the table
        // with no tenant scope sees nothing — the function is the only way across.
        (await QueryAppAsync(database, "select * from evidgate_resolve.member_orgs(@u)", ("u", user))).Count.ShouldBe(1);
        (await QueryAppAsync(database, "select * from public.membership")).ShouldBeEmpty();
    }

    private async Task<Guid> SeedOrgAsync(string database, string slug, string status)
    {
        var id = Guid.CreateVersion7();
        await ExecOwnerAsync(
            database,
            "insert into public.organization (id, name, slug, status) values (@id, @name, @slug, @status)",
            ("id", id),
            ("name", slug),
            ("slug", slug.Length < 2 ? slug + slug : slug),
            ("status", status));

        return id;
    }

    private Task SeedMembershipAsync(string database, Guid org, Guid user, string status) =>
        ExecOwnerAsync(
            database,
            "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org, @user, @email, @status)",
            ("id", Guid.CreateVersion7()),
            ("org", org),
            ("user", user),
            ("email", $"u{user:N}@example.test"),
            ("status", status));

    private async Task ExecOwnerAsync(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<object?[]>> QueryAppAsync(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, TestContext.Current.CancellationToken) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task Exec(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
