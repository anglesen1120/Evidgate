using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T2.2 routes 2-3 as versions: a create writes <c>version_no = 1</c>, a PATCH that changes the snapshot appends
/// exactly one row (so the entity's <c>version</c> moves by exactly one, AC5), and a PATCH whose content equals the stored
/// one writes none and answers <c>changed:false</c> with the unchanged version.
/// </summary>
public sealed partial class AuthoringVersionTests
{
    private sealed record NodeWorld(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public string NodeUrl(Guid node) => $"/v1/projects/{Project}/authoring/nodes/{node:D}";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<NodeWorld> StartNodeWorldAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new NodeWorld(api, client, database, user, org, project);
    }

    [Fact]
    public async Task NodePatch_WritesOneVersion()
    {
        await using var world = await StartNodeWorldAsync();

        var created = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "Checkout");
        created.Status.ShouldBe(HttpStatusCode.Created);
        created.Member("version").GetInt32().ShouldBe(1);

        var node = created.Member("node_id").GetGuid();
        created.ETag.ShouldBe("\"1\"");

        var patched = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(node),
            JsonSerializer.Serialize(new { title = "Checkout v2" }),
            ifMatch: "\"1\"");

        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Member("changed").GetBoolean().ShouldBeTrue();

        // One version row per changed entity (AC5) means the entity's version moves by exactly one, and the answer's
        // version is the row's `version_no` — a second row would show as a jump of two.
        patched.Member("version").GetInt32().ShouldBe(2);
        patched.ETag.ShouldBe("\"2\"");

        var read = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: node);
        read.Member("node").GetProperty("title").GetString().ShouldBe("Checkout v2");
        read.Member("node").GetProperty("version").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task NoOpPatch_WritesNoVersion()
    {
        await using var world = await StartNodeWorldAsync();

        var created = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "Checkout");
        var node = created.Member("node_id").GetGuid();

        // The same title the row already holds: AC5's no-op, so no version row and the unchanged version.
        var patched = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(node),
            JsonSerializer.Serialize(new { title = "Checkout" }),
            ifMatch: "\"1\"");

        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Member("changed").GetBoolean().ShouldBeFalse();
        patched.Member("version").GetInt32().ShouldBe(1);

        var read = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: node);
        read.Member("node").GetProperty("version").GetInt32().ShouldBe(1);
    }
}
