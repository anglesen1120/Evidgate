using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The one answer a run the caller may not see gets: <c>404 not_found</c>, for an unknown id and for a run of a
/// sibling project under a project-scoped credential — the same code and message, so a run's existence is not
/// observable across projects.
/// </summary>
public sealed partial class ListEvidenceRevisionsTests
{
    /// <summary>
    /// AC (00 §5.4): a credential scoped to a sibling project reads no page and learns nothing — the handler's scope
    /// check maps to the unknown-id body, never to <c>403 project_scope_mismatch</c>. The host grants every
    /// permission so this branch is reachable: a real evaluator refuses <c>run:read</c> to a service credential at the
    /// permission stage first (<c>ListRunAttemptsTests.ListRunAttempts_SiblingProjectCredential_Forbidden</c>).
    /// </summary>
    [Fact]
    public async Task ListEvidenceRevisions_SiblingProjectCredential_IsTheSame404BodyAsAnUnknownRun()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "complete"));
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var unknownAnswer = await GetAsync(world, Guid.CreateVersion7());
        unknownAnswer.Status.ShouldBe(HttpStatusCode.NotFound);

        await using var scoped = _hosts.GrantAll(world.Database);
        using var client = scoped.CreateClient();

        // The control: the same credential scoped to the run's own project reads the page, so the 404 below is the
        // scope and not the read itself.
        var ownProject = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{runId}/evidence-revisions",
            TenancyApiFactory.Caller(
                org: world.Org,
                project: world.Project,
                serviceScope: world.Project,
                servicePermissions: ["run:read"]));
        ownProject.Status.ShouldBe(HttpStatusCode.OK, ownProject.ErrorMessage);

        var siblingProject = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{runId}/evidence-revisions",
            TenancyApiFactory.Caller(
                org: world.Org,
                project: sibling,
                serviceScope: sibling,
                servicePermissions: ["run:read"]));

        siblingProject.Status.ShouldBe(HttpStatusCode.NotFound);
        siblingProject.ErrorCode.ShouldBe(unknownAnswer.ErrorCode);
        siblingProject.ErrorMessage.ShouldBe(
            unknownAnswer.ErrorMessage,
            "the scope mismatch must not become a second answer");
    }
}
