using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
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
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ViewerDenyMatrixTests(DatabaseFixture fixture) : CatalogueTestBase(fixture)
{
    /// <summary>
    /// The mutating routes the deny matrix excludes, each with the reason it carries no
    /// catalogue permission a viewer could be denied for.
    /// </summary>
    private static readonly Dictionary<(string Method, string Template), string> ExcludedMutating =
        new()
        {
            [("POST", "/v1/orgs")] =
                "x-permission is 'authenticated' (the org creator is any authenticated caller, 02.1.1)",
            [("POST", "/v1/ci/token")] =
                "x-permission is 'none' (pre-tenant credential exchange, 02.4.1)",
            [("POST", "/v1/slice/run")] =
                "01.1.2 dev surface absent from the spec, no permission requirement",
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

        var excluded = mutating.Where(route => ExcludedMutating.ContainsKey((route.Method, route.Template))).ToList();
        var checkedRoutes = mutating.Where(route => !ExcludedMutating.ContainsKey((route.Method, route.Template))).ToList();

        // The exclusion list is explicit and asserted: exactly the routes named above are
        // excluded, and the checked count is the served mutating count minus them.
        excluded.Select(route => (route.Method, route.Template))
            .OrderBy(key => key.Method, StringComparer.Ordinal)
            .ThenBy(key => key.Template, StringComparer.Ordinal)
            .ShouldBe(ExcludedMutating.Keys
                .OrderBy(key => key.Method, StringComparer.Ordinal)
                .ThenBy(key => key.Template, StringComparer.Ordinal)
                .ToList());
        checkedRoutes.Count.ShouldBe(mutating.Count - ExcludedMutating.Count);
        checkedRoutes.ShouldNotBeEmpty();

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
