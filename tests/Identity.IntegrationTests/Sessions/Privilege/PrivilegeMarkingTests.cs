using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Privilege;

/// <summary>
/// AC7 / D5b at command level: the four privilege-changing handlers mark the changed
/// user's live session for rotation in the same transaction as their write, through
/// the real pipeline and the real <c>ISessionPrivilegeChange</c>, over the harness
/// database as <c>evidgate_app</c> under RLS. Commands are dispatched through the host
/// mediator with the tenant and principal published the way the HTTP request filter
/// publishes them, so the C2 behaviours (validation, authorization, idempotency,
/// tenant transaction) run exactly as in production.
///
/// <para>
/// Each path asserts the same four-row session matrix on <c>public.session</c>, read on
/// the owner connection after the command returns:
/// the changed user's live session is marked (<c>rotation_required_at</c> NOT NULL),
/// and the changed user's revoked row, the changed user's expired row, and another
/// user's live row stay NULL.
/// </para>
///
/// <para>
/// Each path also has a refused command whose trigger leaves the write undone, and
/// every row in the matrix must stay NULL. The trigger is named in each test's doc
/// comment.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class PrivilegeMarkingTests : CatalogueTestBase
{
    public PrivilegeMarkingTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// <c>GrantRoleCommandHandler</c> marks the grantee on a new grant. The handler marks
    /// only when the pair was not already granted.
    /// </summary>
    [Fact]
    public async Task Grant_Role_Marks_Only_The_Changed_Users_Live_Session()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, memberUserId) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var releaseApproverId = await RoleIdAsync("release_approver");
        var matrix = await SeedSessionMatrixAsync(memberUserId);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        var result = await SendAsync(
            factory,
            org,
            adminUserId,
            new GrantRoleCommand(org, memberId, releaseApproverId, "grant-privilege-1"));

        result.IsSuccess.ShouldBeTrue();
        await AssertRotationFlagsAsync(matrix, changedUserLiveFlagged: true);
    }

    /// <summary>
    /// <c>RevokeRoleCommandHandler</c> marks the holder after the grant row is removed. The
    /// revoked role is <c>release_approver</c>, not <c>tenant_admin</c>, so the last-admin
    /// guard does not apply.
    /// </summary>
    [Fact]
    public async Task Revoke_Role_Marks_Only_The_Changed_Users_Live_Session()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, memberUserId) = await SeedActiveMemberAsync(
            org,
            "member@example.com",
            "viewer",
            "release_approver");
        var releaseApproverId = await RoleIdAsync("release_approver");
        var matrix = await SeedSessionMatrixAsync(memberUserId);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        var result = await SendAsync(
            factory,
            org,
            adminUserId,
            new RevokeRoleCommand(org, memberId, releaseApproverId, "revoke-privilege-1"));

        result.IsSuccess.ShouldBeTrue();
        await AssertRotationFlagsAsync(matrix, changedUserLiveFlagged: true);
    }

    /// <summary>
    /// <c>UpdateMembershipCommandHandler</c> marks the user after <c>active → removed</c>.
    /// The version is read from the row, so the precondition is real.
    /// </summary>
    [Fact]
    public async Task Update_Membership_Marks_Only_The_Changed_Users_Live_Session()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, memberUserId) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var version = await ScalarAsync<int>("select version from public.membership where id = @id", ("id", memberId));
        var matrix = await SeedSessionMatrixAsync(memberUserId);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        var result = await SendAsync(
            factory,
            org,
            adminUserId,
            new UpdateMembershipCommand(org, memberId, MembershipStatus.Removed, version, "update-privilege-1"));

        result.IsSuccess.ShouldBeTrue();
        await AssertRotationFlagsAsync(matrix, changedUserLiveFlagged: true);
    }

    /// <summary>
    /// <c>RemoveMemberCommandHandler</c> marks the user after <c>active → removed</c>. The
    /// member holds no admin role, so the last-admin guard does not apply.
    /// </summary>
    [Fact]
    public async Task Remove_Member_Marks_Only_The_Changed_Users_Live_Session()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, memberUserId) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var matrix = await SeedSessionMatrixAsync(memberUserId);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        var result = await SendAsync(
            factory,
            org,
            adminUserId,
            new RemoveMemberCommand(org, memberId, "remove-privilege-1"));

        result.IsSuccess.ShouldBeTrue();
        await AssertRotationFlagsAsync(matrix, changedUserLiveFlagged: true);
    }

    /// <summary>
    /// Trigger: <c>GrantRoleCommand</c> names a role id that does not exist. The handler
    /// answers <c>Conflict(UnknownRole)</c> before any write, so no session is marked.
    /// </summary>
    [Fact]
    public async Task Grant_Role_Refused_For_An_Unknown_Role_Leaves_Every_Flag_Null()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, memberUserId) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var matrix = await SeedSessionMatrixAsync(memberUserId);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        var result = await SendAsync(
            factory,
            org,
            adminUserId,
            new GrantRoleCommand(org, memberId, Guid.CreateVersion7(), "grant-refused-1"));

        result.IsSuccess.ShouldBeFalse();
        await AssertRotationFlagsAsync(matrix, changedUserLiveFlagged: false);
    }

    /// <summary>
    /// Trigger: <c>RevokeRoleCommand</c> names a role the member was never granted. The
    /// handler answers 404 before any write, so no session is marked. The grant that does
    /// exist is still there afterwards.
    /// </summary>
    [Fact]
    public async Task Revoke_Role_Not_Granted_Leaves_Every_Flag_Null()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, memberUserId) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var releaseApproverId = await RoleIdAsync("release_approver");
        var matrix = await SeedSessionMatrixAsync(memberUserId);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        var result = await SendAsync(
            factory,
            org,
            adminUserId,
            new RevokeRoleCommand(org, memberId, releaseApproverId, "revoke-refused-1"));

        result.IsSuccess.ShouldBeFalse();
        await AssertRotationFlagsAsync(matrix, changedUserLiveFlagged: false);
        (await ScalarAsync<long>(
            "select count(*) from public.membership_role where membership_id = @id",
            ("id", memberId))).ShouldBe(1L, "the viewer grant the member does hold is untouched");
    }

    /// <summary>
    /// Trigger: <c>UpdateMembershipCommand</c> carries <c>version + 1</c>, a stale
    /// precondition. The handler answers <c>Stale</c> before any write, so no session is
    /// marked and the membership stays active.
    /// </summary>
    [Fact]
    public async Task Update_Membership_Stale_Version_Leaves_Every_Flag_Null()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, memberUserId) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var version = await ScalarAsync<int>("select version from public.membership where id = @id", ("id", memberId));
        var matrix = await SeedSessionMatrixAsync(memberUserId);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        var result = await SendAsync(
            factory,
            org,
            adminUserId,
            new UpdateMembershipCommand(org, memberId, MembershipStatus.Removed, version + 1, "update-stale-1"));

        result.IsSuccess.ShouldBeFalse();
        await AssertRotationFlagsAsync(matrix, changedUserLiveFlagged: false);
        (await ScalarAsync<string>("select status from public.membership where id = @id", ("id", memberId)))
            .ShouldBe(MembershipStatus.Active, "the stale write did not change the membership");
    }

    /// <summary>
    /// Trigger: <c>RemoveMemberCommand</c> targets the org's only active <c>tenant_admin</c>.
    /// The handler answers <c>Conflict(LastAdmin)</c> before any write, so no session is
    /// marked and the membership stays active.
    /// </summary>
    [Fact]
    public async Task Remove_Last_Admin_Leaves_Every_Flag_Null()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (adminId, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var matrix = await SeedSessionMatrixAsync(adminUserId);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        var result = await SendAsync(
            factory,
            org,
            adminUserId,
            new RemoveMemberCommand(org, adminId, "remove-last-admin-1"));

        result.IsSuccess.ShouldBeFalse();
        await AssertRotationFlagsAsync(matrix, changedUserLiveFlagged: false);
        (await ScalarAsync<string>("select status from public.membership where id = @id", ("id", adminId)))
            .ShouldBe(MembershipStatus.Active, "the refused removal did not change the membership");
    }

    /// <summary>
    /// Dispatches one command through the host mediator. The tenant and the caller
    /// principal are published on a fresh DI scope, the way the HTTP request filter
    /// publishes them, so the pipeline sees the same ambient context as an admin request.
    /// </summary>
    private static async Task<TResponse> SendAsync<TResponse>(
        IdentityApiFactory factory,
        Guid orgId,
        Guid callerUserId,
        ICommand<TResponse> command)
    {
        using var scope = factory.Services.CreateScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<IRequestTenantContextSetter>()
            .Publish(orgId, null);
        using var principal = scope.ServiceProvider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Member(callerUserId));
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        return await mediator.Send(command, Token);
    }

    /// <summary>
    /// The four-row matrix for one changed user. The changed user holds a live, a revoked
    /// and an expired session. Another user holds a live session that the change must not touch.
    /// </summary>
    private async Task<SessionMatrix> SeedSessionMatrixAsync(Guid changedUserId)
    {
        var otherUserId = await SeedUserAsync($"other-{Guid.CreateVersion7():N}@example.com");

        return new SessionMatrix(
            Live: await SeedSessionAsync(changedUserId, SessionShape.Live),
            Revoked: await SeedSessionAsync(changedUserId, SessionShape.Revoked),
            Expired: await SeedSessionAsync(changedUserId, SessionShape.Expired),
            OtherUserLive: await SeedSessionAsync(otherUserId, SessionShape.Live));
    }

    /// <summary>
    /// Asserts the matrix after a command. The changed user's live session is flagged only
    /// when the command should have marked it. The other three rows are never flagged.
    /// </summary>
    private async Task AssertRotationFlagsAsync(SessionMatrix matrix, bool changedUserLiveFlagged)
    {
        (await RotationRequiredAsync(matrix.Live)).ShouldBe(
            changedUserLiveFlagged,
            "the changed user's live session is marked for rotation exactly when the command succeeds");
        (await RotationRequiredAsync(matrix.Revoked)).ShouldBeFalse(
            "a revoked session is never marked");
        (await RotationRequiredAsync(matrix.Expired)).ShouldBeFalse(
            "an expired session is never marked");
        (await RotationRequiredAsync(matrix.OtherUserLive)).ShouldBeFalse(
            "another user's live session is never marked");
    }

    private Task<bool> RotationRequiredAsync(Guid sessionId) =>
        ScalarAsync<bool>(
            "select rotation_required_at is not null from public.session where id = @id",
            ("id", sessionId));

    private async Task<Guid> SeedUserAsync(string email)
    {
        var userId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            """insert into public."user" (id, email) values (@id, @email)""",
            [("id", userId), ("email", email)],
            Token);

        return userId;
    }

    /// <summary>
    /// Seeds one session for <paramref name="userId"/> in the given shape, as the owner.
    /// Live: both <c>expires_at</c> and <c>absolute_expires_at</c> are in the future.
    /// Revoked: live timestamps and <c>revoked_at</c> set. Expired: <c>expires_at</c> is in the
    /// past and <c>absolute_expires_at</c> is in the future.
    /// </summary>
    private async Task<Guid> SeedSessionAsync(Guid userId, SessionShape shape)
    {
        var sessionId = Guid.CreateVersion7();
        var sql = shape switch
        {
            SessionShape.Live =>
                """
                insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at)
                values (@id, @user_id, @cookie_hmac, 1, now() + interval '1 day', now() + interval '30 days')
                """,
            SessionShape.Revoked =>
                """
                insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at, revoked_at)
                values (@id, @user_id, @cookie_hmac, 1, now() + interval '1 day', now() + interval '30 days', now())
                """,
            SessionShape.Expired =>
                """
                insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at)
                values (@id, @user_id, @cookie_hmac, 1, now() - interval '1 hour', now() + interval '30 days')
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };

        await Fixture.SeedAsync(
            sql,
            [
                ("id", sessionId),
                ("user_id", userId),
                ("cookie_hmac", RandomNumberGenerator.GetBytes(32)),
            ],
            Token);

        return sessionId;
    }

    /// <summary>
    /// Seeds one <c>active</c> membership with the named built-in roles, as the owner
    /// (arranging rows only). Returns the membership id and its user id.
    /// </summary>
    private async Task<(Guid MembershipId, Guid UserId)> SeedActiveMemberAsync(Guid orgId, string email, params string[] roleCodes)
    {
        var membershipId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        // 0044's `membership_user_fk` (membership.user_id -> public."user") makes the bound
        // membership name a real account, so the account is seeded before the membership.
        await Fixture.SeedAsync(
            """insert into public."user" (id, email) values (@id, @email)""",
            [("id", userId), ("email", email)],
            Token);

        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
            [("id", membershipId), ("org_id", orgId), ("user_id", userId), ("email", email)],
            Token);

        foreach (var code in roleCodes)
        {
            await Fixture.SeedAsync(
                """
                insert into public.membership_role (id, org_id, membership_id, role_id)
                select @id, @org_id, @membership_id, role.id from public.role
                 where role.org_id is null and role.code = @code
                """,
                [
                    ("id", Guid.CreateVersion7()),
                    ("org_id", orgId),
                    ("membership_id", membershipId),
                    ("code", code),
                ],
                Token);
        }

        return (membershipId, userId);
    }

    /// <summary>The id of one built-in role of 0010's seed.</summary>
    private Task<Guid> RoleIdAsync(string code) =>
        ScalarAsync<Guid>("select id from public.role where org_id is null and code = @code", ("code", code));

    /// <summary>One scalar on the owner connection (arranging/asserting rows, not through the API).</summary>
    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    private sealed record SessionMatrix(Guid Live, Guid Revoked, Guid Expired, Guid OtherUserLive);

    private enum SessionShape
    {
        Live,
        Revoked,
        Expired,
    }
}
