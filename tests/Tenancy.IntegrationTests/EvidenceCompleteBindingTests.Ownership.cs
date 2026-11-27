using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Endpoints;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>Only the ingest token that minted a pending upload may complete or reject it.</summary>
public sealed partial class EvidenceCompleteBindingTests
{
    private const string OtherCredential = "another-ingest-token";

    private static RequestPrincipal Producer(string credentialId) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: credentialId);

    private async Task<ApiAnswer> CompleteAsAsync(World world, string credentialId, Guid artifactId)
    {
        await using var host = hosts.WithPrincipalConfigured(
            world.Database, world.Org, world.Project, Producer(credentialId), _store.Configuration());
        using var client = host.CreateClient();
        return await client.SendAsync(HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());
    }

    [Fact]
    public async Task Complete_OtherTokenOfTheSameProject_IsRefusedAndTheOwnerStillCompletes()
    {
        await using var world = await StartAsync(Producer(IngestTestWorld.OwnerId));
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);
        (await ArtifactColumnAsync(world, artifactId, "owner_credential_id")).ShouldBe(IngestTestWorld.OwnerId);

        var refused = await CompleteAsAsync(world, OtherCredential, artifactId);

        refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("artifact_not_found");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a refused completion must not reach the storage calls");

        var accepted = await CompleteAsync(world, artifactId, NewKey());
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Text("state").ShouldBe("ready");
    }

    [Fact]
    public async Task Complete_OtherTokenWithNoObjectUploaded_CannotRejectTheArtifact_AndTheOwnerRejectsIt()
    {
        await using var world = await StartAsync(Producer(IngestTestWorld.OwnerId));
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);
        var artifactId = minted.Id("artifact_id");

        var refused = await CompleteAsAsync(world, OtherCredential, artifactId);

        refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("artifact_not_found");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");

        var rejected = await CompleteAsync(world, artifactId, NewKey());
        rejected.Status.ShouldBe(HttpStatusCode.OK, rejected.ErrorMessage);
        rejected.Text("state").ShouldBe("rejected");
    }

    [Fact]
    public async Task Complete_OtherTokenAfterTheOwnerCompleted_DoesNotSeeTheStoredOutcome()
    {
        await using var world = await StartAsync(Producer(IngestTestWorld.OwnerId));
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        (await CompleteAsync(world, artifactId, NewKey())).Status.ShouldBe(HttpStatusCode.OK);

        var refused = await CompleteAsAsync(world, OtherCredential, artifactId);

        refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("artifact_not_found");
    }

    [Fact]
    public async Task Complete_LegacyArtifactWithoutOwner_KeepsTheEarlierRuleForAnyAuthorisedToken()
    {
        await using var world = await StartAsync(Producer(IngestTestWorld.OwnerId));
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.artifact set owner_credential_id = null where id = '{artifactId:D}'");

        var accepted = await CompleteAsAsync(world, OtherCredential, artifactId);

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Text("state").ShouldBe("ready");
    }

    [Fact]
    public async Task CompleteCommand_OtherCredential_IsRefusedByTheWriterEvenWithAnAcceptingVerdict_AndTheOwnerCommits()
    {
        await using var world = await StartAsync(Producer(IngestTestWorld.OwnerId));
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        var refused = await SendCommandAsync(world, OtherCredential, artifactId);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(EvidenceErrors.ArtifactNotFound.Code);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");

        var committed = await SendCommandAsync(world, IngestTestWorld.OwnerId, artifactId);

        committed.IsSuccess.ShouldBeTrue();
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
    }

    /// <summary>Dispatches the writer command with an accepting verdict, as a caller that skipped the verifier would.</summary>
    private static async Task<Evidgate.BuildingBlocks.SharedKernel.Results.Result<CompleteResponse>> SendCommandAsync(
        World world, string credentialId, Guid artifactId)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, world.Project);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>().Publish(Producer(credentialId));

        return await provider.GetRequiredService<IMediator>().Send(
            new CompleteArtifactUpload(artifactId, ArtifactCompletionVerdict.Accept(GoodBody.LongLength), credentialId, NewKey()),
            TestContext.Current.CancellationToken);
    }
}
