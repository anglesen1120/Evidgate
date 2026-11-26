// 06.1.2-T1: POST /v1/artifacts/upload-url through the real host and a real PostgreSQL.
// Request bodies are written inline so each test reads as the request it sends.
using System.Globalization;
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceUploadUrlTests
{
    // ------------------------------------------------------------------ AC1: the happy path

    [Fact]
    public async Task UploadUrl_HappyPath_Returns200_PendingKey_SignedHeaders()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var artifactId = answer.Id("artifact_id");
        answer.Text("object_key").ShouldBe($"pending/{world.Org:D}/{artifactId:D}");

        // The presigned PUT signs the declared length and the checksum (T18), so a client cannot upload
        // different bytes or a different length without invalidating the URL.
        var uploadUrl = answer.Text("upload_url")!;
        uploadUrl.ShouldContain("X-Amz-SignedHeaders=");
        uploadUrl.ShouldContain("content-length");
        uploadUrl.ShouldContain("x-amz-checksum-sha256");

        var requiredHeaders = answer.Body.GetProperty("required_headers");
        requiredHeaders.TryGetProperty("Content-Length", out var length).ShouldBeTrue();
        length.GetString().ShouldBe(Cap.ToString(CultureInfo.InvariantCulture));
        requiredHeaders.TryGetProperty("x-amz-checksum-sha256", out _).ShouldBeTrue();

        var expiresAt = DateTimeOffset.Parse(answer.Text("expires_at")!, CultureInfo.InvariantCulture);
        expiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        expiresAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(15).AddSeconds(5));

        // The row keeps the FINAL key (the staging key is only what the client PUTs to), pending_upload,
        // the declared size and scan_state pending; the slot is bound to the caller's token id (T9).
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await ArtifactColumnAsync(world, artifactId, "object_key"))
            .ShouldBe($"org/{world.Org:D}/project/{world.Project:D}/run/{runId:D}/attempt/{ExecutionKey}/1/{artifactId:D}");
        (await _fixture.ScalarAsync<long>(world.Database, $"select bytes from public.artifact where id = '{artifactId:D}'")).ShouldBe(Cap);
        (await ArtifactColumnAsync(world, artifactId, "scan_state")).ShouldBe("pending");
        (await SlotBindingAsync(world, runId)).ShouldBe(IngestTestWorld.OwnerId);
    }

    // ------------------------------------------------------------------ AC2: the slot and the planned pair

    [Fact]
    public async Task UploadUrl_UnknownExecutionKey_422()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, executionKey: OtherExecutionKey), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("unknown_execution_key");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_UnknownProducer_422()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, producerId: "shard-9"), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("unknown_producer");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_OtherIdentity_403_ProducerSlotTaken()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey, boundOwnerId: IngestTestWorld.OtherOwnerId);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("producer_slot_taken");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
        (await SlotBindingAsync(world, runId)).ShouldBe(IngestTestWorld.OtherOwnerId);
    }

    [Fact]
    public async Task UploadUrl_SameIdentityTwice_Succeeds_TwoArtifacts_WhenDifferentKeys()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var first = await UploadUrlAsync(world, Body(runId), NewKey());
        var second = await UploadUrlAsync(world, Body(runId), NewKey());

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.Id("artifact_id").ShouldNotBe(first.Id("artifact_id"));
        (await ArtifactRowsAsync(world)).ShouldBe(2);
        (await SlotBindingAsync(world, runId)).ShouldBe(IngestTestWorld.OwnerId);
    }

    [Fact]
    public async Task UploadUrl_RejectedRequest_DoesNotBindSlot()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        // The planned-pair check runs before the bind, so a refused request leaves the slot unbound (T9).
        var answer = await UploadUrlAsync(world, Body(runId, executionKey: OtherExecutionKey), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SlotBindingAsync(world, runId)).ShouldBeNull();
    }

    // ------------------------------------------------------------------ AC6: run scope

    [Fact]
    public async Task UploadUrl_CrossTenantRun_404_NoArtifactRow()
    {
        await using var world = await StartAsync(Writer());
        var (otherOrg, _) = await CreateOtherOrgAsync(world, Unique.Slug("globex"));
        var otherRun = await SeedRunAsync(world, ExecutionKey, org: otherOrg);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(otherRun), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_RunOfOtherProject_404()
    {
        await using var world = await StartAsync(Writer());
        using var setup = world.Api.CreateClient();
        var otherProject = await CreateProjectAsync(setup, world.User, world.Org, "other");
        var runId = await SeedRunAsync(world, ExecutionKey, project: otherProject);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_UnknownRun_404_Not500()
    {
        await using var world = await StartAsync(Writer());
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(Guid.CreateVersion7()), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task UploadUrl_ProjectDeleted_409_ProjectDeleting()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");
        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var answer = await UploadUrlAsync(world, Body(runId), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("project_deleting");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC8: Idempotency-Key

    [Fact]
    public async Task UploadUrl_MissingIdempotencyKey_Rejected_400()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId));

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_IdempotentReplay_SameBody_OneRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");
        var key = NewKey();

        var first = await UploadUrlAsync(world, Body(runId), key);
        var replay = await UploadUrlAsync(world, Body(runId), key);

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Id("artifact_id").ShouldBe(first.Id("artifact_id"));
        replay.Text("object_key").ShouldBe(first.Text("object_key"));
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task UploadUrl_IdempotencyConflict_409()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");
        var key = NewKey();

        var first = await UploadUrlAsync(world, Body(runId), key);
        var conflict = await UploadUrlAsync(world, Body(runId, bytes: Cap - 1), key);

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task UploadUrl_DifferentIdentitySameKey_409_NoUploadUrl()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");
        var key = NewKey();

        var first = await UploadUrlAsync(world, Body(runId), key);

        // A second principal of the same project replays the key with the same body: the token identity is part of
        // the idempotency hash (F3), so it is a conflict and never receives the first caller's presigned URL.
        await using var other = _hosts.WithPrincipal(
            world.Database, world.Org, world.Project, Writer(IngestTestWorld.OtherOwnerId), ObjectStorage);
        using var otherClient = other.CreateClient();
        var conflict = await otherClient.SendAsync(HttpMethod.Post, "/v1/artifacts/upload-url", _ => { }, Body(runId), key);

        var replay = await UploadUrlAsync(world, Body(runId), key);

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        conflict.Body.GetRawText().ShouldNotContain("upload_url");
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Id("artifact_id").ShouldBe(first.Id("artifact_id"));
        replay.Text("upload_url").ShouldBe(first.Text("upload_url"));
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }
}
