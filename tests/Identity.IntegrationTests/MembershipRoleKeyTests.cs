using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The role reference of <c>membership_role</c> (0097): the database itself refuses a grant of a staff or service
/// role, of another org's custom role and of a role that does not exist; a built-in member role and the org's own
/// custom role still go through; and a granted role cannot be turned into a violating one afterwards.
/// </summary>
[Trait("Category", "Docker")]
public sealed class MembershipRoleKeyTests : CatalogueTestBase
{
    private const string InsertGrant =
        "insert into public.membership_role (id, org_id, membership_id, role_id) values (@id, @org, @membership, @role)";

    public MembershipRoleKeyTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    [Theory]
    [InlineData("platform_operator")]
    [InlineData("billing_operator")]
    [InlineData("ci_producer")]
    public async Task A_Staff_Or_Service_Role_Is_Refused_On_Insert(string code)
    {
        var (org, membership) = await SeedMembershipAsync();
        var role = await BuiltInRoleAsync(code);

        var error = await OwnerErrorAsync(InsertGrant, Grant(org, membership, role));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("membership_role_role_guard");
    }

    [Fact]
    public async Task Another_Orgs_Custom_Role_Is_Refused_On_Insert()
    {
        var (org, membership) = await SeedMembershipAsync();
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var foreign = await SeedCustomRoleAsync(otherOrg, "member");

        var error = await OwnerErrorAsync(InsertGrant, Grant(org, membership, foreign));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("membership_role_role_guard");
    }

    [Fact]
    public async Task A_Missing_Role_Is_Refused_On_Insert()
    {
        var (org, membership) = await SeedMembershipAsync();

        var error = await OwnerErrorAsync(InsertGrant, Grant(org, membership, Guid.CreateVersion7()));

        error.SqlState.ShouldBeOneOf(PostgresErrorCodes.CheckViolation, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_Builtin_Member_Role_And_The_Orgs_Own_Custom_Role_Are_Accepted()
    {
        var (org, membership) = await SeedMembershipAsync();
        var builtIn = await BuiltInRoleAsync("viewer");
        var custom = await SeedCustomRoleAsync(org, "member");

        await Fixture.SeedAsync(InsertGrant, Grant(org, membership, builtIn), Token);
        await Fixture.SeedAsync(InsertGrant, Grant(org, membership, custom), Token);

        (await OwnerScalarAsync(
            "select count(*) from public.membership_role where membership_id = @membership",
            [("membership", membership)])).ShouldBe(2);
    }

    [Fact]
    public async Task Updating_The_Role_To_A_Staff_Or_Foreign_Role_Is_Refused()
    {
        var (org, membership) = await SeedMembershipAsync();
        var grantId = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            InsertGrant,
            [("id", grantId), ("org", org), ("membership", membership), ("role", await BuiltInRoleAsync("viewer"))],
            Token);

        var staff = await OwnerErrorAsync(
            "update public.membership_role set role_id = @role where id = @id",
            [("id", grantId), ("role", await BuiltInRoleAsync("platform_operator"))]);
        staff.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        var foreign = await SeedCustomRoleAsync(await Fixture.SeedOrgAsync(cancellationToken: Token), "member");
        var other = await OwnerErrorAsync(
            "update public.membership_role set role_id = @role where id = @id",
            [("id", grantId), ("role", foreign)]);
        other.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task A_Granted_Role_Cannot_Change_Kind_Or_Org_And_Cannot_Be_Deleted()
    {
        var (org, membership) = await SeedMembershipAsync();
        var custom = await SeedCustomRoleAsync(org, "member");
        await Fixture.SeedAsync(InsertGrant, Grant(org, membership, custom), Token);

        var kind = await OwnerErrorAsync(
            "update public.role set kind = 'staff' where id = @role",
            [("role", custom)]);
        kind.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        kind.ConstraintName.ShouldBe("role_granted_scope_guard");

        var scope = await OwnerErrorAsync(
            "update public.role set org_id = @other where id = @role",
            [("role", custom), ("other", await Fixture.SeedOrgAsync(cancellationToken: Token))]);
        scope.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        var delete = await OwnerErrorAsync("delete from public.role where id = @role", [("role", custom)]);
        delete.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        delete.ConstraintName.ShouldBe("membership_role_role_fk");
    }

    [Fact]
    public async Task An_Ungranted_Custom_Role_Can_Still_Change_Kind()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var custom = await SeedCustomRoleAsync(org, "member");

        await Fixture.SeedAsync("update public.role set kind = 'service' where id = @role", [("role", custom)], Token);

        (await OwnerScalarAsync("select count(*) from public.role where id = @role and kind = 'service'", [("role", custom)]))
            .ShouldBe(1);
    }

    private static (string, object)[] Grant(Guid org, Guid membership, Guid role) =>
        [("id", Guid.CreateVersion7()), ("org", org), ("membership", membership), ("role", role)];

    private async Task<(Guid Org, Guid Membership)> SeedMembershipAsync()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membership = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org, @email, 'pending')",
            [("id", membership), ("org", org), ("email", $"m-{membership:N}@example.test")],
            Token);
        return (org, membership);
    }

    private async Task<Guid> SeedCustomRoleAsync(Guid org, string kind)
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org, @code, 'Custom', false, @kind)",
            [("id", id), ("org", org), ("code", "c_" + id.ToString("N")[..12]), ("kind", kind)],
            Token);
        return id;
    }

    private async Task<Guid> BuiltInRoleAsync(string code) =>
        (Guid)await OwnerScalarObjectAsync(
            "select id from public.role where org_id is null and code = @code",
            [("code", code)]);

    private async Task<PostgresException> OwnerErrorAsync(string sql, (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(Token));
    }

    private async Task<long> OwnerScalarAsync(string sql, (string Name, object Value)[] parameters) =>
        (long)await OwnerScalarObjectAsync(sql, parameters);

    private async Task<object> OwnerScalarObjectAsync(string sql, (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (await command.ExecuteScalarAsync(Token))!;
    }
}
