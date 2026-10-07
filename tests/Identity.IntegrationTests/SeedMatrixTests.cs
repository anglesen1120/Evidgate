using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// AC1 / T21 / T98 / T79 / AC5: the seeded catalogue equals
/// <c>contracts/permissions.json</c>, and the documented 00 §2 matrix holds — a
/// set-difference failure on either side fails the test. Every assertion reads the
/// database through a tenant scope as the application role (never the owner), so
/// what is asserted is exactly what a member's request would see under RLS.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SeedMatrixTests : CatalogueTestBase
{
    /// <summary>The five 00 §2 member roles (the roles a membership can grant).</summary>
    private static readonly string[] MemberRoles =
        ["qa_engineer", "release_approver", "tenant_admin", "test_maintainer", "viewer"];

    /// <summary>
    /// T79: the codes a member principal must never hold — staff-only (`support:grant`,
    /// `billing:*`) and service-only (`job:claim`/`job:execute` are agent-only,
    /// `event:write` is producer-only). Pinned so a flag flip is a deliberate edit.
    /// </summary>
    private static readonly string[] NonAssignable =
        ["billing:read", "billing:write", "event:write", "job:claim", "job:execute", "support:grant"];

    public SeedMatrixTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC1: with two orgs seeded, the catalogue a tenant reads is the catalogue file —
    /// both orgs see the same rows (the catalogue is platform data, 02 §4.5), and every
    /// role's grant set equals its `permissions[]` entry in the file.
    /// </summary>
    [Fact]
    public async Task RolesMatchOverview()
    {
        var first = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var second = await Fixture.SeedOrgAsync(cancellationToken: Token);

        var firstOrg = await ReadCatalogueAsync(first);
        var secondOrg = await ReadCatalogueAsync(second);

        // Two orgs, identical catalogue: the platform rows are not org-scoped.
        Assert.Equal(firstOrg.Permissions, secondOrg.Permissions);
        Assert.Equal(firstOrg.Grants, secondOrg.Grants);

        firstOrg.Permissions.Select(row => row.Code).ShouldBe(Catalogue.Codes);

        var expected = Catalogue.Document.Permissions.ToDictionary(
            permission => permission.Code,
            permission => permission,
            StringComparer.Ordinal);

        foreach (var row in firstOrg.Permissions)
        {
            var file = expected[row.Code];
            row.Resource.ShouldBe(file.Resource);
            row.Action.ShouldBe(file.Action);
            row.Description.ShouldBe(file.Description);
            row.Sensitive.ShouldBe(file.Sensitive);
            row.Reserved.ShouldBe(file.Reserved);
            row.TenantAssignable.ShouldBe(file.TenantAssignable);
        }

        // The (role, permission) matrix itself, both directions: a missing grant and an
        // extra grant are both a set difference.
        Assert.Equal(
            Catalogue.ExpectedGrants(),
            firstOrg.Grants.Select(grant => (grant.RoleCode, grant.PermissionCode)).ToList());

        var grantsByRole = firstOrg.Grants
            .GroupBy(grant => grant.RoleCode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)[.. group.Select(grant => grant.PermissionCode).Order(StringComparer.Ordinal)],
                StringComparer.Ordinal);

        grantsByRole.Keys.Order(StringComparer.Ordinal).ShouldBe(Catalogue.RoleCodes);

        foreach (var role in Catalogue.Document.Roles)
        {
            grantsByRole[role.Code].ShouldBe(Catalogue.PermissionsOf(role.Code));
        }
    }

    /// <summary>
    /// AC1 / T21 / T98 / T79 / AC5: the generated role × permission matrix — every
    /// built-in role × every catalogue permission — equals the 00 §2-derived fixture
    /// (02.2.1-T3.1), on both halves of the enforcement path: the seeded rows read as the
    /// app role under RLS, and the real evaluator's verdict. Member roles are resolved
    /// with one <c>ResolveAsync</c> per role (never per cell); service roles are
    /// evaluated with the fixture's own claim set, bounded by the catalogue ceiling.
    /// Every mismatching cell is reported in one failure message. The fixture's provenance
    /// audit (`InferredGrants`) is pinned first, so a grant whose citation does not name its
    /// holder stays visible instead of hiding behind a citation that only names the permission.
    /// </summary>
    [Fact]
    public async Task Every_Role_Permission_Cell_Matches_The_Overview_Fixture()
    {
        var fixture = RoleMatrixFixture.Load();

        // The axes are the catalogue's, so a fixture that dropped a role or a permission
        // fails here before any cell is compared (a catalogue code missing from the
        // fixture's axis is the drift this test exists to catch).
        fixture.RoleCodes.ShouldBe(Catalogue.RoleCodes);
        fixture.PermissionCodes.ShouldBe(
            [.. Catalogue.Codes.Union(fixture.PermissionCodes, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            "the fixture's permission axis must name every code in contracts/permissions.json");

        // The provenance audit (README §Grant provenance): a grant is `inferred` when its
        // citation does not name the holder. Pin the audit's meaning — an inferred row must
        // be a real grant whose citation does not name the role, and the audit must be
        // present — so flipping a basis is a deliberate edit, not a silent one.
        fixture.InferredGrants.ShouldNotBeEmpty(
            "the fixture must mark the grants whose citations leave the holder implicit");
        foreach (var (role, permission, source) in fixture.InferredGrants)
        {
            fixture.Holds(role, permission).ShouldBeTrue(
                $"inferred grant {role} × {permission} must be a real grant of the fixture");
            source.ShouldNotContain(
                role,
                Case.Sensitive,
                $"inferred grant {role} × {permission} cites '{source}', which names the role");
        }

        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        // (a) the seeded rows, read as the app role under RLS.
        var seeded = await ReadCatalogueAsync(org);
        var seededCells = seeded.Grants
            .Select(grant => (grant.RoleCode, grant.PermissionCode))
            .ToHashSet();

        // (b) the real evaluator. Members are arranged before the scope opens (one scope
        // per async flow, 00 §5.4.2); each holds exactly one role, so one resolution per
        // role yields that role's effective set.
        var members = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var role in fixture.RoleCodes.Where(role => fixture.KindOf(role) == "member"))
        {
            members[role] = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, role, cancellationToken: Token);
        }

        var evaluator = new PermissionEvaluator(Fixture.Db);
        var evaluatorCells = new HashSet<(string Role, string Permission)>();
        var evaluatorRoles = new HashSet<string>(StringComparer.Ordinal);

        await using (var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token))
        {
            foreach (var role in fixture.RoleCodes)
            {
                // Staff roles have no evaluator path in V1: the operator principal is a
                // stub that denies until 02.8.1 ships the support grant, so they are
                // asserted on the seed only.
                IRequestPrincipal? principal = fixture.KindOf(role) switch
                {
                    "member" => RequestPrincipal.Member(members[role]),
                    "service" => RequestPrincipal.Service(projectScope: null, [.. fixture.PermissionsOf(role)]),
                    _ => null,
                };

                if (principal is null)
                {
                    continue;
                }

                evaluatorRoles.Add(role);
                foreach (var code in await evaluator.ResolveAsync(org, principal, Token))
                {
                    evaluatorCells.Add((role, code));
                }
            }
        }

        // Every cell, both halves, aggregated into one failure message.
        var mismatches = new List<string>();
        var cells = 0;

        foreach (var role in fixture.RoleCodes)
        {
            foreach (var permission in fixture.PermissionCodes)
            {
                cells++;
                var expected = fixture.Holds(role, permission);

                var seededActual = seededCells.Contains((role, permission));
                if (seededActual != expected)
                {
                    mismatches.Add($"seed: {role} × {permission}: expected {expected}, seeded {seededActual}");
                }

                if (evaluatorRoles.Contains(role))
                {
                    var evaluatorActual = evaluatorCells.Contains((role, permission));
                    if (evaluatorActual != expected)
                    {
                        mismatches.Add($"evaluator: {role} × {permission}: expected {expected}, evaluator {evaluatorActual}");
                    }
                }
            }
        }

        var expectedCells = fixture.RoleCodes.Count * fixture.PermissionCodes.Count;
        expectedCells.ShouldBeGreaterThan(0, "the matrix must not be empty");
        cells.ShouldBe(expectedCells, "every role × permission cell must be compared");
        evaluatorRoles.ShouldNotBeEmpty("the evaluator half must evaluate at least one role");

        mismatches.ShouldBeEmpty(string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// W7-SEC-03: the seeded <c>role.kind</c> column is the file's — a role's kind decides
    /// who may hold it, so a seed that dropped it (or a migration that defaulted it) would
    /// silently turn `ci_producer` or `platform_operator` into a member role. Read under
    /// RLS through the application role like every other assertion of this suite.
    /// </summary>
    [Fact]
    public async Task Role_Kinds_Match_The_Catalogue_File()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var scope = await Fixture.BeginTenantScopeAsync(org, readOnlyTransaction: true, cancellationToken: Token);

        var seeded = await scope.Db.QueryAsync<RoleKindRow>(
            "select code, kind from public.role where org_id is null order by code",
            cancellationToken: Token);

        seeded.Select(row => $"{row.Code}:{row.Kind}")
            .ShouldBe([.. Catalogue.Document.Roles
                .Select(role => $"{role.Code}:{role.Kind}")
                .Order(StringComparer.Ordinal)]);

        // All three kinds are exercised by the seed.
        seeded.Select(row => row.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .ShouldBe(["member", "service", "staff"]);

        seeded.Where(row => row.Kind == "member").Select(row => row.Code).Order(StringComparer.Ordinal)
            .ShouldBe(MemberRoles.Order(StringComparer.Ordinal), "the member-kind roles are exactly the roles a membership may hold");
    }

    /// <summary>
    /// AC1's documented facts, asserted on the seeded rows: the 00 §2 role→permission
    /// matrix as the plans state it (T21 for `project:*`, T98 for the two roles the org
    /// creator receives, T59 for the catalogue additions, T79 for the non-assignable
    /// codes).
    /// </summary>
    [Fact]
    public async Task Documented_Role_Grants_Hold()
    {
        var catalogue = await ReadCatalogueAsync(await Fixture.SeedOrgAsync(cancellationToken: Token));
        var holders = Holders(catalogue);

        // T21: project:read on every member role; project:write and project:create on
        // tenant_admin only.
        holders["project:read"].ShouldBe(MemberRoles.Order(StringComparer.Ordinal));
        holders["project:write"].ShouldBe(["tenant_admin"]);
        holders["project:create"].ShouldBe(["tenant_admin"]);

        // 00 §2 healing strings.
        holders["healing:triage"].ShouldBe(["qa_engineer"]);
        holders["healing:approve"].ShouldBe(["test_maintainer"]);
        holders["healing:revoke"].ShouldBe(["test_maintainer"]);
        holders["healing:configure"].ShouldBe(["tenant_admin"]);

        // T58/T59: policy:read for every member role, policy:write for release_approver.
        holders["policy:read"].ShouldBe(MemberRoles.Order(StringComparer.Ordinal));
        holders["policy:write"].ShouldBe(["release_approver"]);

        // 00 §2 staff roles.
        holders["billing:read"].ShouldBe(["billing_operator"]);
        holders["billing:write"].ShouldBe(["billing_operator"]);
        holders["support:grant"].ShouldBe(["platform_operator"]);
        holders["support:approve"].ShouldBe(["tenant_admin"]);
        holders["usage:read"].ShouldBe(["tenant_admin"]);
        holders["security_event:read"].ShouldBe(["tenant_admin"]);

        // FR-02-7/AC4: the ci_producer set is exactly these three.
        catalogue.Grants
            .Where(grant => grant.RoleCode == "ci_producer")
            .Select(grant => grant.PermissionCode)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["artifact:write", "event:write", "run:create"]);

        // T98: the two roles the org creator receives (02.1.1's POST /v1/orgs) are
        // built-in roles this seed already carries — org creation needs no catalogue
        // change, and both are seeded with a non-empty grant set.
        var seededRoles = catalogue.Grants.Select(grant => grant.RoleCode).Distinct(StringComparer.Ordinal).ToList();
        seededRoles.ShouldContain("tenant_admin");
        seededRoles.ShouldContain("release_approver");
    }

    /// <summary>
    /// T79: `tenant_assignable = false` is exactly the staff/service-only set, and no
    /// member-kind role holds one — the invariant the evaluator's member path filters
    /// on (02.2.1-T2), asserted here on the seed.
    /// </summary>
    [Fact]
    public async Task NonAssignable_Codes_Are_Held_By_No_Member_Role()
    {
        var catalogue = await ReadCatalogueAsync(await Fixture.SeedOrgAsync(cancellationToken: Token));
        var holders = Holders(catalogue);

        catalogue.Permissions
            .Where(permission => !permission.TenantAssignable)
            .Select(permission => permission.Code)
            .Order(StringComparer.Ordinal)
            .ShouldBe(NonAssignable.Order(StringComparer.Ordinal));

        var memberRoles = Catalogue.Document.Roles
            .Where(role => role.Kind == "member")
            .Select(role => role.Code)
            .ToHashSet(StringComparer.Ordinal);

        memberRoles.Order(StringComparer.Ordinal).ShouldBe(MemberRoles.Order(StringComparer.Ordinal));

        foreach (var code in NonAssignable)
        {
            holders[code].ShouldNotContain(
                holder => memberRoles.Contains(holder),
                $"'{code}' is not tenant-assignable (T79), so no member role may hold it");
        }
    }

    /// <summary>
    /// AC5 (the seed half): a `sensitive` permission is held only by the roles the
    /// 00 §2-derived fixture grants it to — never by `viewer` (00 §2 "No secrets") and
    /// never by a service identity. The allowed-holder set is the fixture's, not a second
    /// hand-maintained list. The full AC5 (that no catalogue permission exposes a secret
    /// value) is 02.2.1-T3's.
    /// </summary>
    [Fact]
    public async Task Sensitive_Permissions_Are_Not_Held_By_Unprivileged_Roles()
    {
        var fixture = RoleMatrixFixture.Load();
        var catalogue = await ReadCatalogueAsync(await Fixture.SeedOrgAsync(cancellationToken: Token));
        var holders = Holders(catalogue);

        var sensitive = catalogue.Permissions
            .Where(permission => permission.Sensitive)
            .Select(permission => permission.Code)
            .ToList();

        sensitive.ShouldNotBeEmpty("the catalogue marks secret-adjacent permissions (AC5)");

        // The allowed-holder set is the union of the roles the fixture grants a sensitive
        // code to (00 §2-derived), so a new sensitive grant in the plans widens it without
        // a second list to maintain.
        var allowed = sensitive
            .SelectMany(code => fixture.HoldersOf(code))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var code in sensitive)
        {
            holders[code].ShouldNotContain("viewer", $"'{code}' is sensitive and viewer reads no secrets (00 §2)");
            holders[code].ShouldBeSubsetOf(allowed);
        }

        // The service identities and viewer hold nothing sensitive, as a set.
        var unprivileged = new[] { "viewer", "ci_producer", "agent" };
        var theirGrants = catalogue.Grants
            .Where(grant => unprivileged.Contains(grant.RoleCode, StringComparer.Ordinal))
            .Select(grant => grant.PermissionCode)
            .ToList();

        theirGrants.ShouldNotContain(code => sensitive.Contains(code, StringComparer.Ordinal));
    }

    /// <summary>Reads the catalogue and the role grants through one tenant scope (the app role, under RLS).</summary>
    private async Task<CatalogueRead> ReadCatalogueAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        var permissions = await scope.Db.QueryAsync<PermissionView>(
            """
            select code,
                   resource,
                   action,
                   description,
                   sensitive,
                   reserved,
                   tenant_assignable
              from public.permission
             order by code
            """,
            cancellationToken: Token);

        var grants = await scope.Db.QueryAsync<RoleGrantRow>(
            """
            select r.code as role_code,
                   rp.permission_code as permission_code
              from public.role r
              join public.role_permission rp on rp.role_id = r.id
             order by r.code,
                      rp.permission_code
            """,
            cancellationToken: Token);

        return new CatalogueRead(permissions, grants);
    }

    /// <summary>The holder role set of each permission code, ordinal order.</summary>
    private static Dictionary<string, IReadOnlyList<string>> Holders(CatalogueRead catalogue) =>
        catalogue.Permissions.ToDictionary(
            permission => permission.Code,
            permission =>
                (IReadOnlyList<string>)
                [
                    .. catalogue.Grants
                        .Where(grant => string.Equals(grant.PermissionCode, permission.Code, StringComparison.Ordinal))
                        .Select(grant => grant.RoleCode)
                        .Order(StringComparer.Ordinal),
                ],
            StringComparer.Ordinal);

    /// <summary>The catalogue and its grants as one tenant read them.</summary>
    private sealed record CatalogueRead(
        IReadOnlyList<PermissionView> Permissions,
        IReadOnlyList<RoleGrantRow> Grants);

    /// <summary>One `(code, kind)` pair of the seeded built-in roles (W7-SEC-03).</summary>
    private sealed record RoleKindRow(string Code, string Kind);

    /// <summary>One `role_permission` row joined to its role code.</summary>
    private sealed record RoleGrantRow(string RoleCode, string PermissionCode);
}
