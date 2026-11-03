using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The catalogue's RLS posture (02 §4.5, FR-02-1, 00 §5.4.1): a tenant reads the
/// platform catalogue and its own rows, never another org's, and the application role
/// cannot write catalogue rows — while an org-scoped row of its own org is writable,
/// which is the shape the flagged custom-role commands of 02.2.1-T2 need. Every probe
/// runs through a real tenant scope as <c>evidgate_app</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CatalogueRlsTests : CatalogueTestBase
{
    public CatalogueRlsTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// A tenant sees the whole built-in catalogue (00 §5.4.1: readable by every org) and
    /// none of another org's custom role — the "never another org's row" half of 02.9.1.
    /// </summary>
    [Fact]
    public async Task Tenant_Sees_The_BuiltIn_Catalogue_And_No_Foreign_Role()
    {
        var orgA = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var orgB = await Fixture.SeedOrgAsync(cancellationToken: Token);

        var customB = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org_id, @code, @name, false, 'member')",
            [("id", customB), ("org_id", orgB), ("code", "custom_b"), ("name", "Custom B")],
            Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(orgA, readOnlyTransaction: true, cancellationToken: Token);

        var permissions = await scope.Db.ScalarAsync<long>("select count(*) from public.permission", cancellationToken: Token);
        permissions.ShouldBe(Catalogue.Document.Permissions.Count);

        var roles = await scope.Db.ScalarAsync<long>("select count(*) from public.role", cancellationToken: Token);
        roles.ShouldBe(Catalogue.Document.Roles.Count);

        var own = await scope.Db.ScalarAsync<long>(
            "select count(*) from public.role where org_id is not null",
            cancellationToken: Token);
        own.ShouldBe(0, "org A holds no custom role of its own");

        var foreign = await scope.Db.QueryAsync<Guid>(
            "select id from public.role where org_id is not null",
            cancellationToken: Token);
        foreign.ShouldBeEmpty("another org's custom role must be invisible under the catalogue policy");
    }

    /// <summary>
    /// FR-02-1 / 02 §4.5: catalogue rows are written only by the owner. The app role is
    /// refused by privilege (`permission` has no write grant) and by policy (a
    /// built-in `role`/`role_permission` row fails the org-scoped WITH CHECK), and the
    /// seeded rows are unchanged afterwards.
    /// </summary>
    [Fact]
    public async Task AppRole_Cannot_Write_Catalogue_Rows()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        // permission: no INSERT/UPDATE/DELETE privilege at all (0010 revokes the DML
        // 0001's default privileges hand to every new table).
        var update = await RefusedAsync(
            org,
            "update public.permission set description = 'tampered'");
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // role: a built-in row carries org_id IS NULL, which the insert policy's
        // WITH CHECK (org_id = evidgate.current_org_id()) can never satisfy. 42501 is
        // PostgreSQL's state for both a privilege refusal and an RLS policy refusal.
        var builtInRole = await RefusedAsync(
            org,
            "insert into public.role (id, org_id, code, name, is_builtin, kind) "
            + "values (gen_random_uuid(), null, 'sneaky', 'Sneaky', true, 'member')");
        builtInRole.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // role_permission: the same for a grant of a built-in role.
        var builtInGrant = await RefusedAsync(
            org,
            "insert into public.role_permission (id, org_id, role_id, permission_code) "
            + "select gen_random_uuid(), null, r.id, 'run:read' from public.role r "
            + "where r.code = 'viewer' and r.org_id is null");
        builtInGrant.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // Nothing changed: the seeded catalogue is intact for the next reader.
        await using var scope = await Fixture.BeginTenantScopeAsync(org, readOnlyTransaction: true, cancellationToken: Token);
        (await scope.Db.ScalarAsync<long>("select count(*) from public.permission", cancellationToken: Token))
            .ShouldBe(Catalogue.Document.Permissions.Count);
        (await scope.Db.ScalarAsync<long>(
            "select count(*) from public.role where org_id is null",
            cancellationToken: Token))
            .ShouldBe(Catalogue.Document.Roles.Count);
        (await scope.Db.ScalarAsync<long>(
            "select count(*) from public.role where org_id is not null",
            cancellationToken: Token))
            .ShouldBe(0, "no role row was created by the refused writes");
    }

    /// <summary>
    /// W7-SEC-05 (INSERT half): a grant row of the caller's own org that points at a
    /// built-in role is refused — the insert policy ties `role_permission.org_id` to the
    /// referenced role's org. Without the tie the app role could attach a code to
    /// `viewer` (or `tenant_admin`) for every member of the tenant who holds it: the
    /// built-ins are shared by all orgs and the `role(id)` FK is org-agnostic. The
    /// catalogue is unchanged afterwards, so the refusal is a refusal and not a
    /// partial write.
    /// </summary>
    [Fact]
    public async Task AppRole_Cannot_Attach_An_Org_Grant_To_A_Builtin_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var viewerRoleId = await IdentityTestData.RoleIdAsync(Fixture, org, "viewer", Token);

        var refusal = await RefusedAsync(
            org,
            "insert into public.role_permission (id, org_id, role_id, permission_code) "
            + $"values (gen_random_uuid(), '{org}'::uuid, '{viewerRoleId}'::uuid, 'role:write')");

        refusal.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // `viewer` still holds exactly the codes the catalogue file lists for it: no
        // forged `role:write`, and nothing else changed either.
        (await IdentityTestData.RolePermissionsAsync(Fixture, org, viewerRoleId, Token))
            .ShouldBe(Catalogue.PermissionsOf("viewer"));
    }

    /// <summary>
    /// W7-SEC-05 (UPDATE half): the same tie guards `update role_permission set
    /// role_id = …`, so the hole cannot be reached by rewriting an existing row of the
    /// caller's own org into a grant of a built-in role instead of inserting one. The
    /// row keeps its custom role afterwards.
    /// </summary>
    [Fact]
    public async Task AppRole_Cannot_Move_A_Grant_Onto_A_Builtin_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var viewerRoleId = await IdentityTestData.RoleIdAsync(Fixture, org, "viewer", Token);
        var customRoleId = Guid.CreateVersion7();
        var grantId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org_id, @code, @name, false, 'member')",
            [("id", customRoleId), ("org_id", org), ("code", "custom_move"), ("name", "Custom move")],
            Token);

        await Fixture.SeedAsync(
            "insert into public.role_permission (id, org_id, role_id, permission_code) values (@id, @org_id, @role_id, 'case:write')",
            [("id", grantId), ("org_id", org), ("role_id", customRoleId)],
            Token);

        var refusal = await RefusedAsync(
            org,
            $"update public.role_permission set role_id = '{viewerRoleId}'::uuid where id = '{grantId}'::uuid");

        refusal.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, readOnlyTransaction: true, cancellationToken: Token);
        (await scope.Db.ScalarAsync<Guid>(
            "select role_id from public.role_permission where id = @id",
            new { id = grantId },
            Token)).ShouldBe(customRoleId, "the refused UPDATE changed nothing");
    }

    /// <summary>
    /// Runs one statement in its own tenant scope and returns the refusal. A fresh scope
    /// per attempt is required: the first refusal aborts its transaction, so a second
    /// statement on the same scope would fail with 25P02 instead of the state under test.
    /// </summary>
    private async Task<PostgresException> RefusedAsync(Guid orgId, string sql)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, cancellationToken: Token);
        return await Should.ThrowAsync<PostgresException>(async () => await scope.ExecuteAsync(sql));
    }

    /// <summary>
    /// The write policies are org-scoped, not blanket denials: the app role may create a
    /// role of its own org (the shape 02.2.1-T2's flagged custom-role command needs) and
    /// may read it back, while a built-in row stays untouchable (previous test).
    /// </summary>
    [Fact]
    public async Task AppRole_May_Create_A_Role_Of_Its_Own_Org()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var roleId = Guid.CreateVersion7();

        await using (var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token))
        {
            await scope.Db.ExecuteAsync(
                "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org_id, @code, @name, false, 'member')",
                new { id = roleId, org_id = org, code = "custom_a", name = "Custom A" },
                Token);

            (await scope.Db.ScalarAsync<long>(
                "select count(*) from public.role where org_id is not null",
                cancellationToken: Token)).ShouldBe(1);
        }
    }

    /// <summary>
    /// 02 §4.5 fail-closed, catalogue exception: with no tenant setting the policy still
    /// returns the built-in rows (they are readable by every org) and none of any org's
    /// custom rows — never an error and never every row.
    /// </summary>
    [Fact]
    public async Task Without_A_Tenant_Setting_The_Catalogue_Still_Reads_BuiltIns_Only()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org_id, @code, @name, false, 'member')",
            [("id", Guid.CreateVersion7()), ("org_id", org), ("code", "custom_a"), ("name", "Custom A")],
            Token);

        await using var command = Fixture.AppDataSource.CreateCommand(
            "select (select count(*) from public.permission), (select count(*) from public.role)");

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        reader.GetInt64(0).ShouldBe(Catalogue.Document.Permissions.Count);
        reader.GetInt64(1).ShouldBe(Catalogue.Document.Roles.Count);
    }
}
