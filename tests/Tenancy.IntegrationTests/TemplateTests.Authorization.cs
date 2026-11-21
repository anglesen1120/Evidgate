using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>The authorization half: both reads require <c>case:read</c>, and the pipeline answers before the query
/// members are validated, so an anonymous caller never learns a bound it may not use.</summary>
public sealed partial class TemplateTests
{
    private static Action<HttpRequestHeaders> Anonymous => _ => { };

    [Fact]
    public async Task TemplateReads_RequireAuthentication()
    {
        await using var world = await StartAsync();
        var id = await SeedAsync(world);

        (await ListAsync(world, caller: Anonymous)).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(world, "?kind=desktop", Anonymous)).Status.ShouldBe(
            HttpStatusCode.Unauthorized,
            "an unknown kind is not a 422 before authentication");
        (await GetAsync(world, id, Anonymous)).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TemplateReads_RefuseAMemberWithoutTheReadPermission()
    {
        await using var world = await StartAsync();
        var id = await SeedAsync(world);
        var member = await AddMemberAsync(_fixture, world.Database, world.Org, roleCode: null);

        (await ListAsync(world, "?kind=desktop&limit=0&cursor=x", world.Caller(member)))
            .Status.ShouldBe(HttpStatusCode.Forbidden, "authorization precedes query validation");
        (await GetAsync(world, id, world.Caller(member))).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TemplateReads_AnswerAViewer()
    {
        await using var world = await StartAsync();
        var id = await SeedAsync(world);
        var viewer = await AddMemberAsync(_fixture, world.Database, world.Org, roleCode: "viewer");

        (await ListAsync(world, caller: world.Caller(viewer))).Status.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(world, id, world.Caller(viewer))).Status.ShouldBe(HttpStatusCode.OK);
    }
}
