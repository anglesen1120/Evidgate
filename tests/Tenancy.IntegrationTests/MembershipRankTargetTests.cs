using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The membership-keyed half of the rank rule introduced with <c>membership_rank</c>: the rank of a target is
/// read from its membership row, whatever the row's status, so a removed owner's dead row still ranks two and a
/// pending invite's carried role still ranks the invite. The function is invoker-rights, so another org's
/// membership id ranks zero under this org's scope.
/// </summary>
/// <remarks>
/// Rows are seeded as the superuser in a dedicated database and the queries run on a real <c>evidgate_app</c>
/// login with the same <c>evidgate.org_id</c> setting a request scope carries. A missing grant would fail with
/// 42501, and a policy that leaked the other org would fail on the value instead.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MembershipRankTargetTests
{
    private readonly PostgresTenancyFixture _fixture;

    public MembershipRankTargetTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// One org with every target status: an active owner ranks two, a removed owner's row still ranks two, a
    /// pending owner invite ranks two without a user, a custom <c>membership:write</c> member ranks one (active
    /// and pending alike), and a plain member ranks zero. The user-keyed function keeps its active-only answer.
    /// </summary>
    [Fact]
    public async Task Membership_Rank_Reads_The_Target_Row_Whatever_Its_Status()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var world = await SeedRankWorldAsync(database);
        var app = _fixture.AppConnectionString(database);

        (await RankAsync(app, world.Org, world.Org, world.OwnerMembership)).ShouldBe(2, "an active owner");
        (await RankAsync(app, world.Org, world.Org, world.RemovedOwnerMembership)).ShouldBe(
            2, "a removed membership keeps the grant its row carries");
        (await RankAsync(app, world.Org, world.Org, world.PendingOwnerMembership)).ShouldBe(
            2, "a pending invite carries the grant its row carries");
        (await RankAsync(app, world.Org, world.Org, world.CustomMembership)).ShouldBe(
            1, "`membership:write` through a custom role");
        (await RankAsync(app, world.Org, world.Org, world.PendingCustomMembership)).ShouldBe(
            1, "the pending invite ranks from the membership id, not from a user");
        (await RankAsync(app, world.Org, world.Org, world.PlainMembership)).ShouldBe(
            0, "a member without an administrator role");

        (await SessionAdminRankAsync(app, world.Org, world.RemovedOwnerUser)).ShouldBe(
            0, "the user-keyed function keeps its active-membership-only answer");
    }

    /// <summary>
    /// Another org's membership id ranks zero under this org's scope, although the same id ranks two when the
    /// scope covers its own org. The foreign owner's grant is asserted first, so the zero is RLS and not a
    /// missing row.
    /// </summary>
    [Fact]
    public async Task Another_Orgs_Membership_Id_Ranks_Zero_Under_This_Orgs_Scope()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (orgA, _, _) = await SeedTenantAdminAsync(database, "rank-scope-a");
        var (orgB, ownerB, membershipB) = await SeedTenantAdminAsync(database, "rank-scope-b");
        var app = _fixture.AppConnectionString(database);

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.membership_role where org_id = '{orgB}' "
            + $"and membership_id = '{membershipB}'")).ShouldBe(1L, "the other org's owner grant is real");

        (await RankAsync(app, orgA, orgA, membershipB)).ShouldBe(
            0, "the rank is the scoped org's grants");
        (await RankAsync(app, orgA, orgB, membershipB)).ShouldBe(
            0, "naming org B does not reveal its rows to org A's scope");

        (await RankAsync(app, orgB, orgB, membershipB)).ShouldBe(
            2, "the same id ranks two once the scope covers its own org");
        (await SessionAdminRankAsync(app, orgB, ownerB)).ShouldBe(
            2, "the positive control for the same rows through the user-keyed function");
    }

    /// <summary>Every membership of the status matrix, with its grants inserted afterwards.</summary>
    private async Task<RankWorld> SeedRankWorldAsync(DedicatedDatabase database)
    {
        var world = new RankWorld(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());

        await SeedRankRowsAsync(database, world);
        await SeedRankGrantsAsync(database, world);

        return world;
    }

    /// <summary>The org, users, memberships and custom role of <see cref="SeedRankWorldAsync"/>.</summary>
    private async Task SeedRankRowsAsync(DedicatedDatabase database, RankWorld world)
    {
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug)
            values ('{world.Org}', 'rank target org', 'rank-target-{world.Org:N}');

            insert into public."user" (id, email) values
              ('{world.OwnerUser}', 'owner-{world.OwnerUser:N}@example.test'),
              ('{world.RemovedOwnerUser}', 'removed-owner-{world.RemovedOwnerUser:N}@example.test'),
              ('{world.CustomUser}', 'custom-{world.CustomUser:N}@example.test'),
              ('{world.PlainUser}', 'plain-{world.PlainUser:N}@example.test');

            insert into public.membership (id, org_id, user_id, invited_email, status) values
              ('{world.OwnerMembership}', '{world.Org}', '{world.OwnerUser}', 'owner-{world.OwnerUser:N}@example.test', 'active'),
              ('{world.RemovedOwnerMembership}', '{world.Org}', '{world.RemovedOwnerUser}', 'removed-{world.RemovedOwnerUser:N}@example.test', 'removed'),
              ('{world.CustomMembership}', '{world.Org}', '{world.CustomUser}', 'custom-{world.CustomUser:N}@example.test', 'active'),
              ('{world.PlainMembership}', '{world.Org}', '{world.PlainUser}', 'plain-{world.PlainUser:N}@example.test', 'active'),
              ('{world.PendingOwnerMembership}', '{world.Org}', null, 'pending-owner-{world.PendingOwnerMembership:N}@example.test', 'pending'),
              ('{world.PendingCustomMembership}', '{world.Org}', null, 'pending-custom-{world.PendingCustomMembership:N}@example.test', 'pending');

            insert into public.role (id, org_id, code, name, is_builtin, kind)
            values ('{world.CustomRole}', '{world.Org}', 'rank_admin_{world.CustomRole:N}', 'Member administrator', false, 'member');

            insert into public.role_permission (id, org_id, role_id, permission_code)
            values (gen_random_uuid(), '{world.Org}', '{world.CustomRole}', 'membership:write');
            """);
    }

    /// <summary>The grants of <see cref="SeedRankWorldAsync"/>: built-in owners and custom administrators.</summary>
    private async Task SeedRankGrantsAsync(DedicatedDatabase database, RankWorld world)
    {
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{world.Org}', '{world.OwnerMembership}', r.id
              from public.role r where r.org_id is null and r.code = 'tenant_admin';
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{world.Org}', '{world.RemovedOwnerMembership}', r.id
              from public.role r where r.org_id is null and r.code = 'tenant_admin';
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{world.Org}', '{world.PendingOwnerMembership}', r.id
              from public.role r where r.org_id is null and r.code = 'tenant_admin';
            insert into public.membership_role (id, org_id, membership_id, role_id)
            values
              (gen_random_uuid(), '{world.Org}', '{world.CustomMembership}', '{world.CustomRole}'),
              (gen_random_uuid(), '{world.Org}', '{world.PendingCustomMembership}', '{world.CustomRole}');
            """);
    }

    /// <summary>One org whose owner holds the built-in <c>tenant_admin</c>: rank two inside it.</summary>
    private async Task<(Guid Org, Guid User, Guid Membership)> SeedTenantAdminAsync(
        DedicatedDatabase database,
        string stem)
    {
        var org = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();
        var email = $"{stem}-owner-{user:N}@example.test";

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug)
            values ('{org}', '{stem} org', '{stem}-{org:N}');
            insert into public."user" (id, email) values ('{user}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership}', '{org}', '{user}', '{email}', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{org}', '{membership}', r.id
              from public.role r where r.org_id is null and r.code = 'tenant_admin';
            """);

        return (org, user, membership);
    }

    /// <summary>The membership-keyed rank, read as the app role with the request's tenant setting.</summary>
    private static Task<int> RankAsync(
        string connectionString,
        Guid scope,
        Guid org,
        Guid membershipId) =>
        ScalarAsAppAsync<int>(
            connectionString,
            scope,
            "select evidgate.membership_rank(@org_id, @membership_id)",
            ("org_id", org),
            ("membership_id", membershipId));

    /// <summary>The user-keyed rank, which keeps its active-membership-only answer.</summary>
    private static Task<int> SessionAdminRankAsync(
        string connectionString,
        Guid scope,
        Guid userId) =>
        ScalarAsAppAsync<int>(
            connectionString,
            scope,
            "select evidgate.session_admin_rank(@org_id, @user_id)",
            ("org_id", scope),
            ("user_id", userId));

    /// <summary>One parameterised query as <c>evidgate_app</c>, with the request's tenant setting.</summary>
    private static async Task<T> ScalarAsAppAsync<T>(
        string connectionString,
        Guid scope,
        string sql,
        params (string Name, object Value)[] values)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);

        await using (var setScope = connection.CreateCommand())
        {
            setScope.CommandText = "select set_config('evidgate.org_id', @scope, false)";
            setScope.Parameters.AddWithValue("scope", scope.ToString("D"));
            await setScope.ExecuteNonQueryAsync(token);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalar = await command.ExecuteScalarAsync(token);
        return scalar is null or DBNull ? default! : (T)scalar;
    }

    private sealed record RankWorld(
        Guid Org,
        Guid OwnerUser,
        Guid RemovedOwnerUser,
        Guid CustomUser,
        Guid PlainUser,
        Guid OwnerMembership,
        Guid RemovedOwnerMembership,
        Guid CustomMembership,
        Guid PlainMembership,
        Guid PendingOwnerMembership,
        Guid PendingCustomMembership,
        Guid CustomRole);
}
