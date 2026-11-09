using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The member rank grant of <c>0093</c>: <c>evidgate_app</c> may ask <c>evidgate.session_admin_rank</c> and
/// <c>evidgate.session_admin_holds_write</c>, the one source the member commands compare actor and target with.
/// Both run invoker-rights, so the answer is the REQUEST's org: reading another org's administrator from inside
/// this one yields 0 (or false), and a call with no tenant setting at all yields 0 rather than an error or a
/// cross-tenant answer.
/// </summary>
/// <remarks>
/// The app connections are real logins (the fixture enables <c>evidgate_app</c>'s credential on the template) and
/// set the same <c>evidgate.org_id</c> setting the request scope sets, so a missing grant fails here with 42501 and
/// a policy that leaks the other org fails on the value instead.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MemberRankGrantTests
{
    private readonly PostgresTenancyFixture _fixture;

    public MemberRankGrantTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_Rank_Is_The_Scoped_Orgs_And_Another_Orgs_Owner_Ranks_Zero()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (orgA, ownerA) = await SeedTenantAdminAsync(database, "rank-a");
        var (orgB, ownerB) = await SeedTenantAdminAsync(database, "rank-b");
        var customA = await SeedCustomAdminAsync(database, orgA, "rank-a");
        var plainA = await SeedPlainMemberAsync(database, orgA, "rank-a");
        var app = _fixture.AppConnectionString(database);

        // The foreign owner's grant is real: the zeros below are RLS, not a missing row.
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.membership_role mr "
            + "join public.membership m on m.id = mr.membership_id "
            + "join public.role r on r.id = mr.role_id "
            + $"where m.org_id = '{orgB}' and m.user_id = '{ownerB}' and m.status = 'active' "
            + "and r.org_id is null and r.code = 'tenant_admin'"))
            .ShouldBe(1L, "the other org's owner holds the built-in role there");

        // Inside org A: the org's owner ranks 2, its custom administrator 1, a plain member 0.
        (await RankAsync(app, orgA, orgA, ownerA)).ShouldBe(2, "the built-in `tenant_admin` of the scoped org");
        (await RankAsync(app, orgA, orgA, customA)).ShouldBe(1, "`membership:write` through a custom role");
        (await HoldsWriteAsync(app, orgA, customA, orgA)).ShouldBeTrue("the probe the rank delegates to");
        (await RankAsync(app, orgA, orgA, plainA)).ShouldBe(0, "a member without an administrator role");

        // Another org's owner is nobody here, whether the question is scoped to org A's row or names org B.
        (await RankAsync(app, orgA, orgA, ownerB)).ShouldBe(0, "the rank is the ACTING org's grants");
        (await HoldsWriteAsync(app, orgA, ownerB, orgA)).ShouldBeFalse("a foreign administrator holds no write here");
        (await RankAsync(app, orgA, orgB, ownerB)).ShouldBe(
            0,
            "the parameters name org B, but the policies hide its rows from org A's scope");
        (await HoldsWriteAsync(app, orgA, ownerB, orgB)).ShouldBeFalse("the same rows, hidden the same way");

        // The control: the identical calls answer from the rows once the scope covers the org they name.
        (await RankAsync(app, orgB, orgB, ownerB)).ShouldBe(2, "the same rows are visible to their own tenant");
        (await HoldsWriteAsync(app, orgB, ownerB, orgB)).ShouldBeTrue("the owner's built-in role carries the write");
    }

    [Fact]
    public async Task Without_A_Tenant_Scope_Every_Rank_Is_Zero()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, owner) = await SeedTenantAdminAsync(database, "rank-none");
        var app = _fixture.AppConnectionString(database);

        // `evidgate.current_org_id()` is NULL-safe: no setting means zero rows, which is rank 0 — never an error
        // and never a cross-tenant answer, so the member commands fail closed rather than open.
        (await RankAsync(app, scope: null, org, owner)).ShouldBe(0, "no tenant setting, no visible membership");
        (await HoldsWriteAsync(app, scope: null, owner, org)).ShouldBeFalse("no tenant setting, no visible grant");
    }

    /// <summary>A new org whose owner holds the built-in <c>tenant_admin</c>: rank 2 inside it.</summary>
    private async Task<(Guid Org, Guid User)> SeedTenantAdminAsync(DedicatedDatabase database, string stem)
    {
        var org = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var email = $"{stem}-owner@example.test";

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org}', '{stem} org', '{Unique.Slug(stem)}');
            insert into public."user" (id, email) values ('{user}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{org}', '{user}', '{email}', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{org}', m.id, r.id
              from public.membership m
              join public.role r on r.org_id is null and r.code = 'tenant_admin'
             where m.org_id = '{org}' and m.user_id = '{user}';
            """);

        return (org, user);
    }

    /// <summary>A member of an existing org whose custom role carries <c>membership:write</c>: rank 1.</summary>
    private async Task<Guid> SeedCustomAdminAsync(DedicatedDatabase database, Guid orgId, string stem)
    {
        var user = Guid.CreateVersion7();
        var role = Guid.CreateVersion7();
        var email = $"{stem}-custom@example.test";

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{user}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{orgId}', '{user}', '{email}', 'active');
            insert into public.role (id, org_id, code, name, is_builtin, kind)
            values ('{role}', '{orgId}', 'member_admin_{role:N}', 'Member administrator', false, 'member');
            insert into public.role_permission (id, org_id, role_id, permission_code)
            values (gen_random_uuid(), '{orgId}', '{role}', 'membership:write');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{orgId}', m.id, '{role}'
              from public.membership m
             where m.org_id = '{orgId}' and m.user_id = '{user}';
            """);

        return user;
    }

    /// <summary>A member of an existing org with no role at all: rank 0.</summary>
    private async Task<Guid> SeedPlainMemberAsync(DedicatedDatabase database, Guid orgId, string stem)
    {
        var user = Guid.CreateVersion7();
        var email = $"{stem}-member@example.test";

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{user}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{orgId}', '{user}', '{email}', 'active');
            """);

        return user;
    }

    private static async Task<int> RankAsync(string connectionString, Guid? scope, Guid org, Guid user) =>
        await ScalarAsAppAsync<int>(
            connectionString,
            scope,
            $"select evidgate.session_admin_rank('{org}', '{user}')");

    /// <summary>
    /// The write probe takes the ACTOR first, unlike <c>session_admin_rank</c>, which takes the org first.
    /// </summary>
    private static async Task<bool> HoldsWriteAsync(string connectionString, Guid? scope, Guid actor, Guid org) =>
        await ScalarAsAppAsync<bool>(
            connectionString,
            scope,
            $"select evidgate.session_admin_holds_write('{actor}', '{org}')");

    /// <summary>One query as <c>evidgate_app</c>, with the request's tenant setting when a scope is given.</summary>
    private static async Task<T> ScalarAsAppAsync<T>(string connectionString, Guid? scope, string sql)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);

        if (scope is { } org)
        {
            await using var setScope = connection.CreateCommand();
            setScope.CommandText = "select set_config('evidgate.org_id', @org, false)";
            setScope.Parameters.AddWithValue("org", org.ToString());
            await setScope.ExecuteNonQueryAsync(token);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(token);

        return value is null or DBNull ? default! : (T)value;
    }
}
