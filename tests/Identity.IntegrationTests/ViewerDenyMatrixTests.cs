using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// AC3 / 02.2.1 §2 line 18: a <c>viewer</c> gets 403 <c>forbidden</c> on EVERY write
/// endpoint, even with a valid request — the deny-by-default C2 stage over the real host
/// and the module's real evaluator, generated from the served route table (never a hand
/// list) and the spec's <c>x-permission</c>.
/// </summary>
/// <remarks>
/// <para>
/// The principal is a seeded ACTIVE <c>viewer</c> member of the org, so the deny is the
/// evaluator's verdict on a real membership, not a broken caller; a positive control
/// (<see cref="Viewer_Read_Is_Not_Denied"/>) proves the same principal reads what it holds.
/// A route whose <c>x-permission</c> is <c>none</c> or <c>authenticated</c> is excluded
/// with an explicit, asserted reason (<see cref="ExcludedMutating"/>), and the number of
/// routes checked equals the served mutating count minus those exclusions — the matrix
/// cannot pass vacuously.
/// </para>
/// <para>
/// <see cref="Every_Served_Route_Is_Classified"/> is the classification gate: a served
/// route with no request builder, or a builder for a route the host no longer serves,
/// fails here naming the route.
/// </para>
/// <para>
/// The read half (<see cref="Viewer_Read_Endpoints_Respect_The_Viewer_Set"/>) runs the same
/// matrix over every served GET route that declares a catalogue code, and
/// <see cref="NonMember_Principals_Are_403_NoMembership"/> over every code-bearing route
/// with a removed member and a member of another org. Custom roles
/// (<c>custom_roles_enabled</c> is off) and the production <c>ProjectId = null</c> member
/// resolution are NOT covered here: the host publishes the tenant directly, so the
/// resolver's own <c>forbidden</c> for a non-member is the 02.6.2 suites' subject.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ViewerDenyMatrixTests(DatabaseFixture fixture) : CatalogueTestBase(fixture)
{
    /// <summary>
    /// The mutating routes the deny matrix excludes, each with the reason it carries no
    /// catalogue permission a viewer could be denied for. The observed exclusion set is
    /// DERIVED from the route data (<see cref="ServedRouteTable.IsUnpermissioned"/>) and
    /// asserted equal to this documented list, so the two cannot drift apart.
    /// </summary>
    private static readonly Dictionary<(string Method, string Template), string> ExcludedMutating =
        new()
        {
            [("POST", "/v1/orgs")] =
                "x-permission is 'authenticated' (the org creator is any authenticated caller, 02.1.1 §5)",
            [("POST", "/v1/ci/token")] =
                "x-permission is 'none' (pre-tenant credential exchange, 02.4.1)",
            [("POST", "/v1/auth/invite-token")] =
                "x-permission is 'none' (pre-tenant invite staging, 02.3.1-T2: the browser holds no session yet)",
            [("POST", "/v1/auth/link/{provider}")] =
                "x-permission is 'none' (the link start acts only on the caller's own session cookie, 02.3.1-T2)",
            [("DELETE", "/v1/auth/link/pending")] =
                "x-permission is 'none' (discarding the caller's own staged link, 02.3.1-T2)",
            [("POST", "/v1/auth/link/confirm")] =
                "x-permission is 'none' (confirming the caller's own staged link, 02.3.1-T2)",
            [("DELETE", "/v1/auth/sessions/{s}")] =
                "x-permission is 'none' (revoking a session acts on the caller's own credential; the admin path needs membership:write inside the definer, 02.3.2-T2)",
            [("POST", "/v1/auth/sessions/revoke-all")] =
                "x-permission is 'none' (revoking the caller's own sessions, 02.3.2-T2)",
            [("POST", "/v1/auth/logout")] =
                "x-permission is 'none' (logout revokes the caller's own session row, 02.3.2-T2)",
        };

    /// <summary>
    /// The classification gate: every served route has a request builder, and every
    /// builder names a route the host still serves. A new endpoint fails here until it is
    /// classified; a removed one fails until its builder is deleted.
    /// </summary>
    [Fact]
    public async Task Every_Served_Route_Is_Classified()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, new IdentityHostSetup(org));

        var served = ServedRouteTable.From(factory.Services);
        served.ShouldNotBeEmpty("the host serves /v1 routes");

        var servedKeys = served.Select(route => (route.Method, route.Template)).ToHashSet();
        var classifiedKeys = RouteRequests.All.Keys.ToHashSet();

        var unclassified = servedKeys.Except(classifiedKeys)
            .OrderBy(key => key.Method, StringComparer.Ordinal)
            .ThenBy(key => key.Template, StringComparer.Ordinal)
            .Select(key => $"{key.Method} {key.Template}")
            .ToList();
        unclassified.ShouldBeEmpty("served routes with no request builder: " + string.Join(", ", unclassified));

        var stale = classifiedKeys.Except(servedKeys)
            .OrderBy(key => key.Method, StringComparer.Ordinal)
            .ThenBy(key => key.Template, StringComparer.Ordinal)
            .Select(key => $"{key.Method} {key.Template}")
            .ToList();
        stale.ShouldBeEmpty("classified routes no longer served: " + string.Join(", ", stale));
    }

    /// <summary>
    /// AC3: for every served mutating route that carries a catalogue permission the viewer
    /// does not hold, the answer is 403 <c>forbidden</c> — never 400/422/404 — with the
    /// request id echoed and <c>retryable</c> false, and no row of the org changes. A route
    /// the viewer does hold must not be denied.
    /// </summary>
    [Fact]
    public async Task Viewer_Write_Endpoints_Are_403_Forbidden()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var viewerUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, ProjectId: project, Principal: RequestPrincipal.Member(viewerUserId)));
        using var client = factory.CreateClient();

        var served = ServedRouteTable.From(factory.Services);
        var mutating = served.Where(route => ServedRouteTable.IsMutating(route.Method)).ToList();
        mutating.ShouldNotBeEmpty("the host serves mutating /v1 routes");

        // Derived from the route data, never from the documented list: a route that gains
        // or loses its x-permission moves in or out of the matrix by itself.
        var excluded = mutating.Where(ServedRouteTable.IsUnpermissioned).ToList();
        var checkedRoutes = mutating.Where(route => !ServedRouteTable.IsUnpermissioned(route)).ToList();

        // The exclusion list is explicit and asserted: exactly the routes named above are
        // excluded, and the checked count is the served mutating count minus them.
        excluded.Select(route => $"{route.Method} {route.Template}")
            .Order(StringComparer.Ordinal)
            .ToList()
            .ShouldBe(
                ExcludedMutating.Keys
                    .Select(key => $"{key.Method} {key.Template}")
                    .Order(StringComparer.Ordinal)
                    .ToList(),
                Case.Sensitive,
                "the exclusion set derived from the route data must equal the documented one");
        checkedRoutes.Count.ShouldBe(mutating.Count - ExcludedMutating.Count);
        checkedRoutes.ShouldNotBeEmpty();

        // Every exclusion carries a reason, and the reason names the route's own
        // classification: the x-permission it carries, or the allowlist that records it.
        // The recorded reason is used, never discarded.
        foreach (var route in excluded)
        {
            var reason = ExcludedMutating[(route.Method, route.Template)];
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
        var before = await OrgRowCounter.CountAsync(Fixture, org, Token);

        var violations = new List<string>();
        var denied = 0;
        var allowed = 0;
        foreach (var route in checkedRoutes)
        {
            using var response = await RouteProbe.SendAsync(client, RouteRequests.Build(route, context), Token);
            if (matrix.Holds("viewer", route.Permission))
            {
                allowed++;
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

        var after = await OrgRowCounter.CountAsync(Fixture, org, Token);
        after.ShouldBe(before, "a denied write must not change the org's rows");

        denied.ShouldBeGreaterThan(0, "the viewer holds no write permission, so at least one route must be denied");
        (denied + allowed).ShouldBe(checkedRoutes.Count);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// AC3, the read half: over EVERY served GET route that declares a catalogue code, the
    /// viewer is 403 <c>forbidden</c> where the fixture says it lacks the code and not-403
    /// where it holds it. Both branches are asserted non-empty, so neither the deny nor the
    /// allow can pass vacuously.
    /// </summary>
    [Fact]
    public async Task Viewer_Read_Endpoints_Respect_The_Viewer_Set()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var viewerUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var matrix = RoleMatrixFixture.Load();

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, ProjectId: project, Principal: RequestPrincipal.Member(viewerUserId)));
        using var client = factory.CreateClient();

        var served = ServedRouteTable.From(factory.Services);
        var reads = served
            .Where(route => string.Equals(route.Method, "GET", StringComparison.Ordinal)
                && !ServedRouteTable.IsUnpermissioned(route))
            .ToList();
        reads.ShouldNotBeEmpty("the host serves GET routes that declare a catalogue permission");

        var context = RouteRequestContext.Fresh(org, project);
        var violations = new List<string>();
        var denied = 0;
        var allowed = 0;

        foreach (var route in reads)
        {
            using var response = await RouteProbe.SendAsync(client, RouteRequests.Build(route, context), Token);
            if (matrix.Holds("viewer", route.Permission))
            {
                allowed++;
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

        allowed.ShouldBeGreaterThan(0, "the viewer holds read codes, so at least one GET route must be allowed");
        denied.ShouldBeGreaterThan(0, "the viewer lacks some read codes, so at least one GET route must be denied");
        (denied + allowed).ShouldBe(reads.Count);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 02.1.2 AC2 / 02.3.1 AC3: a principal the org does not count as an active member is
    /// refused with the evaluator's <c>no_membership</c> code — not the generic
    /// <c>forbidden</c> — on every served route that declares a catalogue code. Two states
    /// are probed: a membership of this org whose status is <c>removed</c>, and an active
    /// member of ANOTHER org with this org published as the tenant.
    /// </summary>
    /// <remarks>
    /// Custom roles (<c>custom_roles_enabled</c> is off) and the production
    /// <c>ProjectId = null</c> member resolution are not covered here: the host publishes
    /// the tenant directly, so the resolver's own <c>forbidden</c> for a non-member
    /// (<c>TenantResolver.ResolveMemberAsync</c>) is exercised by the 02.6.2 suites instead.
    /// </remarks>
    [Fact]
    public async Task NonMember_Principals_Are_403_NoMembership()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var removedUserId = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, org, "viewer", status: "removed", cancellationToken: Token);
        var foreignOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var foreignUserId = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, foreignOrg, "viewer", cancellationToken: Token);

        var context = RouteRequestContext.Fresh(org, project);

        foreach (var (label, userId) in new (string Label, Guid UserId)[]
        {
            ("a removed member of the org", removedUserId),
            ("an active member of another org", foreignUserId),
        })
        {
            await using var factory = IdentityApiFactory.Start(
                Fixture,
                new IdentityHostSetup(org, ProjectId: project, Principal: RequestPrincipal.Member(userId)));
            using var client = factory.CreateClient();

            var served = ServedRouteTable.From(factory.Services);
            var withCode = served.Where(route => !ServedRouteTable.IsUnpermissioned(route)).ToList();
            withCode.ShouldNotBeEmpty("the host serves routes that declare a catalogue permission");

            var violations = new List<string>();
            foreach (var route in withCode)
            {
                using var response = await RouteProbe.SendAsync(client, RouteRequests.Build(route, context), Token);
                var violation = await RouteProbe.DeniedViolationAsync(route, response, ApiErrorCode.NoMembership, Token);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }

            violations.ShouldBeEmpty($"{label}: " + string.Join(Environment.NewLine, violations));
        }
    }

    /// <summary>
    /// The positive control: the same seeded viewer principal reads the routes it holds
    /// (<c>role:read</c>, 02.2.1 §9), so the 403s above are the evaluator's verdict and not
    /// a broken caller or an unseeded catalogue.
    /// </summary>
    [Fact]
    public async Task Viewer_Read_Is_Not_Denied()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var viewerUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, ProjectId: project, Principal: RequestPrincipal.Member(viewerUserId)));
        using var client = factory.CreateClient();

        foreach (var url in new[] { "/v1/permissions", "/v1/roles" })
        {
            using var response = await client.GetAsync(url, Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"viewer holds role:read, so GET {url} must not be denied");
            (await response.Content.ReadAsStringAsync(Token)).ShouldNotBeNullOrWhiteSpace();
        }
    }
}
