// The verification half of the completion suite: the HEAD size and checksum refusals, the
// failure counter measured on the commit, and the missing-object case. The rest of the
// suite is in EvidenceCompleteTests.cs and its other partial files.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC3: what the completion refuses — a checksum or size mismatch and a missing object — and
/// how a committed rejection is counted, once, on the commit, while a rolled-back attempt
/// counts nothing. <see cref="CompleteDirectAsync"/> is the harness the counter cases share.
/// </summary>
public sealed partial class EvidenceCompleteTests
{

    // ------------------------------------------------------------------ AC3: size/checksum verification

    [Fact]
    public async Task Complete_ChecksumMismatch_Rejects()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        // Declare GoodBody, but write OtherBody (same length, different bytes) straight to the staging key,
        // bypassing the signed checksum header the presigned URL would have enforced.
        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);
        var artifactId = minted.Id("artifact_id");
        var stagingKey = minted.Text("object_key")!;
        var finalKey = await ArtifactColumnAsync(world, artifactId, "object_key");
        await _store.PutAsync(stagingKey, OtherBody);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        // A rejection is a committed 200, not an error: the row persists with its reason.
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("checksum_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("checksum_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(finalKey!)).ShouldBeFalse("rejected bytes must never become evidence");
    }

    [Fact]
    public async Task Complete_SizeMismatch_Rejects()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);
        var artifactId = minted.Id("artifact_id");
        var stagingKey = minted.Text("object_key")!;
        var finalKey = await ArtifactColumnAsync(world, artifactId, "object_key");
        await _store.PutAsync(stagingKey, ShortBody);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("size_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("size_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(finalKey!)).ShouldBeFalse();
    }

    // ------------------------------------------------- counters: measured on the commit, never inside it

    /// <summary>
    /// The upload failure counter measures a committed verdict: the handler registers the measurement on the request's
    /// tenant scope, so the commit — and only the commit — counts the rejection, once and with its code. The refusal
    /// code is a member of the closed vocabulary no other suite completes with, since the instrument is process-wide.
    /// </summary>
    [Fact]
    public async Task Complete_Rejection_Is_Counted_Once_On_The_Commit()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var artifactId = await InsertArtifactAsync(world, world.Org, world.Project, runId);

        using var listener = IngestTelemetryCapture.ArtifactFailures(out var failures);
        var scopes = new RecordingTenantScopeAccessor(
            world.Host.Services.GetRequiredService<ITenantScopeAccessor>());

        var result = await CompleteDirectAsync(
            world,
            scopes,
            artifactId,
            ArtifactCompletionVerdict.Reject(ArtifactFailureCode.SecretRejected),
            commit: true);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        scopes.Last.ShouldNotBeNull();
        scopes.Last!.RegisteredCount.ShouldBe(1, "one rejected completion, one measurement registered for the commit");
        scopes.Last!.ExecutedCount.ShouldBe(1, "the commit is what runs the measurement");
        failures.Count(entry => entry == ("upload", ArtifactFailureCode.SecretRejected))
            .ShouldBe(1, "the rejection is counted once, after the commit");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
    }

    /// <summary>
    /// A rolled-back attempt records nothing: the row it rejected never became durable, so the counter must not name
    /// it. The registration proves the attempt meant to measure; the missing commit is what cancels it, and the code
    /// is again one no other suite emits, so the absence below is this attempt's alone.
    /// </summary>
    [Fact]
    public async Task Complete_Rejection_RolledBack_Is_Not_Counted()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);
        var artifactId = await InsertArtifactAsync(world, world.Org, world.Project, runId);

        using var listener = IngestTelemetryCapture.ArtifactFailures(out var failures);
        var scopes = new RecordingTenantScopeAccessor(
            world.Host.Services.GetRequiredService<ITenantScopeAccessor>());

        var result = await CompleteDirectAsync(
            world,
            scopes,
            artifactId,
            ArtifactCompletionVerdict.Reject(ArtifactFailureCode.CapExceeded),
            commit: false);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        scopes.Last.ShouldNotBeNull();
        scopes.Last!.RegisteredCount.ShouldBe(1, "the rejected attempt registered its measurement");
        scopes.Last!.ExecutedCount.ShouldBe(0, "a scope disposed without a commit runs no post-commit work");
        failures.ShouldNotContain(
            ("upload", ArtifactFailureCode.CapExceeded), "a rolled-back verdict is not a counted failure");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload", "the scope rolled back");
    }

    /// <summary>
    /// Runs the completion handler directly over a tenant scope this test owns, so the commit or the rollback is the
    /// test's own decision; the recording accessor it receives is how the counters above tell a deferred measurement
    /// from one taken inside the open transaction.
    /// </summary>
    /// <param name="world">The tenant the attempt runs as.</param>
    /// <param name="scopes">The ambient-scope accessor this attempt reads, recording what it registers.</param>
    /// <param name="artifactId">The artifact row the command addresses.</param>
    /// <param name="verdict">The verdict the completion verifier derives before this command commits it.</param>
    /// <param name="commit">Whether the attempt commits (otherwise it rolls back on disposal).</param>
    /// <returns>The handler's answer.</returns>
    private static async Task<Result<CompleteResponse>> CompleteDirectAsync(
        World world,
        ITenantScopeAccessor scopes,
        Guid artifactId,
        ArtifactCompletionVerdict verdict,
        bool commit)
    {
        var services = world.Host.Services;
        var audit = services.GetRequiredService<IAuditTrail>();
        var handler = new CompleteArtifactUploadHandler(
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            services.GetRequiredService<IOutboxWriter>(),
            audit,
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IOptions<ParseRunnerOptions>>(),
            services.GetRequiredService<IRawReportProducerKindReader>(),
            scopes);

        var factory = services.GetRequiredService<ITenantScopeFactory>();
        var token = TestContext.Current.CancellationToken;

        // The handler describes its audit entry, which only a pipeline unit of work accepts.
        using var auditUnit = audit.BeginUnit();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, cancellationToken: token);
        var result = await handler.Handle(
            new CompleteArtifactUpload(artifactId, verdict, CredentialId: null, NewKey()), token);

        if (commit)
        {
            await scope.CommitAsync(token);
        }

        return result;
    }

    [Fact]
    public async Task Complete_MissingObject_Rejects()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        // Mint only: nothing was ever PUT, so neither the staging nor the final object exists.
        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);
        var artifactId = minted.Id("artifact_id");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("object_missing");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }
}
