using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// <c>GET /v1/me/permissions</c> on the real Api composition: the caller's own grants in the org,
/// ordered, never cached, member-only, and refused 401/403 before a malformed <c>project</c> is 422.
/// </summary>
[Trait("Category", "Docker")]
public sealed class MyPermissionsTests(DatabaseFixture fixture) : CatalogueTestBase(fixture)
{
    private const string Path = "/v1/me/permissions";
    private const string OrgHeader = "X-Evidgate-Org";

    public static TheoryData<string> BuiltInMemberRoles()
    {
        var roles = new TheoryData<string>();
        foreach (var role in PermissionCatalogueFile.Load().Document.Roles.Where(role => role.Kind == "member"))
        {
            roles.Add(role.Code);
        }

        roles.Count.ShouldBeGreaterThanOrEqualTo(5);
        return roles;
    }

    [Theory]
    [MemberData(nameof(BuiltInMemberRoles))]
    public async Task Read_MemberRole_ReturnsExactlyTheCataloguedGrantsOfThatRole(string role)
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, role, cancellationToken: Token);
        await using var host = MemberHost(org, user);
        using var client = host.CreateClient();

        using var response = await GetAsync(client, org);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl.NoStore.ShouldBeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        body.GetProperty("org_id").GetGuid().ShouldBe(org);
        body.TryGetProperty("project_id", out _).ShouldBeFalse();
        Strings(body, "roles").ShouldBe([role]);

        // The expectation is the catalogue file, not the evaluator: a member role holds its file
        // permissions minus the codes a member may never hold.
        var assignable = Catalogue.Document.Permissions.Where(entry => entry.TenantAssignable).Select(entry => entry.Code);
        var expected = Catalogue.PermissionsOf(role).Intersect(assignable, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        expected.ShouldNotBeEmpty();
        var actual = Strings(body, "permissions");
        actual.ShouldBe(expected);

        // And every verdict of the C2 evaluator agrees with the list.
        await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);
        var evaluator = new PermissionEvaluator(Fixture.Db);
        var context = new RequestTenantContext(org, null);
        Catalogue.Codes.Count.ShouldBeGreaterThan(expected.Length);
        foreach (var code in Catalogue.Codes)
        {
            var granted = (await evaluator.EvaluateAsync(code, RequestPrincipal.Member(user), context, Token)).IsGranted;
            granted.ShouldBe(actual.Contains(code, StringComparer.Ordinal), code);
        }
    }

    [Fact]
    public async Task Read_TwoRolesAndProject_ListsBothRolesSortedAndEchoesProject()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var extra = await IdentityTestData.RoleIdAsync(Fixture, org, "qa_engineer", Token);
        await IdentityTestData.GrantRoleToMemberAsync(Fixture, org, user, extra, Token);
        await using var host = MemberHost(org, user);
        using var client = host.CreateClient();

        using var orgRead = await GetAsync(client, org);
        using var projectRead = await GetAsync(client, org, $"?project={project:D}");

        orgRead.StatusCode.ShouldBe(HttpStatusCode.OK);
        projectRead.StatusCode.ShouldBe(HttpStatusCode.OK);
        var orgBody = await orgRead.Content.ReadFromJsonAsync<JsonElement>(Token);
        var projectBody = await projectRead.Content.ReadFromJsonAsync<JsonElement>(Token);
        orgBody.TryGetProperty("project_id", out _).ShouldBeFalse();
        projectBody.GetProperty("project_id").GetGuid().ShouldBe(project);
        Strings(projectBody, "roles").ShouldBe(["qa_engineer", "viewer"]);
        var viewerOnly = Catalogue.PermissionsOf("viewer").Count;
        Strings(projectBody, "permissions").Length.ShouldBeGreaterThan(viewerOnly);
        Strings(projectBody, "permissions").ShouldBe(Strings(orgBody, "permissions"));
    }

    [Fact]
    public async Task Read_ResolverAnswersADistinctSet_ResponseCarriesThatSetSorted()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var resolver = new GrantAllPermissionEvaluator(Guid.CreateVersion7());
        await using var host = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(user), Evaluator: resolver, ResolveTenant: true));
        using var client = host.CreateClient();

        using var response = await GetAsync(client, org);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Strings(body, "permissions").ShouldBe(Permissions.All.Order(StringComparer.Ordinal).ToArray());
        Strings(body, "permissions").Length.ShouldBeGreaterThan(Catalogue.PermissionsOf("viewer").Count);
    }

    [Fact]
    public async Task Read_ResolverReportsNoMembership_Is403NoMembership()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        await using var control = MemberHost(org, user);
        using var controlClient = control.CreateClient();
        using var allowed = await GetAsync(controlClient, org);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var host = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(user),
                Evaluator: new FixedPermissionEvaluator(ApiErrorCode.Forbidden),
                ResolveTenant: true));
        using var client = host.CreateClient();
        using var refused = await GetAsync(client, org);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe(ApiErrorCode.NoMembership);
    }

    [Fact]
    public async Task Read_NonMemberOfTheNamedOrg_Is403BeforeProjectValidation()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var foreignOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        await IdentityTestData.SeedActiveMemberAsync(Fixture, foreignOrg, "tenant_admin", cancellationToken: Token);
        await using var host = MemberHost(org, user);
        using var client = host.CreateClient();

        using var control = await GetAsync(client, org);
        control.StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var query in new[] { "", "?project=bad" })
        {
            using var refused = await GetAsync(client, foreignOrg, query);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>(Token);
            body.TryGetProperty("permissions", out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("?project=bad")]
    [InlineData("?project=")]
    [InlineData("?project=00000000-0000-0000-0000-000000000000")]
    [InlineData("?project=11111111-1111-4111-8111-111111111111&project=22222222-2222-4222-8222-222222222222")]
    public async Task Read_MalformedProjectOfAnActiveMember_Is422Malformed(string query)
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        await using var host = MemberHost(org, user);
        using var client = host.CreateClient();

        using var control = await GetAsync(client, org, $"?project={Guid.CreateVersion7():D}");
        control.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await GetAsync(client, org, query);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe(ApiErrorCode.RequestInvalid);
        var detail = error.GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe("project");
        detail.GetProperty("reason").GetString().ShouldBe("malformed");
    }

    [Fact]
    public async Task Read_ServiceToken_Is403EvenWithMalformedProject()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        await using var control = MemberHost(org, user);
        using var controlClient = control.CreateClient();
        using var allowed = await GetAsync(controlClient, org);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var claims = RoleMatrixFixture.Load().PermissionsOf("ci_producer");
        await using var host = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, ProjectId: project, Principal: RequestPrincipal.Service(project, claims)));
        using var client = host.CreateClient();

        foreach (var query in new[] { "", "?project=bad" })
        {
            using var refused = await GetAsync(client, org, query);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Read_NoCredential_Is401EvenWithMalformedProject()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        await using var control = MemberHost(org, user);
        using var controlClient = control.CreateClient();
        using var allowed = await GetAsync(controlClient, org);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var host = IdentityApiFactory.Start(Fixture, new IdentityHostSetup(org, ResolveTenant: true));
        using var client = host.CreateClient();

        foreach (var query in new[] { "", "?project=bad" })
        {
            using var refused = await GetAsync(client, org, query);
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    private IdentityApiFactory MemberHost(Guid org, Guid user) =>
        IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(user), ResolveTenant: true));

    private static string[] Strings(JsonElement body, string name) =>
        [.. body.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Guid org, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path + query);
        request.Headers.Add(OrgHeader, org.ToString("D"));
        return await client.SendAsync(request, Token);
    }
}
