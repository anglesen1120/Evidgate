using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 01.6.1-T4.2 and 00 §9.2: a tenant-scoped read over HTTP, in-process. The route
/// is a test-only stand-in (see <see cref="TestOnlyTenantApi"/>); the machinery that
/// decides the answer — app-role connection, <c>evidgate.org_id</c>, RLS, Dapper —
/// is the production one, so what the test checks survives the 02.x real routes.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantIsolationApiTests : HarnessTest
{
    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public TenantIsolationApiTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }


    private static string ProjectUrl(Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"/v1/projects/{id}");

    /// <summary>
    /// Id-guessing: org B's project id asked as org A is 404 — the same answer a
    /// missing id gets (00 §9.2), never 403 and never a body. Two controls keep the
    /// assertion honest: org A's own id is 200 (the route works and RLS is not just
    /// hiding everything), and the very same id asked as org B is 200 too — so the
    /// 404 is the tenant boundary, not a missing row.
    /// </summary>
    [Fact]
    public async Task Isolation_ApiGet_OtherOrgId_404()
    {
        var (orgA, projectA) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var (orgB, projectB) = await Fixture.SeedProjectAsync(cancellationToken: Token);

        await using var api = await TestOnlyTenantApi.StartAsync(Fixture.AppDataSource);
        using var client = api.CreateClient();

        // As org A: own id is a real read, org B's id is 404, an invented id is 404 —
        // a foreign id and a nonexistent one are indistinguishable.
        using (var request = RequestFor(orgA, ProjectUrl(projectA)))
        {
            var response = await client.SendAsync(request, Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<ProjectDto>(Token);
            body.ShouldNotBeNull().Id.ShouldBe(projectA);
        }

        using (var request = RequestFor(orgA, ProjectUrl(projectB)))
        {
            var response = await client.SendAsync(request, Token);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync(Token)).ShouldBeEmpty();
        }

        using (var request = RequestFor(orgA, ProjectUrl(Guid.CreateVersion7())))
        {
            var response = await client.SendAsync(request, Token);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // The same row as its owner: 200 — the 404 above is the boundary, not absence.
        using (var request = RequestFor(orgB, ProjectUrl(projectB)))
        {
            var response = await client.SendAsync(request, Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static HttpRequestMessage RequestFor(Guid orgId, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestOnlyTenantApi.TenantHeader, orgId.ToString("D", CultureInfo.InvariantCulture));
        return request;
    }
}
