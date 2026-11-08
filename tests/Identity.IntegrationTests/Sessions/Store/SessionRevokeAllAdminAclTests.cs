using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// What the administrative revoke-all definer may be called by, and what its <c>membership:write</c> join counts
/// as an authority: the defined privilege of the function itself, and the org scope of a custom role.
/// </summary>
/// <remarks>
/// <c>membership_role.role_id</c> carries no foreign key (0011), so a row written around the commands can point an
/// org-A membership at another org's custom role. This definer does not run under the app path's role RLS, so the
/// grant join has to scope the role itself, and the cases here are what make that scope fire.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionRevokeAllAdminAclTests : SessionRevokeAllAdminTestBase
{
    /// <summary>The argument list the definer must keep, in order.</summary>
    private const string ExpectedArguments =
        "p_cookie_hmacs bytea[], p_target_user_id uuid, p_org_id uuid, p_now timestamp with time zone, "
        + "p_request_id text, p_trace_id text";

    /// <summary>Own database: catalog counts and role switches want a database nothing else is writing.</summary>
    /// <param name="fixture">The assembly fixture.</param>
    public SessionRevokeAllAdminAclTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The defined privilege: one function of that name, owner-owned with an empty <c>search_path</c> and the
    /// bounded wait, EXECUTE for <c>evidgate_app</c> alone — and the operator, the sweep role and PUBLIC are
    /// refused by the server when they try to run it.
    /// </summary>
    [Fact]
    public async Task Only_The_App_Role_May_Execute_The_Admin_Revoke_All_Definer()
    {
        await AssertOneDefinitionAsync();
        await AssertDefinerPostureAsync();
        await AssertOnlyTheAppRoleHoldsExecuteAsync();
    }

    /// <summary>
    /// A custom role is an authority only inside its own org: the same-org role holding <c>membership:write</c>
    /// authorizes the call (the positive control, so the only variable in the refusals is the role's org), a
    /// membership pointing at ANOTHER org's role does not, and a membership whose status is <c>removed</c> holds
    /// its <c>membership_role</c> row but no active grant.
    /// </summary>
    [Fact]
    public async Task Only_A_Custom_Role_Of_The_Acting_Org_Authorizes_The_Admin_Revoke_All()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);

        var localRole = await SeedCustomRoleAsync(org, "admin_revoke_local");
        var localActor = await SeedMemberWithRoleAsync(org, localRole, "active", Unique.Email("admin-revoke-local"));
        var foreignRole = await SeedCustomRoleAsync(otherOrg, "admin_revoke_foreign");
        var foreignActor = await SeedMemberWithRoleAsync(org, foreignRole, "active", Unique.Email("admin-revoke-foreign"));
        var removedActor = await SeedMemberWithRoleAsync(org, localRole, "removed", Unique.Email("admin-revoke-removed"));

        var localTarget = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var refusedTarget = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var removedTarget = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var localTicket = await CreateSessionAsync(localActor, clock);
        var foreignTicket = await CreateSessionAsync(foreignActor, clock);
        var removedTicket = await CreateSessionAsync(removedActor, clock);
        var localTargetTicket = await CreateSessionAsync(localTarget, clock);
        var refusedTargetTicket = await CreateSessionAsync(refusedTarget, clock);
        var removedTargetTicket = await CreateSessionAsync(removedTarget, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(localTicket), localTarget, org, null, null, Token))
            .Status.ShouldBe(
                SessionRouteStatus.Done,
                "a custom role of THIS org carrying membership:write authorizes the call");

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(foreignTicket), refusedTarget, org, null, null, Token))
            .Status.ShouldBe(
                SessionRouteStatus.NotFound,
                "one org's custom role grants nothing in another org, whatever the membership_role row says");

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(removedTicket), removedTarget, org, null, null, Token))
            .Status.ShouldBe(
                SessionRouteStatus.NotFound,
                "a removed membership still holds its role row, but grants no membership:write");

        (await IsLiveAsync(localTargetTicket.SessionId)).ShouldBeFalse("the authorized call revoked the target's row");
        (await IsLiveAsync(refusedTargetTicket.SessionId)).ShouldBeTrue("the foreign-role refusal revokes nothing");
        (await IsLiveAsync(removedTargetTicket.SessionId)).ShouldBeTrue();
        (await AdminRowCountAsync(org)).ShouldBe(1L, "only the authorized call writes a row");
        (await AdminRowCountAsync(otherOrg)).ShouldBe(0L);
    }

    /// <summary>One definition, its argument list, and the names that list must keep.</summary>
    private async Task AssertOneDefinitionAsync()
    {
        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
            """)).ShouldBe(1L, "one definition, never a stale overload a grant could still reach");

        (await OwnerScalarAsync<string>(
            """
            select pg_catalog.pg_get_function_identity_arguments(p.oid)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
            """)).ShouldBe(ExpectedArguments);

        (await OwnerScalarAsync<string>(
            """
            select array_to_string(p.proargnames[1:p.pronargs], ',')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
            """)).ShouldBe(
            "p_cookie_hmacs,p_target_user_id,p_org_id,p_now,p_request_id,p_trace_id",
            "the target is an argument, the ACTOR never is: it is resolved from the cookie proof inside the definer");
    }

    /// <summary>The definer posture: owner, empty <c>search_path</c>, bounded wait, and a two-column row set.</summary>
    private async Task AssertDefinerPostureAsync()
    {
        var posture = await OwnerRowAsync(
            """
            select p.prosecdef
                 , pg_catalog.pg_get_userbyid(p.proowner)
                 , coalesce(array_to_string(p.proconfig, ','), '')
                 , p.proretset
                 , pg_catalog.pg_get_function_result(p.oid)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
            """);

        posture[0].ShouldBe(true, "a definer, so the app role reaches the owner-only session table through it");
        posture[1].ShouldBe("evidgate_owner");

        var configuration = ((string?)posture[2]).ShouldNotBeNull("the function pins its configuration");
        configuration.ShouldContain("search_path=\"\"");
        configuration.ShouldContain("lock_timeout", customMessage: "the whole call is bounded");

        posture[3].ShouldBe(true, "it returns a set: status and the count");

        var result = ((string?)posture[4]).ShouldNotBeNull("a set-returning function always reports its row type");
        result.ShouldContain("status");
        result.ShouldContain("revoked_count");
    }

    /// <summary>
    /// EXECUTE for <c>evidgate_app</c> and no other grantee, and the server's refusal for the two runtime roles
    /// that must not call it — a grant is only real when an actual attempt is refused.
    /// </summary>
    private async Task AssertOnlyTheAppRoleHoldsExecuteAsync()
    {
        (await OwnerScalarAsync<string>(
            """
            select coalesce(string_agg(
                     case when a.grantee = 0 then 'PUBLIC'
                          else pg_catalog.pg_get_userbyid(a.grantee) end
                     || ':' || a.privilege_type, ',' order by a.grantee)
                   , '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
              cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
               and a.privilege_type = 'EXECUTE'
               and a.grantee <> p.proowner
            """)).ShouldBe("evidgate_app:EXECUTE", "the app role, and never PUBLIC or another runtime role");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            (await OwnerScalarAsync<bool>(
                """
                select exists (
                  select 1
                    from pg_catalog.pg_proc p
                    join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_all_admin'
                     and pg_catalog.has_function_privilege(@role, p.oid, 'execute'))
                """,
                ("role", role))).ShouldBeFalse($"{role} holds no EXECUTE on the admin revoke-all");

            // The harness holds no password for these roles, so the switch and the attempt run on the bootstrap
            // connection, and the refusal must come from the server.
            await using var connection = await OpenAsRoleAsync(role);

            var refusal = await Should.ThrowAsync<PostgresException>(
                () => AdminRevokeAllSqlAsync(connection, null, [], Guid.CreateVersion7(), Guid.CreateVersion7(),
                    ClockAt().GetUtcNow()));

            refusal.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{role} must not run the function");
        }
    }
}
