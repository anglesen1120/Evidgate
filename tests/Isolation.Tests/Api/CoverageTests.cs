using Shouldly;
using Xunit;

namespace Isolation.Tests.Api;

/// <summary>
/// 02.9.1-T1.1 (AC1, AC6): the coverage gate over the real host. Every route the host serves under <c>/v1</c> has
/// a case in <see cref="IsolationRouteCases"/>, every case names a served route, and every served route is in
/// <c>contracts/openapi.yaml</c> or on the allowlist below with its reason; a new endpoint without a case fails here,
/// naming the route.
/// </summary>
[Collection(ApiIsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class CoverageTests(ApiIsolationFixture fixture)
{
    /// <summary>Served routes deliberately absent from the spec, each a recorded decision.</summary>
    private static readonly Dictionary<ApiRoute, string> ServedNotInSpec = new();

    /// <summary>Spec ∩ served ⊆ covered, covered ⊆ served, served ⊆ spec ∪ allowlist.</summary>
    [Fact]
    public void EveryOpenApiRoute_HasIsolationCase()
    {
        var spec = OpenApiRouteInventory.Load();
        var served = ServedRouteInventory.From(fixture.Services);
        var covered = IsolationRouteCases.Covered;

        covered.Count.ShouldBe(covered.Distinct().Count(), "IsolationRouteCases lists a route twice");
        served.Count.ShouldBeGreaterThan(0);

        var result = RouteCoverage.Check(spec, served, covered, ServedNotInSpec);

        result.IsClean.ShouldBeTrue(result.Describe());
    }

    /// <summary>Every case yields at least one probe or list walk, so a covered route is never an empty case.</summary>
    [Fact]
    public void EveryIsolationCase_HasProbes()
    {
        var world = fixture.World;
        var walked = IsolationRouteCases.Lists(world).Select(w => w.Route).ToHashSet();
        var empty = IsolationRouteCases.All
            .Where(c => c.Member(world).Count + c.Service(world).Count == 0 && !walked.Contains(c.Route))
            .Select(c => c.Route.ToString())
            .ToList();

        empty.ShouldBeEmpty("isolation cases without a probe: " + string.Join(", ", empty));
    }
}
