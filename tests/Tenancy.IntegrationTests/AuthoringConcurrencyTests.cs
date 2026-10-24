using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 AC19 under real concurrency (C4): two writers that read one node's version both send <c>If-Match: "v"</c>, and
/// exactly one of them lands. The row is locked <c>for update</c> before the snapshot is merged, so the loser compares its
/// stale tag against the committed version and answers 412 — the writer's own out-of-step guard cannot tell the two apart
/// (each appends exactly one version above the last), which is why the check has to happen under the lock.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringConcurrencyTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringConcurrencyTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task NodePatch_TwoWriters_SecondGets412()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        Action<HttpRequestHeaders> asUser = TenancyApiFactory.Resolved(user: user, orgHeader: org);

        var created = await AuthoringCalls.CreateNodeAsync(client, asUser, project, null, "module", "Checkout");
        created.Status.ShouldBe(HttpStatusCode.Created);
        var node = created.Member("node_id").GetGuid();
        var url = $"/v1/projects/{project}/authoring/nodes/{node:D}";

        // Both writers start from the same version and the same tag, which is exactly what two editors of one node see.
        var first = AuthoringCalls.SendAsync(client, asUser, HttpMethod.Patch, url, JsonSerializer.Serialize(new { title = "First" }), ifMatch: "\"1\"");
        var second = AuthoringCalls.SendAsync(client, asUser, HttpMethod.Patch, url, JsonSerializer.Serialize(new { title = "Second" }), ifMatch: "\"1\"");

        var answers = await Task.WhenAll(first, second);

        answers.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(1);
        answers.Count(answer => answer.Status == HttpStatusCode.PreconditionFailed).ShouldBe(1);

        var refused = answers.Single(answer => answer.Status == HttpStatusCode.PreconditionFailed);
        refused.Body!.Value.GetProperty("error").GetProperty("code").GetString().ShouldBe("precondition_failed");

        // The winner's version is the only one written: one landed edit is one version row.
        var winner = answers.Single(answer => answer.Status == HttpStatusCode.OK);
        winner.Member("version").GetInt32().ShouldBe(2);

        var read = await AuthoringCalls.TreeAsync(client, asUser, project, nodeId: node);
        read.Member("node").GetProperty("version").GetInt32().ShouldBe(2);
    }
}
