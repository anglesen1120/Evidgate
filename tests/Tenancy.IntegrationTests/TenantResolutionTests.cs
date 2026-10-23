using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.6.2-T1.1 through the production <c>TenantResolutionFilter</c>: no test middleware
/// publishes a tenant, a stand-in authenticator only says who the caller is, and the
/// org comes from the route/header after <c>evidgate_resolve.member_orgs</c> confirms the
/// membership on every request (AC2, AC5, AC6; feature plan §9).
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantResolutionTests
{
    private static readonly string[] ProjectRead = ["project:read"];
    private static readonly string[] BillingRead = ["billing:read"];
    private static readonly string[] RunCreate = ["run:create"];

    private readonly PostgresTenancyFixture _fixture;

    public TenantResolutionTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Session_RevokedMembership_NextRequestForbidden()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");

        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", TenancyApiFactory.Resolved(user: user)))
            .Status.ShouldBe(HttpStatusCode.OK);

        // The membership is removed between two requests: the next one is refused, because
        // the membership is re-read per request (no cache).
        await _fixture.ExecuteAsync(
            database, $"update public.membership set status = 'removed' where org_id = '{org}' and user_id = '{user}'");

        var after = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", TenancyApiFactory.Resolved(user: user));
        after.Status.ShouldBe(HttpStatusCode.Forbidden);
        after.ErrorCode.ShouldBe("forbidden");
    }

    [Fact]
    public async Task NonMember_GetsTheSame403_ForARealAndAnUnknownOrg()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await CreateOrgAsync(client, Guid.CreateVersion7(), "acme");
        var stranger = Guid.CreateVersion7();

        var real = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", TenancyApiFactory.Resolved(user: stranger));
        var unknown = await client.SendAsync(
            HttpMethod.Get, $"/v1/orgs/{Guid.CreateVersion7()}", TenancyApiFactory.Resolved(user: stranger));

        real.Status.ShouldBe(HttpStatusCode.Forbidden);
        unknown.Status.ShouldBe(HttpStatusCode.Forbidden);
        real.ErrorMessage.ShouldBe(unknown.ErrorMessage);
    }

    [Fact]
    public async Task Unauthenticated_TenantRoute_Is401_AndNothingIsPublished()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{Guid.CreateVersion7()}", _ => { });
        answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
        answer.ErrorCode.ShouldBe("unauthenticated");

        (await client.SendAsync(HttpMethod.Get, "/v1/orgs", _ => { })).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListOrganizations_ReturnsOnlyTheCallersActiveOrgs()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        var first = await CreateOrgAsync(client, user, "first-org");
        var second = await CreateOrgAsync(client, user, "second-org");
        await CreateOrgAsync(client, other, "not-mine");

        var list = await client.SendAsync(HttpMethod.Get, "/v1/orgs", TenancyApiFactory.Resolved(user: user));
        list.Status.ShouldBe(HttpStatusCode.OK);
        var items = list.Body.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("slug").GetString()).Order().ShouldBe(["first-org", "second-org"]);
        items.Select(item => item.GetProperty("id").GetGuid()).Order().ShouldBe(new[] { first, second }.Order());
        items.ShouldAllBe(item => item.GetProperty("status").GetString() == "active");

        // A membership that is removed drops the org from the list on the next call.
        await _fixture.ExecuteAsync(
            database, $"update public.membership set status = 'removed' where org_id = '{first}' and user_id = '{user}'");
        (await client.SendAsync(HttpMethod.Get, "/v1/orgs", TenancyApiFactory.Resolved(user: user)))
            .Body.GetProperty("items").GetArrayLength().ShouldBe(1);

        (await client.SendAsync(HttpMethod.Get, "/v1/orgs?limit=0", TenancyApiFactory.Resolved(user: user)))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task ListOrganizations_ServiceToken_Is403()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Get,
            "/v1/orgs",
            TenancyApiFactory.Resolved(serviceOrg: Guid.CreateVersion7(), servicePermissions: ProjectRead));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProjectRoute_NamesItsOrgByHeader_AndTheHeaderIsCheckedAgainstMembership()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var elsewhere = await CreateOrgAsync(client, Guid.CreateVersion7(), "elsewhere");

        // No org named: 422, not a guess at "the user's only org".
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", TenancyApiFactory.Resolved(user: user)))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", TenancyApiFactory.Resolved(user: user, orgHeader: org)))
            .Status.ShouldBe(HttpStatusCode.OK);

        // An org the user is not a member of is refused before any tenant opens.
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", TenancyApiFactory.Resolved(user: user, orgHeader: elsewhere)))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceToken_RunsOnlyInItsOwnOrg_AndNeedsAnOrgClaim()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await CreateOrgAsync(client, Guid.CreateVersion7(), "acme");
        var elsewhere = await CreateOrgAsync(client, Guid.CreateVersion7(), "elsewhere");

        var token = TenancyApiFactory.Resolved(serviceOrg: org, servicePermissions: RunCreate);

        // Another org is 404 before authorization; the org's own route reaches the
        // evaluator, which refuses a ci_producer the tenant read it never held.
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{elsewhere}", token)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", token)).Status.ShouldBe(HttpStatusCode.Forbidden);

        (await client.SendAsync(
                HttpMethod.Get,
                $"/v1/orgs/{org}",
                TenancyApiFactory.Resolved(servicePermissions: RunCreate)))
            .Status.ShouldBe(HttpStatusCode.Unauthorized);

        // The org header is a member's selector: a service token ignores it.
        (await client.SendAsync(
                HttpMethod.Get,
                $"/v1/orgs/{org}",
                TenancyApiFactory.Resolved(orgHeader: elsewhere, serviceOrg: org, servicePermissions: RunCreate)))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceToken_ClaimingTenantAdminCodes_IsDenied_ByTheServiceRoleCeiling()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        var token = TenancyApiFactory.Resolved(serviceOrg: org, serviceProject: project, servicePermissions: ProjectRead);

        var answer = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", token);
        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
    }

    [Fact]
    public async Task ServicePrincipal_StaffOnlyCode_DeniedEndToEnd()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await CreateOrgAsync(client, Guid.CreateVersion7(), "acme");

        var answer = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/orgs/{org}",
            TenancyApiFactory.Resolved(serviceOrg: org, servicePermissions: ["run:create", .. BillingRead]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MalformedOrgRoute_IsLeftToTheFramework400()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Get, "/v1/orgs/not-a-guid", TenancyApiFactory.Resolved(user: Guid.CreateVersion7()));
        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A route marked <c>TenantlessEndpointMetadata</c> skips tenant resolution, so the
    /// set is pinned: only the two pre-tenant org routes may carry it (02.6.2 §3).
    /// </summary>
    [Fact]
    public async Task OnlyTheOrgCollectionRoutes_AreTenantless()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var tenantless = api.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<TenantlessEndpointMetadata>() is not null)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Order()
            .ToList();

        tenantless.ShouldBe(["GET /v1/orgs", "POST /v1/orgs"]);
    }

    private static async Task<Guid> CreateOrgAsync(HttpClient client, Guid user, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post,
            "/v1/orgs",
            TenancyApiFactory.Resolved(user: user, email: $"{slug}@example.test"),
            new { name = slug, slug },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, Guid user, Guid org, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Resolved(user: user),
            new { name = slug, slug },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }
}
