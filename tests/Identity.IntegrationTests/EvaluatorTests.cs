using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// 02.2.1-T2.2 against real PostgreSQL and the migrated catalogue: the member path
/// (one statement, RLS-scoped, <c>tenant_assignable</c>-filtered per TL T79), the
/// service-identity path (the claim set must be a subset of the catalogue), the
/// operator stub for 02.8.1, and the P3 budget. The evaluator is the module's own
/// class over the harness's production Dapper path, so the SQL under test is the SQL
/// the Api and the Worker run.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvaluatorTests : CatalogueTestBase
{
    /// <summary>A project uuid the service-principal cases scope their tokens to.</summary>
    private static readonly Guid OtherProject = Guid.Parse("55555555-5555-7555-8555-555555555555");

    public EvaluatorTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// FR-02-2 / AC3: an active member whose built-in role carries the permission is
    /// granted — the happy path of the single join (membership → membership_role →
    /// role → role_permission).
    /// </summary>
    [Fact]
    public async Task Member_With_The_Grant_Is_Granted()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);

        var decision = await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), Permissions.Project.Read);

        decision.IsGranted.ShouldBeTrue();

        // The same member is NOT granted what `viewer` does not hold: the roles are
        // data, so the difference between the two codes is the seed, not the code.
        var refused = await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), Permissions.Membership.Write);

        refused.DenyCode.ShouldBe(ApiErrorCode.Forbidden);
    }

    /// <summary>
    /// AC7 / T79: a <c>role_permission</c> row corrupted to carry a non-assignable
    /// code grants nothing to a member — the read-time filter, proven by asserting the
    /// row is really there.
    /// </summary>
    [Fact]
    public async Task NonAssignable_Code_Is_Never_Granted()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var viewerRoleId = await IdentityTestData.RoleIdAsync(Fixture, org, "viewer", Token);

        await IdentityTestData.GrantAsync(Fixture, org, viewerRoleId, Permissions.Billing.Read, Token);

        (await IdentityTestData.RolePermissionsAsync(Fixture, org, viewerRoleId, Token))
            .ShouldContain(Permissions.Billing.Read, "the corrupt grant must exist for this test to mean anything");

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var decision = await EvaluateAsync(
            new PermissionEvaluator(Fixture.Db),
            org,
            RequestPrincipal.Member(userId),
            Permissions.Billing.Read);

        decision.IsGranted.ShouldBeFalse();
        decision.DenyCode.ShouldBe(ApiErrorCode.Forbidden);
    }

    /// <summary>
    /// 02.1.2 AC2 / 02.3.1 AC3: a caller with no active membership in the org is
    /// <c>no_membership</c>, not <c>forbidden</c> — the client's fix is an invite, not
    /// a role change.
    /// </summary>
    [Fact]
    public async Task Member_Without_An_Active_Membership_Is_NoMembership()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        // Both members are arranged before the scope opens: one scope per async
        // flow (00 §5.4.2), and the harness's seeding seam opens none of its own.
        var removed = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, org, "viewer", status: "removed", cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);

        var unknown = await EvaluateAsync(evaluator, org, RequestPrincipal.Member(Guid.CreateVersion7()), Permissions.Project.Read);

        unknown.DenyCode.ShouldBe(ApiErrorCode.NoMembership);

        var removedDecision = await EvaluateAsync(evaluator, org, RequestPrincipal.Member(removed), Permissions.Project.Read);

        removedDecision.DenyCode.ShouldBe(ApiErrorCode.NoMembership);
    }

    /// <summary>
    /// T2 isolation: an active membership of <b>another</b> org grants nothing here — the
    /// single statement is scoped to <c>@org_id</c> on top of RLS, so the verdict is
    /// <c>no_membership</c> and the permission the foreign <c>tenant_admin</c> role holds
    /// is never granted. Membership is per org, and the request's org comes from the
    /// tenant context, never from input.
    /// </summary>
    [Fact]
    public async Task Member_Of_Another_Org_Is_NoMembership()
    {
        var orgA = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var orgB = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, orgA, "tenant_admin", cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(orgB, cancellationToken: Token);

        var decision = await EvaluateAsync(
            new PermissionEvaluator(Fixture.Db),
            orgB,
            RequestPrincipal.Member(userId),
            Permissions.Role.Write);

        decision.IsGranted.ShouldBeFalse();
        decision.DenyCode.ShouldBe(ApiErrorCode.NoMembership);
    }

    /// <summary>
    /// AC3 / FR-02-2: a permission the catalogue does not declare is denied — and
    /// denied without a round trip, because no row could ever grant it.
    /// </summary>
    [Fact]
    public async Task Unknown_Permission_Is_Denied()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var counting = new CountingTenantDb(Fixture.Db);
        var decision = await EvaluateAsync(
            new PermissionEvaluator(counting),
            org,
            RequestPrincipal.Member(userId),
            "not_a:permission");

        decision.DenyCode.ShouldBe(ApiErrorCode.Forbidden);
        counting.Statements.ShouldBe(0, "an unknown code cannot be granted by any row, so it must not read one");
    }

    /// <summary>
    /// P3: permission resolution costs exactly one SQL statement per evaluation and
    /// never an N+1, whatever the answer is.
    /// </summary>
    [Fact]
    public async Task Single_Statement_Per_Member_Evaluation()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var counting = new CountingTenantDb(Fixture.Db);
        var evaluator = new PermissionEvaluator(counting);

        (await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), Permissions.Project.Read))
            .IsGranted.ShouldBeTrue();
        counting.Statements.ShouldBe(1);

        (await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), Permissions.Role.Write))
            .DenyCode.ShouldBe(ApiErrorCode.Forbidden);
        counting.Statements.ShouldBe(2);

        (await EvaluateAsync(evaluator, org, RequestPrincipal.Member(Guid.CreateVersion7()), Permissions.Project.Read))
            .DenyCode.ShouldBe(ApiErrorCode.NoMembership);
        counting.Statements.ShouldBe(3);
    }

    /// <summary>
    /// AC4 / FR-02-7: a service token is evaluated through the same behaviour. It
    /// holds exactly what it claims, and a claim outside the catalogue invalidates the
    /// whole set rather than being ignored.
    /// </summary>
    [Fact]
    public async Task Service_Principal_Is_Bounded_By_Its_Claims_And_The_Catalogue()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);

        var producer = RequestPrincipal.Service(
            projectScope: null,
            [Permissions.Run.Create, Permissions.Event.Write, Permissions.Artifact.Write]);

        (await EvaluateAsync(evaluator, org, producer, Permissions.Run.Create)).IsGranted.ShouldBeTrue();

        // AC4's case: `ci_producer` may not write membership.
        (await EvaluateAsync(evaluator, org, producer, Permissions.Membership.Write))
            .DenyCode.ShouldBe(ApiErrorCode.Forbidden);

        var poisoned = RequestPrincipal.Service(projectScope: null, [Permissions.Run.Create, "not_a:permission"]);

        (await EvaluateAsync(evaluator, org, poisoned, Permissions.Run.Create))
            .DenyCode.ShouldBe(ApiErrorCode.Forbidden);

        // Evaluating a service token costs no statement: the claims are in the token.
        var counting = new CountingTenantDb(Fixture.Db);
        (await EvaluateAsync(new PermissionEvaluator(counting), org, producer, Permissions.Run.Create))
            .IsGranted.ShouldBeTrue();
        counting.Statements.ShouldBe(0);
    }

    /// <summary>The evaluator's service ceiling is exactly the union of the catalogue's service roles.</summary>
    [Fact]
    public void ServiceRoleCeiling_MatchesTheCatalogue()
    {
        var catalogue = PermissionCatalogueFile.Load().Document;
        var union = catalogue.Roles
            .Where(role => role.Kind == "service")
            .SelectMany(role => role.Permissions)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        PermissionEvaluator.ServiceRoleCeiling.Order(StringComparer.Ordinal).ToList().ShouldBe(union);
    }

    /// <summary>
    /// 02.6.2 AC6 / W7-SEC-06: a service token never holds a staff-only code. A claim of
    /// <c>billing:*</c> or <c>support:*</c> is denied, and it invalidates the token's
    /// other claims too (a malformed token holds nothing), on both the decision path and
    /// the permission-set path.
    /// </summary>
    [Fact]
    public async Task ServicePrincipal_StaffOnlyCode_Denied()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);

        foreach (var staffCode in new[] { Permissions.Billing.Read, Permissions.Billing.Write, Permissions.Support.Grant })
        {
            var token = RequestPrincipal.Service(projectScope: null, [Permissions.Run.Create, staffCode]);

            (await EvaluateAsync(evaluator, org, token, staffCode)).DenyCode.ShouldBe(ApiErrorCode.Forbidden);
            (await EvaluateAsync(evaluator, org, token, Permissions.Run.Create)).DenyCode.ShouldBe(ApiErrorCode.Forbidden);
            (await evaluator.ResolveAsync(org, token, Token)).ShouldBeEmpty();
        }

        // 02.6.2 AC6: nothing outside the service roles either — a service token cannot
        // claim tenant-admin codes, even though they are valid catalogue codes.
        foreach (var tenantCode in new[]
                 {
                     Permissions.Membership.Write, Permissions.Role.Write, Permissions.Organization.Write, Permissions.Project.Read,
                 })
        {
            var overreach = RequestPrincipal.Service(projectScope: null, [Permissions.Run.Create, tenantCode]);

            (await EvaluateAsync(evaluator, org, overreach, tenantCode)).DenyCode.ShouldBe(ApiErrorCode.Forbidden);
            (await EvaluateAsync(evaluator, org, overreach, Permissions.Run.Create)).DenyCode.ShouldBe(ApiErrorCode.Forbidden);
        }

        // The tenant-facing codes of the service roles are unaffected.
        var producer = RequestPrincipal.Service(projectScope: null, [Permissions.Run.Create]);
        (await EvaluateAsync(evaluator, org, producer, Permissions.Run.Create)).IsGranted.ShouldBeTrue();
    }

    /// <summary>
    /// 00 §9.1 / FR-02-2: the project dimension is evaluated against the token's
    /// claim, and a request outside it is <c>project_scope_mismatch</c>.
    /// </summary>
    [Fact]
    public async Task Cross_Project_Is_Denied()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var token = RequestPrincipal.Service(OtherProject, [Permissions.Run.Create]);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, projectId: project, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);

        var mismatched = await evaluator.EvaluateAsync(
            Permissions.Run.Create,
            token,
            new RequestTenantContext(org, project),
            Token);

        mismatched.DenyCode.ShouldBe(ApiErrorCode.ProjectScopeMismatch);

        var matching = await evaluator.EvaluateAsync(
            Permissions.Run.Create,
            RequestPrincipal.Service(project, [Permissions.Run.Create]),
            new RequestTenantContext(org, project),
            Token);

        matching.IsGranted.ShouldBeTrue();
    }

    /// <summary>
    /// 02 §5: the operator path is a fail-closed stub until 02.8.1 ships the
    /// tenant-approved, expiring <c>support_grant</c> — never an implicit allow.
    /// </summary>
    [Fact]
    public async Task Operator_Is_Denied_Until_Support_Grants_Land()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);

        var decision = await EvaluateAsync(
            new PermissionEvaluator(Fixture.Db),
            org,
            RequestPrincipal.Operator(),
            Permissions.Project.Read);

        decision.DenyCode.ShouldBe(ApiErrorCode.Forbidden);
    }

    /// <summary>
    /// W7-SEC-03: a staff role attached to a membership row grants nothing — the member
    /// join requires <c>r.kind = 'member'</c>. The row is arranged as the owner because
    /// the commands now refuse such a role (409 <c>unknown_role</c>), and the case is
    /// about the read side: <c>platform_operator</c> is visible to every tenant and
    /// carries <c>support:read</c>, an assignable code, so without the kind filter the
    /// database layer would have no answer to a hand-written row. The viewer grant is
    /// evaluated too, so the test cannot pass because the member holds nothing at all.
    /// </summary>
    [Fact]
    public async Task Staff_Role_Attached_To_A_Membership_Grants_Nothing()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var staffRoleId = await IdentityTestData.RoleIdAsync(Fixture, org, "platform_operator", Token);

        await IdentityTestData.GrantRoleToMemberAsync(Fixture, org, userId, staffRoleId, Token);

        (await IdentityTestData.RolePermissionsAsync(Fixture, org, staffRoleId, Token))
            .ShouldContain(Permissions.Support.Read, "the staff role must really carry the code for this test to mean anything");

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);

        var staffCode = await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), Permissions.Support.Read);

        staffCode.IsGranted.ShouldBeFalse("a `staff`-kind role is not a role a membership can hold (W7-SEC-03)");
        staffCode.DenyCode.ShouldBe(ApiErrorCode.Forbidden);

        (await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), Permissions.Project.Read))
            .IsGranted.ShouldBeTrue("the membership's own viewer role still grants, so the refusal above is the kind filter");
    }

    /// <summary>
    /// W7-SEC-05: an org-scoped <c>role_permission</c> row forged onto a built-in role
    /// grants nothing — the member join requires <c>rp.org_id is not distinct from
    /// r.org_id</c>, and a built-in role's own <c>org_id</c> is null. Without the tie the
    /// row would widen <c>viewer</c> — the role every member of every org shares — with a
    /// code as strong as <c>role:write</c>, for this tenant's members. The policy half of
    /// the rule (<c>CatalogueRlsTests</c>) now refuses the row at write time; this case
    /// pins the evaluator's own answer for a row that is already there.
    /// </summary>
    [Fact]
    public async Task Org_Scoped_Grant_On_A_Builtin_Role_Grants_Nothing()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var viewerRoleId = await IdentityTestData.RoleIdAsync(Fixture, org, "viewer", Token);

        await IdentityTestData.GrantAsync(Fixture, org, viewerRoleId, Permissions.Role.Write, Token);

        (await IdentityTestData.RolePermissionsAsync(Fixture, org, viewerRoleId, Token))
            .ShouldContain(Permissions.Role.Write, "the forged grant must exist for this test to mean anything");

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);

        var forged = await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), Permissions.Role.Write);

        forged.IsGranted.ShouldBeFalse("a grant row must live in its role's own org (W7-SEC-05)");
        forged.DenyCode.ShouldBe(ApiErrorCode.Forbidden);

        (await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), Permissions.Project.Read))
            .IsGranted.ShouldBeTrue("the viewer role's own grants still apply");
    }

    /// <summary>
    /// W7-SEC-02: <see cref="PermissionEvaluator"/> answers <c>ICallerPermissionSet</c>
    /// with the caller's effective set — the same rows the per-permission verdict reads
    /// (one statement, P3) — while a caller without an active membership and the operator
    /// stub hold nothing, so a write command's self-escalation rule fails closed.
    /// </summary>
    [Fact]
    public async Task Caller_Permission_Set_Matches_The_Per_Permission_Verdict()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "qa_engineer", cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var counting = new CountingTenantDb(Fixture.Db);
        var evaluator = new PermissionEvaluator(counting);

        var set = await evaluator.ResolveAsync(org, RequestPrincipal.Member(userId), Token);

        counting.Statements.ShouldBe(1, "the whole set is one statement (P3), never one per code");

        foreach (var code in set)
        {
            (await EvaluateAsync(evaluator, org, RequestPrincipal.Member(userId), code))
                .IsGranted.ShouldBeTrue($"'{code}' is in the resolved set, so the verdict must grant it");
        }

        set.ShouldContain(Permissions.Artifact.ReadSensitive);
        set.ShouldNotContain(Permissions.Billing.Read, "a non-assignable code is never granted to a member (T79)");
        set.ShouldNotContain(Permissions.Support.Read, "no staff-kind role may reach a membership (W7-SEC-03)");

        (await evaluator.ResolveAsync(org, RequestPrincipal.Member(Guid.CreateVersion7()), Token))
            .ShouldBeEmpty("a caller with no active membership holds nothing");

        (await evaluator.ResolveAsync(org, RequestPrincipal.Operator(), Token))
            .ShouldBeEmpty("the operator stub holds nothing until 02.8.1");

        var malformed = RequestPrincipal.Service(projectScope: null, [Permissions.Run.Create, "not_a:permission"]);

        (await EvaluateAsync(evaluator, org, malformed, Permissions.Run.Create))
            .IsGranted.ShouldBeFalse("one unknown claim makes the token malformed for the verdict");
        (await evaluator.ResolveAsync(org, malformed, Token))
            .ShouldBeEmpty("the set must agree with the verdict: a malformed token holds nothing");

        var producer = RequestPrincipal.Service(projectScope: null, [Permissions.Run.Create]);

        (await evaluator.ResolveAsync(org, producer, Token))
            .ShouldBe([Permissions.Run.Create], ignoreOrder: true, "a well-formed token holds exactly its claims");
    }

    private static ValueTask<PermissionDecision> EvaluateAsync(
        PermissionEvaluator evaluator,
        Guid org,
        IRequestPrincipal principal,
        string permission) =>
        evaluator.EvaluateAsync(permission, principal, new RequestTenantContext(org, null), Token);
}
