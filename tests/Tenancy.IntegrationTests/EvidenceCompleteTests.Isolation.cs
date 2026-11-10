// The isolation and guard half of the completion suite: cross-tenant and other-project
// 404s, the project-scoped caller, the state and project guards, and the P3 statement
// bound. The rest of the suite is in EvidenceCompleteTests.cs and its other partial files.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Isolation and state guards: an artifact of another tenant or project is a 404, the route
/// needs a project-scoped principal, a deleted project or wrong state is a 409, and one
/// completion is a bounded number of statements whose errors leak no key.
/// </summary>
public sealed partial class EvidenceCompleteTests
{

    // ------------------------------------------------------------------ isolation and state guards

    [Fact]
    public async Task Complete_CrossTenantArtifact_404()
    {
        await using var world = await StartAsync(Writer());
        var (otherOrg, otherProject) = await CreateOtherOrgAsync(world, "globex");
        var otherRun = await SeedRunAsync(world, otherOrg);
        var artifactId = await InsertArtifactAsync(world, otherOrg, otherProject, otherRun);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("artifact_not_found");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
    }

    [Fact]
    public async Task Complete_OtherProjectArtifact_404()
    {
        await using var world = await StartAsync(Writer());
        using var setup = world.Api.CreateClient();
        var otherProject = await CreateProjectAsync(setup, world.User, world.Org, "other");
        var otherRun = await SeedRunAsync(world);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set project_id = '{otherProject:D}' where id = '{otherRun:D}'");
        var artifactId = await InsertArtifactAsync(world, world.Org, otherProject, otherRun);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("artifact_not_found");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
    }

    [Fact]
    public async Task Complete_OrgScopedPrincipal_403_ProjectRequired()
    {
        // The route names no project: a project-scoped principal (the CI producer token) is required, and an
        // org-scoped service principal is refused with project_required before any storage work (T1 decision 10).
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        using var client = api.CreateClient();
        var artifactId = Guid.CreateVersion7();
        var answer = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/artifacts/{artifactId:D}/complete",
            TenancyApiFactory.Resolved(serviceOrg: org, servicePermissions: [Permissions.Artifact.Write]),
            null,
            NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("project_required");
        project.ShouldNotBe(Guid.Empty, "the org has a project, but the caller's credential names none");
    }

    [Fact]
    public async Task Complete_ProjectDeleted_409_project_deleting()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("project_deleting");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task Complete_StateGuard_409()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        // A matching staging object is in place, so only the state guard can refuse the completion. The row reaches
        // `pending_delete` over the two edges the contract allows, because the trigger refuses the direct move from
        // `pending_upload`.
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.artifact set state = 'ready' where id = '{artifactId:D}'");
        await _fixture.ExecuteAsync(
            world.Database, $"update public.artifact set state = 'pending_delete' where id = '{artifactId:D}'");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("artifact_state_conflict");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_delete");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ P3: bounded statements, redaction

    [Fact]
    public async Task Complete_BoundedStatements()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        world.Api.Statements.Clear();
        world.Host.Statements.Clear();
        var answer = await CompleteAsync(world, artifactId, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        // One completion is a constant number of statements (P3), derived from the code paths:
        //   verifier  — the row-facts read (the read-only scope's `set transaction read only; select set_config(…)`
        //               begin is dropped by the recorder's transaction-control filter, so it is not counted);
        //   writer    — the `select set_config(evidgate.org_id, …)` scope begin, the idempotency claim, the artifact
        //               read, the project-status read (review S-F3), the artifact UPDATE and the two outbox inserts;
        //   audit     — the lock_timeout set_config, the advisory lock, the chain-tip read and the audit insert;
        //   idempotency — the response completion.
        // That is 1 + (1 + 1 + 1 + 1 + 1 + 2) + 4 + 1 = 13 statements; 16 leaves a small margin for an EF or
        // pipeline variation without admitting an unbounded count. The storage HEAD/copy are network calls, never
        // SQL (P5).
        var statements = world.Host.Statements.Snapshot();
        statements.Count.ShouldBeGreaterThan(0);
        statements.Count.ShouldBeLessThanOrEqualTo(16);
    }

    [Fact]
    public async Task Complete_ErrorResponses_DoNotContainKeys()
    {
        await using var world = await StartAsync(Writer());

        var answer = await CompleteAsync(world, Guid.CreateVersion7(), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("artifact_not_found");

        var body = answer.Body.GetRawText();
        body.ShouldNotContain("pending/");
        body.ShouldNotContain("sha256:");
        body.ShouldNotContain("X-Amz");
        body.ShouldNotContain("upload_url");
    }
}
