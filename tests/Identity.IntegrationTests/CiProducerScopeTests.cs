using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// AC4 / 02.4.1 AC7 (the hand-over to 02.2.1-T3): a <c>ci_producer</c> service identity is
/// evaluated through the same C2 behaviour as a member — its token claims are bounded by
/// the catalogue, it may call only the routes its permission set covers, and a token
/// scoped to one project is refused on another project's request.
/// </summary>
/// <remarks>
/// <para>
/// The principal is <see cref="RequestPrincipal.Service"/> carrying exactly
/// <see cref="RoleMatrixFixture.PermissionsOf"/> of <c>ci_producer</c> (the 00 §2-derived
/// expectation, never the catalogue file), with the tenant published for the seeded org and
/// project. The route set is the served table, so a new endpoint is covered the moment it
/// ships.
/// </para>
/// <para>
/// The spec's <c>authenticated</c> operations (<c>GET /v1/orgs</c>, <c>POST /v1/orgs</c>)
/// are a USER session (<c>x-auth: user_session</c>), so a service token is refused there
/// too — asserted, not excluded. Only the <c>none</c> operations are excluded, each with a
/// documented reason.
/// </para>
/// <para>
/// <see cref="Real_Minted_CiProducer_Token_Is_403_On_Membership_Write"/> exercises the same
/// rule through a REAL minted token: an ingest token created on the admin route, exchanged
/// at <c>POST /v1/ci/token</c>, presented as <c>Authorization: Bearer</c>. The stand-in
/// member of <see cref="CiProducerJwtFactory"/> is published only for requests without a
/// bearer, so the real <c>CiProducerAuthenticator</c> owns the bearer request — the
/// <see cref="IdentityApiFactory"/> middleware always publishes a principal and would
/// therefore never authenticate one.
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class CiProducerScopeTests(DatabaseFixture fixture) : CatalogueTestBase(fixture)
{
    /// <summary>The explicit route of the hand-over: a <c>ci_producer</c> calling <c>membership:write</c>.</summary>
    private static readonly ServedRoute MembershipWrite = new("POST", "/v1/orgs/{o}/memberships", "membership:write");

    /// <summary>The one served route whose spec code is in the <c>ci_producer</c> set (<c>run:create</c>).</summary>
    private static readonly ServedRoute ManifestUpload = new("POST", "/v1/projects/{p}/manifests", "run:create");

    /// <summary>
    /// The served routes the ci_producer matrix excludes, each with the reason it carries no
    /// catalogue permission a service token could be denied for. The observed exclusion set
    /// is DERIVED from the route data (<see cref="ServedRouteTable.IsUnpermissioned"/>) and
    /// asserted equal to this documented list.
    /// </summary>
    private static readonly Dictionary<(string Method, string Template), string> Excluded =
        new()
        {
            [("POST", "/v1/ci/token")] =
                "x-permission is 'none' (the pre-tenant credential exchange itself, 02.4.1)",
            [("GET", "/v1/auth/oidc/{provider}/start")] =
                "x-permission is 'none' (pre-tenant login start, 02.3.1-T1.1: no tenant, no principal, no session yet)",
            [("GET", "/v1/auth/oidc/{provider}/callback")] =
                "x-permission is 'none' (pre-tenant login callback, 02.3.1-T1.1: the browser holds only the pending-login cookie)",
            [("POST", "/v1/auth/invite-token")] =
                "x-permission is 'none' (pre-tenant invite staging, 02.3.1-T2: no session, no principal, no tenant yet)",
            [("POST", "/v1/auth/link/{provider}")] =
                "x-permission is 'none' (the link start acts only on the caller's own session cookie, 02.3.1-T2: no catalogue permission exists for it)",
            [("GET", "/v1/auth/link/pending")] =
                "x-permission is 'none' (the staged link is the caller's own session state, 02.3.1-T2: no catalogue permission exists for it)",
            [("DELETE", "/v1/auth/link/pending")] =
                "x-permission is 'none' (discarding the caller's own staged link, 02.3.1-T2: no catalogue permission exists for it)",
            [("POST", "/v1/auth/link/confirm")] =
                "x-permission is 'none' (confirming the caller's own staged link, 02.3.1-T2: it writes no tenant row and holds no catalogue permission)",
            [("GET", "/v1/auth/sessions")] =
                "x-permission is 'none' (the caller's own live sessions, resolved from the session cookie, 02.3.2-T2: no catalogue permission exists for a pre-tenant read)",
            [("DELETE", "/v1/auth/sessions/{s}")] =
                "x-permission is 'none' (revoking a session acts on the caller's own credential; the admin path needs membership:write, checked inside the definer, 02.3.2-T2)",
            [("POST", "/v1/auth/sessions/revoke-all")] =
                "x-permission is 'none' (revoking the caller's own sessions, 02.3.2-T2: no catalogue permission exists for it)",
            [("POST", "/v1/auth/logout")] =
                "x-permission is 'none' (logout revokes the caller's own session row, 02.3.2-T2: no catalogue permission exists for it)",
            [("POST", "/v1/slice/run")] =
                "01.1.2 run-only slice dev surface, absent from the spec (ServedRouteTable.ServedNotInSpec)",
        };

    /// <summary>AC4: a valid <c>ci_producer</c> token calling <c>membership:write</c> is 403 <c>forbidden</c>.</summary>
    [Fact]
    public async Task Membership_Write_Is_403()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                ProjectId: project,
                Principal: RequestPrincipal.Service(project, matrix.PermissionsOf("ci_producer"))));
        using var client = factory.CreateClient();

        var served = ServedRouteTable.From(factory.Services);
        var route = served.Single(candidate => candidate == MembershipWrite);

        using var response = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(route, RouteRequestContext.Fresh(org, project)),
            Token);

        var violation = await RouteProbe.ForbiddenViolationAsync(route, response, Token);
        violation.ShouldBeNull();
    }

    /// <summary>
    /// AC4: over every served route a service token can be judged on, a <c>ci_producer</c>
    /// token is refused with 403 <c>forbidden</c> unless the code is in its set — and the
    /// route it does cover is not refused. The spec's <c>authenticated</c> operations are
    /// refused too: they are a user session, not a service token. Every branch is asserted
    /// non-empty, and the exclusion set is derived from the route data, never hand-picked.
    /// </summary>
    [Fact]
    public async Task Every_Served_Route_Respects_The_CiProducer_Set()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();
        var ciProducer = matrix.PermissionsOf("ci_producer").ToHashSet(StringComparer.Ordinal);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                ProjectId: project,
                Principal: RequestPrincipal.Service(project, matrix.PermissionsOf("ci_producer"))));
        using var client = factory.CreateClient();

        var served = ServedRouteTable.From(factory.Services);
        // 'authenticated' routes stay in the checked set (S0): a service token is refused
        // there, so only 'none' and served-not-in-spec routes are excluded.
        static bool IsExcluded(ServedRoute route) =>
            ServedRouteTable.IsUnpermissioned(route)
            && !string.Equals(route.Permission, ServedRouteTable.Authenticated, StringComparison.Ordinal);

        var excluded = served.Where(IsExcluded).ToList();
        var checkedRoutes = served.Where(route => !IsExcluded(route)).ToList();

        // The exclusion list is explicit and asserted: exactly the routes named above are
        // excluded, and the checked count is the served count minus them.
        excluded.Select(route => $"{route.Method} {route.Template}")
            .Order(StringComparer.Ordinal)
            .ToList()
            .ShouldBe(
                Excluded.Keys
                    .Select(key => $"{key.Method} {key.Template}")
                    .Order(StringComparer.Ordinal)
                    .ToList(),
                Case.Sensitive,
                "the exclusion set derived from the route data must equal the documented one");
        checkedRoutes.Count.ShouldBe(served.Count - Excluded.Count);
        checkedRoutes.ShouldNotBeEmpty();

        // Every exclusion carries a reason, and the reason names the route's own
        // classification: the x-permission it carries, or the allowlist that records it.
        foreach (var route in excluded)
        {
            var reason = Excluded[(route.Method, route.Template)];
            reason.ShouldNotBeNullOrWhiteSpace($"{route} is excluded without a documented reason");

            if (ServedRouteTable.NotInSpecReason(route) is { } recorded)
            {
                recorded.ShouldNotBeNullOrWhiteSpace($"{route} is allowlisted in ServedNotInSpec without a reason");
                reason.ShouldContain(
                    "ServedNotInSpec",
                    Case.Sensitive,
                    $"{route} is absent from the spec, so its reason must cite the allowlist that records it");
            }
            else
            {
                reason.ShouldContain(
                    route.Permission,
                    Case.Sensitive,
                    $"{route} carries x-permission '{route.Permission}', so its reason must name it");
            }
        }

        var context = RouteRequestContext.Fresh(org, project);
        var violations = new List<string>();
        var granted = 0;
        var denied = 0;
        var authenticated = 0;

        foreach (var route in checkedRoutes)
        {
            using var response = await RouteProbe.SendAsync(client, RouteRequests.Build(route, context), Token);

            if (string.Equals(route.Permission, ServedRouteTable.Authenticated, StringComparison.Ordinal))
            {
                // S0: the spec's 'authenticated' is a user session (x-auth: user_session), so
                // a service token is refused — the same 403 forbidden the delegate and the
                // [AuthenticatedMember] stage answer.
                authenticated++;
                var violation = await RouteProbe.ForbiddenViolationAsync(route, response, Token);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }
            else if (ciProducer.Contains(route.Permission))
            {
                granted++;
                var violation = await RouteProbe.NotForbiddenViolationAsync(route, response, Token);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }
            else
            {
                denied++;
                var violation = await RouteProbe.ForbiddenViolationAsync(route, response, Token);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }
        }

        authenticated.ShouldBeGreaterThan(0, "the host serves 'authenticated' routes a service token must be refused on");
        granted.ShouldBeGreaterThan(0, "the ci_producer set must cover at least one served route");
        denied.ShouldBeGreaterThan(0, "the ci_producer set must not cover every served route");
        (authenticated + granted + denied).ShouldBe(checkedRoutes.Count);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// FR-02-2 / AC3: the EVALUATOR branch of the scope rule — a token scoped to project A
    /// while the host publishes project B (same org) is 403 <c>project_scope_mismatch</c> on
    /// every route its permission set covers, and no row of the org changes.
    /// </summary>
    /// <remarks>
    /// This pins <c>PermissionEvaluator.Service</c> (the token's project claim against the
    /// request's tenant project), not the handler's own route-vs-scope guard: the tenant is
    /// published directly, so the request's project IS B and only the claim disagrees. The
    /// handler guard is the real-token counterpart's subject
    /// (<see cref="Real_Minted_Token_For_Project_A_On_Project_B_Is_Refused"/>).
    /// </remarks>
    [Fact]
    public async Task CrossProjectDenied()
    {
        var (org, projectB) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var projectA = Guid.CreateVersion7();
        var matrix = RoleMatrixFixture.Load();
        var ciProducer = matrix.PermissionsOf("ci_producer").ToHashSet(StringComparer.Ordinal);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                ProjectId: projectB,
                Principal: RequestPrincipal.Service(projectA, matrix.PermissionsOf("ci_producer"))));
        using var client = factory.CreateClient();

        var served = ServedRouteTable.From(factory.Services);
        var scoped = served.Where(route => ciProducer.Contains(route.Permission)).ToList();
        scoped.ShouldNotBeEmpty("the ci_producer set must cover at least one served route");

        var context = RouteRequestContext.Fresh(org, projectB);
        var before = await OrgRowCounter.CountAsync(Fixture, org, Token);

        var violations = new List<string>();
        foreach (var route in scoped)
        {
            using var response = await RouteProbe.SendAsync(client, RouteRequests.Build(route, context), Token);
            var violation = await RouteProbe.DeniedViolationAsync(route, response, ApiErrorCode.ProjectScopeMismatch, Token);
            if (violation is not null)
            {
                violations.Add(violation);
            }
        }

        var after = await OrgRowCounter.CountAsync(Fixture, org, Token);
        after.ShouldBe(before, "a scope-mismatched write must not change the org's rows");
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// W7-SEC-06 / 02.6.2 AC6: a token claiming a code outside the service ceiling — a
    /// staff code, a member code, or a string the catalogue does not declare — is malformed,
    /// so it holds nothing, not even the <c>run:create</c> it also claims.
    /// </summary>
    /// <remarks>
    /// One host serves every claim set (P0): the principal is published per request from
    /// <see cref="ServicePrincipalProbeFactory.ClaimsHeader"/>, so the claims do not each
    /// boot the app. The route is the served one the <c>ci_producer</c> set covers, so a
    /// refusal here can only come from the malformed claim set.
    /// </remarks>
    [Fact]
    public async Task OverReach_Claims_Are_403_Forbidden()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();
        var held = matrix.PermissionsOf("ci_producer");
        var context = RouteRequestContext.Fresh(org, project);

        await using var factory = ServicePrincipalProbeFactory.Start(Fixture, org, project);
        using var client = factory.CreateClient();

        var violations = new List<string>();
        foreach (var overreach in new[] { "billing:read", "support:grant", "membership:write", "not_a:catalogue_code" })
        {
            using var response = await RouteProbe.SendAsync(
                client,
                RouteRequests.Build(ManifestUpload, context) with { Claims = [.. held, overreach] },
                Token);

            var violation = await RouteProbe.ForbiddenViolationAsync(ManifestUpload, response, Token);
            if (violation is not null)
            {
                violations.Add($"{overreach}: {violation}");
            }
        }

        violations.ShouldBeEmpty(
            "a token claiming a code outside the service ceiling must be refused (403 forbidden) "
            + "even though it also holds run:create: " + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 02.6.2 AC6 / §12 finding 1: the service-claim bound is the UNION of the
    /// <c>kind: service</c> roles (<c>PermissionEvaluator.ServiceRoleCeiling</c>, pinned to
    /// <c>contracts/permissions.json</c>), not the minting role's own set. The agent codes
    /// <c>job:claim</c> / <c>job:execute</c> are inside that ceiling, so a token claiming
    /// them alongside <c>run:create</c> is NOT malformed and the request it covers is
    /// granted — while the same token plus a staff code holds nothing. Both halves are
    /// asserted, so the boundary is pinned rather than assumed.
    /// </summary>
    [Fact]
    public async Task AgentCodes_AreInsideTheServiceCeiling_SoRunCreateStillHolds()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();
        var held = matrix.PermissionsOf("ci_producer");
        var context = RouteRequestContext.Fresh(org, project);

        await using var factory = ServicePrincipalProbeFactory.Start(Fixture, org, project);
        using var client = factory.CreateClient();

        // Inside the ceiling: the token is well formed, so run:create still counts.
        using var inside = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, context) with { Claims = [.. held, "job:claim", "job:execute"] },
            Token);
        (await RouteProbe.NotForbiddenViolationAsync(ManifestUpload, inside, Token)).ShouldBeNull(
            "job:claim and job:execute are agent codes inside the service ceiling, so the token is not malformed");

        // Outside it: one staff code makes the whole claim set hold nothing.
        using var outside = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, context) with { Claims = [.. held, "job:claim", "billing:read"] },
            Token);
        (await RouteProbe.ForbiddenViolationAsync(ManifestUpload, outside, Token)).ShouldBeNull(
            "billing:read is outside the service ceiling, so the token holds nothing");
    }

    /// <summary>
    /// The same rule through a REAL minted token: an ingest token created on the admin
    /// route, exchanged at <c>POST /v1/ci/token</c>, presented as a bearer. The minted
    /// <c>ci_producer</c> calling <c>POST /v1/orgs/{o}/memberships</c> is 403
    /// <c>forbidden</c>; its <c>perm</c> claim is exactly the <c>ci_producer</c> set; and
    /// the same token on the route its set covers (<c>POST /v1/projects/{p}/manifests</c>)
    /// is neither 401 nor 403.
    /// </summary>
    [Fact]
    public async Task Real_Minted_CiProducer_Token_Is_403_On_Membership_Write()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var adminUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();
        var keys = CiKeyMaterial.Create();

        await using var factory = CiProducerJwtFactory.Start(
            Fixture,
            keys,
            org,
            project,
            adminUserId,
            $"admin-{adminUserId:N}@example.test");
        using var client = factory.CreateClient();

        var jwt = await MintAsync(client, project);

        // S1: the exchange minted exactly the ci_producer set, no more and no less.
        CiProducerJwtFactory.PermissionClaim(jwt)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ShouldBe(matrix.PermissionsOf("ci_producer"));

        // The minted token calling membership:write is 403 forbidden.
        using var denied = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(MembershipWrite, RouteRequestContext.Fresh(org, project)) with { Bearer = jwt },
            Token);
        (await RouteProbe.ForbiddenViolationAsync(MembershipWrite, denied, Token)).ShouldBeNull();

        // Positive control: the same token on the route its set covers is not refused, so
        // the 403 above is the evaluator's verdict and not a rejected credential.
        using var allowed = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, RouteRequestContext.Fresh(org, project)) with { Bearer = jwt },
            Token);
        allowed.StatusCode.ShouldNotBe(
            HttpStatusCode.Unauthorized, "the minted token must authenticate through the real CI authenticator");
        allowed.StatusCode.ShouldNotBe(
            HttpStatusCode.Forbidden, "run:create is in the ci_producer set, so the manifest route must not deny");
    }

    /// <summary>
    /// FR-02-2 / 02.9.1 isolation precedent: a REAL minted token for project A calling
    /// <c>POST /v1/projects/{B}/manifests</c> in the same org is 403
    /// <c>project_scope_mismatch</c> — the code the 02.9.1 status row asserts on this route
    /// — and nothing is written.
    /// </summary>
    /// <remarks>
    /// The tenant is resolved from the token (org, project A), so the evaluator's claim
    /// check passes; the refusal is the handler's own route-vs-scope guard
    /// (<c>OrchestratorScope.Project</c>), which is what a real entry point reaches.
    /// </remarks>
    [Fact]
    public async Task Real_Minted_Token_For_Project_A_On_Project_B_Is_Refused()
    {
        var (org, projectA) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var (_, projectB) = await Fixture.SeedProjectAsync(orgId: org, cancellationToken: Token);
        var adminUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var keys = CiKeyMaterial.Create();

        await using var factory = CiProducerJwtFactory.Start(
            Fixture,
            keys,
            org,
            projectA,
            adminUserId,
            $"admin-{adminUserId:N}@example.test");
        using var client = factory.CreateClient();

        var jwt = await MintAsync(client, projectA);

        var before = await OrgRowCounter.CountAsync(Fixture, org, Token);
        using var response = await RouteProbe.SendAsync(
            client,
            RouteRequests.Build(ManifestUpload, RouteRequestContext.Fresh(org, projectB)) with { Bearer = jwt },
            Token);
        var after = await OrgRowCounter.CountAsync(Fixture, org, Token);

        var violation = await RouteProbe.DeniedViolationAsync(
            ManifestUpload, response, ApiErrorCode.ProjectScopeMismatch, Token);
        violation.ShouldBeNull();
        after.ShouldBe(before, "a scope-mismatched manifest must not change the org's rows");
    }

    /// <summary>
    /// Mints a real <c>ci_producer</c> JWT for <paramref name="project"/>: an ingest token
    /// created on the admin route, then exchanged at <c>POST /v1/ci/token</c>.
    /// </summary>
    private static async Task<string> MintAsync(HttpClient client, Guid project)
    {
        using var created = await client.PostAsync(
            $"/v1/projects/{project}/ingest-tokens",
            new StringContent("""{"name":"probe"}""", Encoding.UTF8, "application/json"),
            Token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("secret").GetString();
        secret.ShouldNotBeNullOrWhiteSpace();

        using var exchanged = await client.PostAsync(
            "/v1/ci/token",
            new StringContent($$"""{"ingest_token":"{{secret}}"}""", Encoding.UTF8, "application/json"),
            Token);
        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK);
        var jwt = (await exchanged.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("token").GetString();
        jwt.ShouldNotBeNullOrWhiteSpace();

        return jwt!;
    }
}
