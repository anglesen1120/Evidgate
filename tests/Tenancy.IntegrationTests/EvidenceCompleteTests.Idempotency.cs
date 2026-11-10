// The idempotency half of the completion suite: AC5's promotion of an already-ready
// artifact and the retry path, then AC8's Idempotency-Key contract. The rest of the suite
// is in EvidenceCompleteTests.cs and its other partial files.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC5 and AC8: a second completion adds no work, a stored response replays byte for byte,
/// a rejected artifact returns its current state, and the <c>Idempotency-Key</c> is required
/// and bound to the caller's identity.
/// </summary>
public sealed partial class EvidenceCompleteTests
{

    // ------------------------------------------------------------------ AC5: idempotency and the retry path

    [Fact]
    public async Task Complete_Twice_SameBody_NoSecondOutboxRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        var first = await CompleteAsync(world, artifactId, NewKey());
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Text("state").ShouldBe("ready");

        // A fresh key forces the handler to run again on an already-ready artifact (not a stored-response replay).
        var second = await CompleteAsync(world, artifactId, NewKey());
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.Text("state").ShouldBe("ready");
        second.Body.GetProperty("bytes").GetInt64().ShouldBe(first.Body.GetProperty("bytes").GetInt64());

        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
    }

    [Fact]
    public async Task Complete_IdempotentReplay_SameBody()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        var key = NewKey();

        var first = await CompleteAsync(world, artifactId, key);
        var replay = await CompleteAsync(world, artifactId, key);

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Id("artifact_id").ShouldBe(first.Id("artifact_id"));
        replay.Text("state").ShouldBe(first.Text("state"));
        replay.Body.GetProperty("bytes").GetInt64().ShouldBe(first.Body.GetProperty("bytes").GetInt64());
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
    }

    [Fact]
    public async Task Complete_OnRejected_ReturnsCurrentState()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var minted = await UploadUrlAsync(world, UploadBody(runId, GoodBody), NewKey());
        var artifactId = minted.Id("artifact_id");
        await _store.PutAsync(minted.Text("object_key")!, OtherBody);

        var rejected = await CompleteAsync(world, artifactId, NewKey());
        rejected.Status.ShouldBe(HttpStatusCode.OK, rejected.ErrorMessage);
        rejected.Text("state").ShouldBe("rejected");
        rejected.Text("failure_code").ShouldBe("checksum_mismatch");

        // A second complete on a rejected artifact returns the current state, unchanged and without new work.
        var again = await CompleteAsync(world, artifactId, NewKey());
        again.Status.ShouldBe(HttpStatusCode.OK, again.ErrorMessage);
        again.Text("state").ShouldBe("rejected");
        again.Text("failure_code").ShouldBe("checksum_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
    }

    [Fact]
    public async Task Complete_StagingGone_FinalMatches_Commits()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        // Simulate a previous complete that copied staging → final but crashed before its commit: the final
        // object holds the declared bytes (same size, same checksum) and the staging object is gone.
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);
        await _store.PutAsync(finalKey, GoodBody);
        await _store.DeleteAsync(stagingKey);
        (await _store.ExistsAsync(stagingKey)).ShouldBeFalse();
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue();

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(GoodBody);
    }

    // ------------------------------------------------------------------ AC8: Idempotency-Key

    [Fact]
    public async Task Complete_MissingIdempotencyKey_Rejected()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);

        var answer = await CompleteAsync(world, artifactId);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task Complete_DifferentPrincipalSameKey_409()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, GoodBody);
        var key = NewKey();

        var first = await CompleteAsync(world, artifactId, key);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);

        // The caller's token identity is part of the idempotency hash (F3): another principal replaying the
        // same key is a conflict, never the first caller's stored response.
        await using var other = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project, Writer(OtherJti), _store.Configuration());
        using var otherClient = other.CreateClient();
        var conflict = await otherClient.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
    }
}
