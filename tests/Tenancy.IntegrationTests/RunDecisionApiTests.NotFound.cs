using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The one answer a run the caller may not see gets: <c>404 not_found</c>, for an unknown id, for another tenant's run and
/// for a sibling project's run — the same code and message, so a run's existence is not observable across tenants or
/// projects.
/// </summary>
public sealed partial class RunDecisionApiTests
{
    /// <summary>An unknown run id is 404 <c>not_found</c>.</summary>
    [Fact]
    public async Task UnknownRun_IsNotFound()
    {
        await using var world = await StartAsync();

        var answer = await GetAsync(world, Guid.CreateVersion7());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>
    /// Another tenant's run is invisible under RLS, and the whole error envelope is the unknown-id one, so the decision
    /// route is no existence oracle.
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
        unknownAnswer.Status.ShouldBe(HttpStatusCode.NotFound);
        ErrorEnvelope(foreignAnswer).ShouldBe(
            ErrorEnvelope(unknownAnswer),
            "an existence oracle is a leak even when only one member differs");
    }

    /// <summary>
    /// A credential scoped to a sibling project reads no decision and learns nothing — the handler's scope check answers
    /// the unknown-id body, never a project-scope error. The host grants every permission so this branch is reachable: a
    /// real minted-token case (a CI token with <c>decision:read</c>) is in <c>RunDecisionCiTokenTests</c>.
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

        // The control: the same credential scoped to the run's own project reads the decision, so the 404 below is the
        // scope and not the read itself.
        var ownProject = await GetAsync(
            client,
            runId,
            TenancyApiFactory.Caller(
                org: world.Org,
                project: world.Project,
                serviceScope: world.Project,
                servicePermissions: ["decision:read"]));
        ownProject.Status.ShouldBe(HttpStatusCode.OK, ownProject.ErrorMessage);
        ownProject.Text("decision").ShouldBe("PASS");

        var siblingProject = await GetAsync(
            client,
            runId,
            TenancyApiFactory.Caller(
                org: world.Org,
                project: sibling,
                serviceScope: sibling,
                servicePermissions: ["decision:read"]));

        siblingProject.Status.ShouldBe(HttpStatusCode.NotFound);
        ErrorEnvelope(siblingProject).ShouldBe(
            ErrorEnvelope(unknownAnswer),
            "the scope mismatch must not become a second answer");
    }

    /// <summary>
    /// A member who names the run's org but a sibling project in the request header reads no decision and learns nothing:
    /// the answer is the unknown-run envelope, never a project-scope error. The same member naming the run's own project
    /// reads the decision, so the 404 is the header's scope and not a broken read.
    /// </summary>
    [Fact]
    public async Task SiblingProjectHeader_IsTheSame404BodyAsAnUnknownRun()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var policy = await SeedPolicyRevisionAsync(world);
        await InsertEvaluationAsync(world, runId, policy, new Verdict(1, "live", "PASS"));
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var unknownAnswer = await GetAsync(world, Guid.CreateVersion7());
        unknownAnswer.Status.ShouldBe(HttpStatusCode.NotFound);

        var ownProject = await GetAsync(
            world,
            runId,
            TenancyApiFactory.Caller(org: world.Org, project: world.Project, user: world.User));
        ownProject.Status.ShouldBe(HttpStatusCode.OK, ownProject.ErrorMessage);
        ownProject.Text("decision").ShouldBe("PASS");

        var siblingProject = await GetAsync(
            world,
            runId,
            TenancyApiFactory.Caller(org: world.Org, project: sibling, user: world.User));

        siblingProject.Status.ShouldBe(HttpStatusCode.NotFound);
        ErrorEnvelope(siblingProject).ShouldBe(
            ErrorEnvelope(unknownAnswer),
            "the header's project must not become a second answer");
    }

    /// <summary>
    /// The whole error envelope as comparable text, with <c>request_id</c> normalised because it differs per request: every
    /// other member — code, message, details and retryable — must match to the character.
    /// </summary>
    private static string ErrorEnvelope(ApiAnswer answer)
    {
        var error = answer.Body.GetProperty("error");
        var requestId = error.GetProperty("request_id").GetString();

        return string.Join(
            "\n",
            "code=" + error.GetProperty("code").GetString(),
            "message=" + error.GetProperty("message").GetString(),
            "retryable=" + error.GetProperty("retryable").GetBoolean(),
            "details=" + error.GetProperty("details").GetRawText(),
            "request_id=" + (string.IsNullOrEmpty(requestId) ? "<absent>" : "<set>"));
    }
}
