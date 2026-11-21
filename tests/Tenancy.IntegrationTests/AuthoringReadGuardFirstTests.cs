using System.Net;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Every Authoring read/export route with query parameters refuses an anonymous caller, then a caller holding no
/// <c>authoring:read</c>, before it judges the query: the same invalid parameter answers 401, 403 and only then the
/// route's own 422, so a refusal never tells a caller what the route would have made of the query.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringReadGuardFirstTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>No header at all: no principal, no tenant, no org.</summary>
    private static readonly Action<HttpRequestHeaders> Anonymous = _ => { };

    public AuthoringReadGuardFirstTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReadRoutes_InvalidQuery_RefuseBeforeValidationPerCaller()
    {
        await using var world = await StartAsync();
        var member = await AddMemberAsync(_fixture, world.Database, world.Org, roleCode: null);
        var asMember = TenancyApiFactory.Resolved(user: member, orgHeader: world.Org);

        foreach (var route in Routes(world.Project))
        {
            var unauthenticated = await AuthoringCalls.SendAsync(
                world.Client, Anonymous, HttpMethod.Get, route.InvalidUrl, withIdempotencyKey: false);
            unauthenticated.Status.ShouldBe(HttpStatusCode.Unauthorized, $"{route.Label}: an anonymous caller");
            ErrorCode(unauthenticated).ShouldBe(ApiErrorCode.Unauthenticated, $"{route.Label}: the 401 envelope");

            var forbidden = await AuthoringCalls.SendAsync(
                world.Client, asMember, HttpMethod.Get, route.InvalidUrl, withIdempotencyKey: false);
            forbidden.Status.ShouldBe(HttpStatusCode.Forbidden, $"{route.Label}: a member without authoring:read");
            ErrorCode(forbidden).ShouldBe(ApiErrorCode.Forbidden, $"{route.Label}: the 403 envelope");

            var invalid = await AuthoringCalls.SendAsync(
                world.Client, world.As, HttpMethod.Get, route.InvalidUrl, withIdempotencyKey: false);
            invalid.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{route.Label}: the authorised invalid query");
            ErrorCode(invalid).ShouldBe(ApiErrorCode.RequestInvalid, $"{route.Label}: the 422 envelope");
            invalid.Paths().ShouldContain(route.ErrorPath, $"{route.Label}: the refusal names the parameter");

            var valid = await AuthoringCalls.SendAsync(
                world.Client, world.As, HttpMethod.Get, route.ValidUrl, withIdempotencyKey: false);
            valid.Status.ShouldBe(HttpStatusCode.OK, $"{route.Label}: the positive control with a valid query");
        }
    }

    private static string? ErrorCode(AuthoringCalls.Answer answer) =>
        answer.Member("error").GetProperty("code").GetString();

    /// <summary>One guarded route: the invalid parameter it must refuse, its valid control, and the path the refusal blames.</summary>
    private sealed record Route(string Label, string InvalidUrl, string ValidUrl, string ErrorPath);

    private static IReadOnlyList<Route> Routes(Guid project)
    {
        var authoring = $"/v1/projects/{project}/authoring";
        var settings = $"/v1/projects/{project}/ai-settings";

        return
        [
            new("tree", $"{authoring}/tree?node_id=not-a-guid", $"{authoring}/tree?limit=1", "/node_id"),
            new("versions", $"{authoring}/versions?entity_id=not-a-guid", $"{authoring}/versions?limit=1", "/entity_id"),
            new("checklist items", $"{authoring}/checklist-items?node_id=not-a-guid", $"{authoring}/checklist-items?limit=1", "/node_id"),
            new("export", $"{authoring}/export?format=bogus", $"{authoring}/export?format=json", "/format"),
            new("manual cases", $"{authoring}/manual-cases?node_id=not-a-guid", $"{authoring}/manual-cases?limit=1", "/node_id"),
            new("changes", $"{authoring}/changes?target_id=not-a-guid", $"{authoring}/changes?limit=1", "/target_id"),
            new("AI settings versions", $"{settings}/versions?limit=0", $"{settings}/versions?limit=1", "/limit"),
        ];
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("read-guard"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("read-project"));

        return new World(api, client, database, user, org, project);
    }

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }
}
