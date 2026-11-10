// The staging half of the completion suite: the happy path and T86's rule that only the
// outbox task deletes the staging object, after the commit. The rest of the suite is in
// EvidenceCompleteTests.cs and its other partial files.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC4 through the real pipeline: the verified bytes are promoted to the final key and both
/// outbox rows are written in the committing transaction, while the staging object survives
/// the commit and is deleted by the <c>artifact_staging_delete</c> task only afterwards.
/// </summary>
public sealed partial class EvidenceCompleteTests
{

    // ------------------------------------------------------------------ AC4: the happy path

    [Fact]
    public async Task Upload_Complete_HappyPath()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Id("artifact_id").ShouldBe(artifactId);
        answer.Text("state").ShouldBe("ready");
        answer.Body.GetProperty("bytes").GetInt64().ShouldBe(GoodBody.LongLength);
        answer.Text("scan_state").ShouldBe("pending");
        answer.Body.GetProperty("failure_code").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        // The row is ready with the FINAL key, the HEAD size and scan_state pending; the staging key is never stored.
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await ArtifactColumnAsync(world, artifactId, "object_key")).ShouldBe(finalKey);
        (await _fixture.ScalarAsync<long>(world.Database, $"select bytes from public.artifact where id = '{artifactId:D}'"))
            .ShouldBe(GoodBody.LongLength);
        (await ArtifactColumnAsync(world, artifactId, "scan_state")).ShouldBe("pending");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBeNull();

        // The verified bytes were promoted to the final key.
        (await _store.ReadAsync(finalKey)).ShouldBe(GoodBody);

        // Exactly one async-scan row and one staging-delete row, both in the same commit (T19/T86).
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC4: staging delete only after commit

    [Fact]
    public async Task Complete_StagingDelete_OnlyAfterCommit()
    {
        // The dispatcher claims undispatched outbox rows of every tenant, so this test needs a database of its own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(Writer(), dedicated.Name);
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, GoodBody);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the staging object must exist after the PUT");

        var answer = await CompleteAsync(world, artifactId, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        // AC4: the commit is the boundary — the staging object is still there, and the outbox row that
        // will delete it exists. The API host runs no dispatcher, so no task row exists yet.
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue(
            "the staging object must survive the commit: it is deleted by the outbox task, never before");
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue("the verified bytes must be promoted before the commit");
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await TaskRowsAsync(world, StagingDeleteTask)).ShouldBe(0);

        // The outbox hop (01.5.1-T3) moves the rows into the task queue...
        await DispatchAsync(world);
        (await TaskRowsAsync(world, StagingDeleteTask)).ShouldBe(1);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the dispatch alone must not delete the staging object");

        // ...and the async scan row lands too, but its handler is 06.2.3-T1: no consumer claims the type,
        // so the row stays queued and `ready` never waits for the scan (T19).
        (await TaskRowsAsync(world, SecretScanTask)).ShouldBe(1);
        (await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select state from public.task where task_type = '{SecretScanTask}' and org_id = '{world.Org:D}'"))
            .ShouldBe("queued");

        // ...and the task handler deletes it, idempotently, after the commit.
        var payload = await StagingDeletePayloadAsync(world, artifactId);
        payload.ShouldNotBeNull();
        await RunStagingDeleteAsync(world, payload!);

        (await _store.ExistsAsync(stagingKey)).ShouldBeFalse("the staging-delete handler must remove the staging object");
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue("the final object must never be touched by the staging delete");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
    }

    [Fact]
    public async Task Complete_WithoutCommit_StagingUntouched()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world);

        var (artifactId, stagingKey, _) = await MintAndUploadAsync(world, runId, GoodBody);

        // No complete: the commit never happens, so nothing is deleted and no work is enqueued.
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue();
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }
}
