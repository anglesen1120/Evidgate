#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    [Fact]
    public async Task GetRun_ClosedBindingWindow_PreservesEachPreviouslyBoundComponent()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "read-closed", "active", ("web", 'a'), ("api", 'b'));
        var laterRelease = await ReleaseWithAsync(world, "read-too-late", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("api", 'b')));
        var bound = await BindAsync(world, releaseId, runId);
        bound.Status.ShouldBe(HttpStatusCode.Created, bound.ErrorMessage);
        await SeedLiveEvaluationAsync(world, runId);

        var refused = await BindAsync(world, laterRelease, runId);
        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("release_bind_closed");

        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}", world.Admin);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var releases = answer.Body.GetProperty("releases").EnumerateArray().ToArray();
        releases.Length.ShouldBe(2);
        releases.ShouldAllBe(item => item.GetProperty("release_id").GetGuid() == releaseId);
        releases.Select(item => item.GetProperty("component_key").GetString())
            .ShouldBe(["web", "api"], ignoreOrder: true);
        answer.Body.GetRawText().ShouldNotContain(laterRelease.ToString());
    }
}
