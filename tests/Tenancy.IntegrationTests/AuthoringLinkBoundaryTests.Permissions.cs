using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringLinkBoundaryTests
{
    [Fact]
    public async Task Links_ViewerRole_ReadsButCannotLinkOrUnlink()
    {
        await using var world = await StartAsync();
        var viewer = await ViewerAsync(world);
        var read = await world.Client.SendAsync(HttpMethod.Get, world.Tree.CasesPath, viewer);
        read.Status.ShouldBe(HttpStatusCode.OK);
        var denied = await world.Client.SendAsync(HttpMethod.Post, world.Tree.LinkPath(world.Manual), viewer,
            new { test_case_id = world.Catalog }, NewKey());
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(0);

        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
        var path = world.Tree.LinkPath(world.Manual) + $"/{world.Catalog:D}";
        var unlink = await world.Client.SendAsync(HttpMethod.Delete, path, viewer, null, NewKey());
        unlink.Status.ShouldBe(HttpStatusCode.Forbidden);
        unlink.ErrorCode.ShouldBe("forbidden");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(1);
        (await world.Client.SendAsync(HttpMethod.Delete, path, world.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Links_ServiceToken_CannotUseClaimedAuthoringPermissions(bool otherProject)
    {
        await using var world = await StartAsync();
        var project = otherProject
            ? (await AuthoringLinkTestData.SeedTreeAsync(fixture, world.Database, world.Tree.Org)).Project
            : world.Tree.Project;
        var token = TenancyApiFactory.Resolved(serviceOrg: world.Tree.Org, serviceProject: project,
            servicePermissions: ["authoring:read", "authoring:write"]);
        var path = world.Tree.LinkPath(world.Manual);
        var denied = await world.Client.SendAsync(HttpMethod.Post, path, token,
            new { test_case_id = world.Catalog }, NewKey());
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        // Authoring claims exceed the service-role ceiling before project-scope evaluation.
        denied.ErrorCode.ShouldBe("forbidden");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(0);
        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
        var unlink = await world.Client.SendAsync(HttpMethod.Delete, path + $"/{world.Catalog:D}", token, null, NewKey());
        unlink.Status.ShouldBe(HttpStatusCode.Forbidden);
        unlink.ErrorCode.ShouldBe("forbidden");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(1);
        var read = await world.Client.SendAsync(HttpMethod.Get, world.Tree.CasesPath + "?covered=true", token);
        read.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Get, world.Tree.CasesPath + "?covered=true", world.Caller))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Links_MissingIdempotencyKey_RefusesWithoutMutation()
    {
        await using var world = await StartAsync();
        var path = world.Tree.LinkPath(world.Manual);
        var denied = await world.Client.SendAsync(HttpMethod.Post, path, world.Caller,
            new { test_case_id = world.Catalog });
        denied.Status.ShouldBe(HttpStatusCode.BadRequest);
        denied.ErrorCode.ShouldBe("request_invalid");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(0);
        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
        var unlinkPath = path + $"/{world.Catalog:D}";
        var unlink = await world.Client.SendAsync(HttpMethod.Delete, unlinkPath, world.Caller);
        unlink.Status.ShouldBe(HttpStatusCode.BadRequest);
        unlink.ErrorCode.ShouldBe("request_invalid");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(1);
        (await world.Client.SendAsync(HttpMethod.Delete, unlinkPath, world.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Link_BodyOver64KiB_RefusesBeforeMutation()
    {
        await using var world = await StartAsync();
        var denied = await world.Client.SendAsync(HttpMethod.Post, world.Tree.LinkPath(world.Manual), world.Caller,
            new { test_case_id = world.Catalog, padding = new string('x', 65_537) }, NewKey());
        denied.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        denied.ErrorCode.ShouldBe("payload_too_large");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(0);
        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
    }

    private async Task<Action<HttpRequestHeaders>> ViewerAsync(World world)
    {
        var user = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public."user" (id, email) values ('{user}', 'boundary-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{member}', '{world.Tree.Org}', '{user}', 'boundary-{user:N}@example.test', 'active');
            """);
        var role = await fixture.ScalarAsync<Guid>(world.Database,
            "select id from public.role where org_id is null and code = 'viewer'");
        var granted = await world.Client.SendAsync(HttpMethod.Post,
            $"/v1/orgs/{world.Tree.Org:D}/memberships/{member:D}/roles", world.Caller, new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);
        return TenancyApiFactory.Resolved(user: user, orgHeader: world.Tree.Org);
    }
}
