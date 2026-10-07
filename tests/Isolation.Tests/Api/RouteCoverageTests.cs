using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Isolation.Tests.Api;

/// <summary>
/// 02.9.1 AC2, pure (no container, no host, no network): the spec inventory reads
/// <c>contracts/openapi.yaml</c> correctly, the served inventory reads endpoint
/// metadata, and the coverage gate goes red for an uncovered, stale or unspecified
/// route and green once the route is covered or allowlisted.
/// </summary>
// The tests are pure, but this assembly's DatabaseFixture assembly fixture
// (tests/Shared/DatabaseFixture.cs:16) starts Testcontainers for every test it runs, so
// a Docker-less run (--filter-not-trait Category=Docker, smoke-clean-build.sh) would
// fail them with DockerUnavailableException; same defect class as 01.6.1 Notes.
[Trait("Category", "Docker")]
public sealed class RouteCoverageTests
{
    private static readonly ApiRoute GetOrg = new("GET", "/v1/orgs/{o}");
    private static readonly ApiRoute GetCase = new("GET", "/v1/cases/{c}");
    private static readonly ApiRoute DeleteOrg = new("DELETE", "/v1/orgs/{o}");
    private static readonly ApiRoute SliceRun = new("POST", "/v1/slice/run");

    private static readonly IReadOnlyDictionary<ApiRoute, string> NoAllowlist = new Dictionary<ApiRoute, string>();

    [Fact]
    public void Spec_Lists_Known_Operations_Once_Under_V1()
    {
        var routes = OpenApiRouteInventory.Load();

        routes.ShouldContain(new ApiRoute("GET", "/v1/orgs/{o}"));
        routes.ShouldContain(new ApiRoute("PATCH", "/v1/projects/{p}"));
        routes.ShouldContain(new ApiRoute("POST", "/v1/ci/token"));
        routes.ShouldContain(new ApiRoute("DELETE", "/v1/orgs/{o}/memberships/{m}/roles/{r}"));
        routes.Distinct().Count().ShouldBe(routes.Count);
        routes.ShouldAllBe(route => ApiRoute.IsVersioned(route.Template));
    }

    [Fact]
    public void Spec_Parse_Ignores_Siblings_And_Folded_Scalars()
    {
        string[] spec =
        [
            "openapi: 3.1.0",
            "servers:",
            "  - url: /v1",
            "paths:",
            "  /things/{id}:",
            "    summary: Things",
            "    description: >-",
            "      get: this folded text is not an operation",
            "    parameters:",
            "      - $ref: '#/components/parameters/Id'",
            "    get:",
            "      description: >-",
            "        post: nor is this",
            "      responses:",
            "        '200':",
            "          $ref: '#/components/responses/Ok'",
            "    delete:",
            "      responses: {}",
            "components:",
            "  schemas: {}",
        ];

        var routes = OpenApiRouteInventory.Parse(spec, "inline.yaml");

        routes.ShouldBe([new ApiRoute("GET", "/v1/things/{id}"), new ApiRoute("DELETE", "/v1/things/{id}")]);
    }

    [Fact]
    public void Spec_Parse_Fails_Loudly_With_The_Line_Number()
    {
        string[] spec =
        [
            "servers:",
            "  - url: /v1",
            "paths:",
            "  /things:",
            "   get:",
            "      responses: {}",
        ];

        var exception = Should.Throw<InvalidOperationException>(() => OpenApiRouteInventory.Parse(spec, "bad.yaml"));

        exception.Message.ShouldStartWith("bad.yaml:5:");
    }

    [Fact]
    public void Gate_Is_Red_For_An_Uncovered_Route_And_Green_Once_Covered()
    {
        ApiRoute[] routes = [GetOrg, GetCase];

        var red = RouteCoverage.Check(routes, routes, [GetOrg], NoAllowlist);

        red.IsClean.ShouldBeFalse();
        red.Uncovered.ShouldBe([GetCase]);
        red.Describe().ShouldContain("uncovered: GET /v1/cases/{c} — add an isolation case in IsolationRouteCases");
        red.Describe().ShouldNotContain("GET /v1/orgs/{o} —");

        var green = RouteCoverage.Check(routes, routes, [GetOrg, GetCase], NoAllowlist);

        green.IsClean.ShouldBeTrue(green.Describe());
        green.Uncovered.ShouldBeEmpty();
    }

    [Fact]
    public void Gate_Reports_A_Stale_Covered_Route()
    {
        var result = RouteCoverage.Check([GetOrg, DeleteOrg], [GetOrg], [GetOrg, DeleteOrg], NoAllowlist);

        result.IsClean.ShouldBeFalse();
        result.Stale.ShouldBe([DeleteOrg]);
        result.Describe().ShouldContain("stale: DELETE /v1/orgs/{o}");

        // A spec route that is not served yet is informational, never a violation.
        var pending = RouteCoverage.Check([GetOrg, DeleteOrg], [GetOrg], [GetOrg], NoAllowlist);
        pending.IsClean.ShouldBeTrue(pending.Describe());
        pending.Pending.ShouldBe([DeleteOrg]);
    }

    [Fact]
    public void Gate_Reports_A_Served_Route_Missing_From_The_Spec_Unless_Allowlisted()
    {
        ApiRoute[] served = [GetOrg, SliceRun];

        var red = RouteCoverage.Check([GetOrg], served, served, NoAllowlist);

        red.IsClean.ShouldBeFalse();
        red.Unspecified.ShouldBe([SliceRun]);
        red.Describe().ShouldContain("unspecified: POST /v1/slice/run");

        var allowlist = new Dictionary<ApiRoute, string> { [SliceRun] = "dev-only slice surface, not part of the V1 contract" };
        var green = RouteCoverage.Check([GetOrg], served, served, allowlist);

        green.IsClean.ShouldBeTrue(green.Describe());
    }

    [Fact]
    public void Route_Constraints_Are_Normalised()
    {
        var route = new ApiRoute("get", "/v1/cases/{id:guid}/");

        route.ShouldBe(new ApiRoute("GET", "/v1/cases/{id}"));
        route.ToString().ShouldBe("GET /v1/cases/{id}");
        new ApiRoute("GET", "/v1//cases/{id?}").ShouldBe(route);
        new ApiRoute("GET", "/v1/cases/{id:guid}").GetHashCode().ShouldBe(route.GetHashCode());
        Should.Throw<ArgumentException>(() => new ApiRoute("GET", "/v10/cases"));
    }

    [Fact]
    public void Served_Inventory_Reads_V1_Route_Endpoints_With_Their_Methods()
    {
        Endpoint[] endpoints =
        [
            Route("/v1/cases/{id:guid}", "GET", "PATCH"),
            Route("/v1/slice/run"),
            Route("/internal/health/live", "GET"),
            Route("/v10/other", "GET"),
        ];
        using var services = new ServiceCollection()
            .AddSingleton<EndpointDataSource>(new DefaultEndpointDataSource(endpoints))
            .BuildServiceProvider();

        var routes = ServedRouteInventory.From(services);

        routes.ShouldBe(
        [
            new ApiRoute("GET", "/v1/cases/{id}"),
            new ApiRoute("PATCH", "/v1/cases/{id}"),
            new ApiRoute(ServedRouteInventory.AnyMethod, "/v1/slice/run"),
        ]);
    }

    private static RouteEndpoint Route(string pattern, params string[] methods)
    {
        var metadata = methods.Length == 0
            ? EndpointMetadataCollection.Empty
            : new EndpointMetadataCollection(new HttpMethodMetadata(methods));

        return new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, metadata, pattern);
    }
}
