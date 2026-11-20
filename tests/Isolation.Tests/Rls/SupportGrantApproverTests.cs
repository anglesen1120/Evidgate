using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The approver clause of the <c>support_grant</c> guard: only an active member of the grant's org who
/// holds the built-in <c>tenant_admin</c> role may approve. Every refusal asserts the clause's SQLSTATE
/// and exact message, and is followed by a positive control that approves the same grant once the
/// approver qualifies, so a refusal cannot pass for an unrelated reason.
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class SupportGrantApproverTests
{
    private static readonly object Gate = new();
    private static Task<string>? _database;
    private const string CaseRead = "{case:read}";
    private readonly PostgresIsolationFixture _fixture;

    public SupportGrantApproverTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    private Task<string> DatabaseAsync()
    {
        lock (Gate)
        {
            return _database ??= _fixture.CreateDatabaseAsync();
        }
    }

    private sealed record GrantCase(string Database, Guid Org, Guid OtherOrg, Guid Operator, Guid Approver,
        Guid Membership, Guid MembershipRole, Guid Grant)
    {
        // The whole v7 uuid, not a prefix: its leading bits are a timestamp, so a prefix repeats
        // between cases created in the same minute and would collide on the slug and email keys.
        public string Tag => Org.ToString("N");
        public string Email => Approver.ToString("N");
    }

    /// <summary>One org pair, one requested grant, and the approver's membership in the shape under test.</summary>
    private async Task<GrantCase> CaseAsync(string membership = "same", string status = "active",
        string role = "tenant_admin", bool custom = false)
    {
        var c = new GrantCase(await DatabaseAsync(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7());
        await _fixture.ExecuteAsync(c.Database, $"""
            insert into public.organization (id, name, slug) values
              ('{c.Org}', 'Grant', 'grant-{c.Tag}'), ('{c.OtherOrg}', 'Other', 'other-{c.Tag}');
            insert into public."user" (id, email) values
              ('{c.Operator}', 'op-{c.Tag}@grant.test'), ('{c.Approver}', 'admin-{c.Email}@grant.test');
            insert into public.support_grant (id, org_id, operator_user_id, scopes, reason, expires_at)
            values ('{c.Grant}', '{c.Org}', '{c.Operator}', '{CaseRead}', 'investigate', statement_timestamp() + interval '1 hour');
            """);
        if (membership != "none")
        {
            await SeedMembershipAsync(c, membership == "other" ? c.OtherOrg : c.Org, status, role, custom);
        }

        return c;
    }

    private async Task SeedMembershipAsync(GrantCase c, Guid org, string status, string role, bool custom)
    {
        var roleId = Guid.CreateVersion7();
        if (custom)
        {
            await _fixture.ExecuteAsync(c.Database, $"""
                insert into public.role (id, org_id, code, name, is_builtin, kind)
                values ('{roleId}', '{org}', 'tenant_admin', 'Custom tenant admin', false, 'member');
                """);
        }

        var roleQuery = custom ? $"id = '{roleId}'" : $"org_id is null and is_builtin and code = '{role}'";
        await _fixture.ExecuteAsync(c.Database, $"""
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{c.Membership}', '{org}', '{c.Approver}', 'admin-{c.Email}@grant.test', '{status}');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select '{c.MembershipRole}', '{org}', '{c.Membership}', id from public.role where {roleQuery};
            """);
        (await _fixture.ScalarAsync<int>(c.Database,
            $"select count(*)::int from public.membership_role where id = '{c.MembershipRole}'"))
            .ShouldBe(1, $"the {role} role must be seeded before the fact means anything");
    }

    /// <summary>Restores a qualifying approver for the positive control, whichever defect the case seeded.</summary>
    private async Task RestoreAdminAsync(GrantCase c)
    {
        await _fixture.ExecuteAsync(c.Database, $"""
            update public.membership set status = 'active' where org_id = '{c.Org}' and user_id = '{c.Approver}';
            insert into public.membership (id, org_id, user_id, invited_email, status)
            select uuidv7(), '{c.Org}', '{c.Approver}', 'admin-{c.Email}@grant.test', 'active'
            where not exists (select 1 from public.membership where org_id = '{c.Org}' and user_id = '{c.Approver}');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select uuidv7(), m.org_id, m.id, r.id from public.membership m cross join public.role r
            where m.org_id = '{c.Org}' and m.user_id = '{c.Approver}'
              and r.org_id is null and r.is_builtin and r.code = 'tenant_admin'
            on conflict (membership_id, role_id) do nothing;
            """);
    }

    private Task<TenantScope> ScopeAsync(GrantCase c, bool owner = false) =>
        TenantScope.BeginAsync(
            owner ? _fixture.OwnerConnectionString(c.Database) : _fixture.AppConnectionString(c.Database), c.Org);

    private static string ApproveSql =>
        "update public.support_grant set approved_by = @a, approved_at = statement_timestamp() where id = @g";

    private static async Task<int> ApproveAsync(TenantScope scope, GrantCase c, Guid? approver = null) =>
        await scope.ExecuteAsync(ApproveSql, ("a", approver ?? c.Approver), ("g", c.Grant));

    private async Task AssertRefusedAsync(GrantCase c, bool owner = false)
    {
        await using var scope = await ScopeAsync(c, owner);
        var error = await Should.ThrowAsync<PostgresException>(() => ApproveAsync(scope, c));
        error.SqlState.ShouldBe("23514");
        error.MessageText.ShouldBe($"support_grant {c.Grant} approver is not an active tenant_admin of the organization");
    }

    private async Task AssertApprovedAsync(GrantCase c, string because)
    {
        (await _fixture.ScalarAsync<bool>(c.Database, $"""
            select approved_by = '{c.Approver}'::uuid and approved_at is not null and revoked_at is null
            from public.support_grant where id = '{c.Grant}'
            """)).ShouldBeTrue(because);
    }

    private async Task ApproveAndCommitAsync(GrantCase c, bool owner, string because)
    {
        await using var scope = await ScopeAsync(c, owner);
        (await ApproveAsync(scope, c)).ShouldBe(1);
        await scope.CommitAsync();
        await AssertApprovedAsync(c, because);
    }

    [Fact]
    public async Task ApproverRule_ActiveBuiltinTenantAdmin_ApprovesWithServerStatementTime()
    {
        var c = await CaseAsync();
        await using var scope = await ScopeAsync(c);
        (await scope.ScalarAsync<bool>("""
            with approved as (
              update public.support_grant set approved_by = @a, approved_at = statement_timestamp() + interval '1 year'
              where id = @g returning approved_at)
            select approved_at = statement_timestamp() from approved
            """, ("a", c.Approver), ("g", c.Grant))).ShouldBeTrue("the decision time is the server's");
        await scope.CommitAsync();
        await AssertApprovedAsync(c, "an active built-in tenant_admin approves");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApproverRule_NonMember_RefusesAndOwnerCannotBypass(bool owner)
    {
        var c = await CaseAsync(membership: "none");
        await AssertRefusedAsync(c, owner);
        await RestoreAdminAsync(c);
        await ApproveAndCommitAsync(c, owner, "the same grant approves once the approver is a tenant_admin");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApproverRule_MembershipOfAnotherOrgOnly_RefusesAndOwnerCannotBypass(bool owner)
    {
        var c = await CaseAsync(membership: "other");
        await AssertRefusedAsync(c, owner);
        await RestoreAdminAsync(c);
        await ApproveAndCommitAsync(c, owner, "a membership of the grant's own org is what qualifies");
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("removed")]
    public async Task ApproverRule_InactiveMembership_Refuses(string status)
    {
        var c = await CaseAsync(status: status);
        await AssertRefusedAsync(c);
        await RestoreAdminAsync(c);
        await ApproveAndCommitAsync(c, false, "an active membership is what qualifies");
    }

    [Fact]
    public async Task ApproverRule_ActiveMemberWithViewerRole_Refuses()
    {
        var c = await CaseAsync(role: "viewer");
        await AssertRefusedAsync(c);
        await RestoreAdminAsync(c);
        await ApproveAndCommitAsync(c, false, "the built-in tenant_admin role is what qualifies, not any membership");
    }

    [Fact]
    public async Task ApproverRule_CustomRoleNamedTenantAdmin_Refuses()
    {
        var c = await CaseAsync(custom: true);
        await AssertRefusedAsync(c);
        await RestoreAdminAsync(c);
        await ApproveAndCommitAsync(c, false, "a custom role's spelling does not make it the built-in role");
    }

    [Fact]
    public async Task ApproverDiffersCheck_ApproverIsTheOperator_StillRefuses()
    {
        var c = await CaseAsync();
        await RestoreAdminAsync(c with { Approver = c.Operator });
        await using (var scope = await ScopeAsync(c))
        {
            var error = await Should.ThrowAsync<PostgresException>(() => ApproveAsync(scope, c, c.Operator));
            error.SqlState.ShouldBe("23514");
            error.ConstraintName.ShouldBe("support_grant_approver_differs_check");
        }

        await ApproveAndCommitAsync(c, false, "the requester cannot approve, another tenant_admin can");
    }
}
