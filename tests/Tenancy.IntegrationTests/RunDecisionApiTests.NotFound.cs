using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The one answer a run the caller may not see gets: <c>404 not_found</c>, for an unknown id, for another tenant's run
/// and for a sibling project's run under a project-scoped credential — the same code and message, so a run's existence
/// is not observable across tenants or projects.
/// </summary>
public sealed partial class RunDecisionApiTests
{
    /// <summary>AC: an unknown run id is 404 <c>not_found</c>.</summary>
    [Fact]
    public async Task UnknownRun_IsNotFound()
    {
        await using var world = await StartAsync();

        var answer = await GetAsync(world, Guid.CreateVersion7());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>
    /// AC (00 §5.4): another tenant's run is invisible under RLS, and the answer is the unknown-id one to the code and
    /// the message, so the decision route is no existence oracle.
    /// </summary>
    [Fact]
    public async Task CrossTenant_IsTheSame404Body()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        await CreateProjectAsync(world.Client, otherUser, otherOrg, "rival-project");
        var foreign = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, otherOrg, 1);

        var foreignAnswer = await GetAsync(world, foreign.RunId);
        var unknownAnswer = await GetAsync(world, Guid.CreateVersion7());

        foreignAnswer.Status.ShouldBe(HttpStatusCode.NotFound);
        foreignAnswer.ErrorCode.ShouldBe(unknownAnswer.ErrorCode);
        foreignAnswer.ErrorMessage.ShouldBe(
            unknownAnswer.ErrorMessage,
            "an existence oracle is a leak even when only the code matches");
    }

    /// <summary>
    /// AC (00 §5.4): a credential scoped to a sibling project reads no decision and learns nothing — the handler's
    /// scope check maps to the unknown-id body, never to <c>403 project_scope_mismatch</c>. The host grants every
    /// permission so this branch is reachable: a real evaluator refuses <c>decision:read</c> to a service credential at
    /// the permission stage first.
    /// </summary>
    [Fact]
    public async Task SiblingProjectCredential_IsTheSame404BodyAsAnUnknownRun()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var policy = await SeedPolicyRevisionAsync(world);
        await InsertEvaluationAsync(world, runId, policy, new Verdict(1, "live", "PASS"));
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var unknownAnswer = await GetAsync(world, Guid.CreateVersion7());
        unknownAnswer.Status.ShouldBe(HttpStatusCode.NotFound);

        await using var scoped = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(world.Database));
        using var client = scoped.CreateClient();

        // The control: the same credential scoped to the run's own project reads the decision, so the 404 below is
        // the scope and not the read itself.
        var ownProject = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{runId}/decision",
            TenancyApiFactory.Caller(
                org: world.Org,
                project: world.Project,
                serviceScope: world.Project,
                servicePermissions: ["decision:read"]));
        ownProject.Status.ShouldBe(HttpStatusCode.OK, ownProject.ErrorMessage);
        ownProject.Text("decision").ShouldBe("PASS");

        var siblingProject = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{runId}/decision",
            TenancyApiFactory.Caller(
                org: world.Org,
                project: sibling,
                serviceScope: sibling,
                servicePermissions: ["decision:read"]));

        siblingProject.Status.ShouldBe(HttpStatusCode.NotFound);
        siblingProject.ErrorCode.ShouldBe(unknownAnswer.ErrorCode);
        siblingProject.ErrorMessage.ShouldBe(
            unknownAnswer.ErrorMessage,
            "the scope mismatch must not become a second answer");
    }
}
