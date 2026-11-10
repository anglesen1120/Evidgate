using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The role reference of <c>membership_role</c> from the runtime role: inside its own org scope the app role
/// cannot grant another org's custom role, a role that does not exist or a staff role, accepts a built-in member
/// role and the org's own custom member role, and cannot turn a granted role into a violating one afterwards.
/// </summary>
/// <remarks>
/// The guard reads the role through RLS, so another org's role is invisible here and the refusal is the same
/// check the API path meets; each refused statement gets its own scope, because a refusal aborts the
/// transaction the scope opened.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MembershipRoleKeyAppRoleTests : CatalogueTestBase
{
    private const string InsertGrant =
        "insert into public.membership_role (id, org_id, membership_id, role_id) values (@id, @org, @membership, @role)";

    public MembershipRoleKeyAppRoleTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Another_Orgs_Custom_Role_Is_Refused_From_The_App_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membership = await SeedMembershipAsync(org);
        var foreignRole = await SeedCustomRoleAsync(await Fixture.SeedOrgAsync(cancellationToken: Token), "member");

        var refusal = await AppRefusalAsync(org, InsertGrant, Grant(org, membership, foreignRole));

        refusal.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.ShouldBe("membership_role_role_guard");
    }

    [Fact]
    public async Task A_Missing_Role_Is_Refused_From_The_App_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membership = await SeedMembershipAsync(org);

        var refusal = await AppRefusalAsync(org, InsertGrant, Grant(org, membership, Guid.CreateVersion7()));

        refusal.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.ShouldBe("membership_role_role_guard");
    }

    [Fact]
    public async Task A_Staff_Role_Is_Refused_From_The_App_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membership = await SeedMembershipAsync(org);
        var staffRole = await BuiltInRoleAsync("platform_operator");

        var refusal = await AppRefusalAsync(org, InsertGrant, Grant(org, membership, staffRole));

        refusal.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.ShouldBe("membership_role_role_guard");
    }

    [Fact]
    public async Task A_Builtin_Member_Role_And_The_Orgs_Own_Custom_Role_Are_Accepted_From_The_App_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membership = await SeedMembershipAsync(org);
        var builtIn = await BuiltInRoleAsync("viewer");
        var custom = await SeedCustomRoleAsync(org, "member");

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);

        await InsertAsync(scope, Grant(org, membership, builtIn));
        await InsertAsync(scope, Grant(org, membership, custom));

        (await scope.Db.ScalarAsync<long>(
            "select count(*) from public.membership_role where membership_id = @membership",
            new { membership },
            Token)).ShouldBe(2, "the app role may grant a built-in member role and its own org's custom role");
    }

    [Fact]
    public async Task A_Granted_Role_Cannot_Change_Kind_From_The_App_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membership = await SeedMembershipAsync(org);
        var granted = await SeedCustomRoleAsync(org, "member");
        var ungranted = await SeedCustomRoleAsync(org, "member");
        await Fixture.SeedAsync(InsertGrant, Grant(org, membership, granted), Token);

        var refusal = await AppRefusalAsync(
            org,
            "update public.role set kind = 'staff' where id = @role",
            ("role", granted));

        refusal.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.ShouldBe("role_granted_scope_guard");

        (await AppExecuteAsync(
            org,
            "update public.role set kind = 'service' where id = @role",
            ("role", ungranted))).ShouldBe(1, "a role no membership holds may still change kind");
    }

    private static (string Name, object Value)[] Grant(Guid org, Guid membership, Guid role) =>
        [("id", Guid.CreateVersion7()), ("org", org), ("membership", membership), ("role", role)];

    /// <summary>One refusal on a fresh scope: an aborted transaction may not be reused.</summary>
    private async Task<PostgresException> AppRefusalAsync(
        Guid org,
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        await using var command = Command(scope, sql, values);

        return await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
    }

    /// <summary>One statement on a fresh scope, returning the rows it changed.</summary>
    private async Task<int> AppExecuteAsync(Guid org, string sql, params (string Name, object Value)[] values)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        await using var command = Command(scope, sql, values);

        return await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task InsertAsync(TenantTestScope scope, (string Name, object Value)[] values)
    {
        await using var command = Command(scope, InsertGrant, values);

        (await command.ExecuteNonQueryAsync(Token)).ShouldBe(1, "the app role may grant this role");
    }

    private static NpgsqlCommand Command(
        TenantTestScope scope,
        string sql,
        (string Name, object Value)[] values)
    {
        var command = scope.Command(sql);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private async Task<Guid> SeedMembershipAsync(Guid org)
    {
        var membership = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org, @email, 'pending')",
            [("id", membership), ("org", org), ("email", $"m-{membership:N}@example.test")],
            Token);
        return membership;
    }

    private async Task<Guid> SeedCustomRoleAsync(Guid org, string kind)
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org, @code, 'Custom', false, @kind)",
            [("id", id), ("org", org), ("code", "c_" + id.ToString("N")), ("kind", kind)],
            Token);
        return id;
    }

    private async Task<Guid> BuiltInRoleAsync(string code)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select id from public.role where org_id is null and code = @code";
        command.Parameters.AddWithValue("code", code);

        return (Guid)(await command.ExecuteScalarAsync(Token))!;
    }
}
