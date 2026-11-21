// 06.1.2-T1: POST /v1/artifacts/upload-url through the real host and a real PostgreSQL.
// Request bodies are written inline so each test reads as the request it sends.
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceUploadUrlTests
{
    // ------------------------------------------------------------------ the kind policy

    [Fact]
    public async Task UploadUrl_NoPolicyRow_KindDisabled_422()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);

        // A kind with no static default (06.1.2-T3.1): trace/video/screenshot/raw_report now fall back to the
        // static per-kind defaults, so dom_snapshot — one of the four kinds D-A9 fixes no cap for — is the one
        // that still fails closed with no policy row. It is the same 422 the refusal carried before the defaults.
        var answer = await UploadUrlAsync(world, Body(runId, kind: "dom_snapshot", mediaType: "application/json"), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_disabled");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_CaptureDisabled_KindDisabled_422()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace", captureEnabled: false);

        var answer = await UploadUrlAsync(world, Body(runId), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_disabled");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_MediaTypeNotAllowed_422()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace", mediaTypes: [ZipType]);

        var refused = await UploadUrlAsync(world, Body(runId, mediaType: "text/plain"), NewKey());

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("media_type_not_allowed");
        (await ArtifactRowsAsync(world)).ShouldBe(0);

        // A parameter on an allowed base type is accepted: the handler strips ";params" before the match.
        var accepted = await UploadUrlAsync(world, Body(runId, mediaType: ZipType + "; charset=utf-8"), NewKey());

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
    }

    [Fact]
    public async Task UploadUrl_TooLarge_413()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace", maxBytes: Cap);

        var answer = await UploadUrlAsync(world, Body(runId, bytes: Cap + 1), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("payload_too_large");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_AtCap_Accepted_AboveCap_Rejected()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace", maxBytes: Cap);

        var atCap = await UploadUrlAsync(world, Body(runId, bytes: Cap), NewKey());
        var aboveCap = await UploadUrlAsync(world, Body(runId, bytes: Cap + 1), NewKey());

        atCap.Status.ShouldBe(HttpStatusCode.OK, atCap.ErrorMessage);
        aboveCap.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        aboveCap.ErrorCode.ShouldBe("payload_too_large");
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the mint's named 422s

    [Fact]
    public async Task UploadUrl_BadChecksum_422_ChecksumInvalid()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, checksum: "sha256:zzzz"), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("checksum_invalid");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_ZeroBytes_422_SizeOutOfRange()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, bytes: 0), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("size_out_of_range");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_ClientObjectKeyField_Ignored()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        // The wire contract has no client key member, so a stray field is ignored and the key stays server-chosen.
        var answer = await UploadUrlAsync(
            world,
            new
            {
                run_id = runId,
                producer_id = ProducerId,
                execution_key = ExecutionKey,
                attempt_no = 1,
                kind = "trace",
                media_type = ZipType,
                bytes = Cap,
                sha256 = Checksum,
                client_object_key = "org/other/project/other/run/other/attempt/x/1/y",
            },
            NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("object_key").ShouldBe($"pending/{world.Org:D}/{answer.Id("artifact_id"):D}");
    }

    // ------------------------------------------------------------------ authorization and redaction

    [Fact]
    public async Task UploadUrl_WithoutArtifactWrite_403()
    {
        await using var world = await StartAsync(Reader());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_ErrorResponses_DoNotContainSignature()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, executionKey: OtherExecutionKey), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = answer.Body.GetRawText();
        body.ShouldNotContain("X-Amz");
        body.ShouldNotContain("Signature");
        body.ShouldNotContain("upload_url");
    }

    // ------------------------------------------------------------------ P3: bounded statements

    [Fact]
    public async Task UploadUrl_Issues_BoundedStatements()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        world.Api.Statements.Clear();
        world.Host.Statements.Clear();
        var answer = await UploadUrlAsync(world, Body(runId), NewKey());
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        // One request is a constant number of statements (P3): the idempotency claim, the run scope, the kind
        // policy, the slot read + bind, the mint read + insert, the audit row and the idempotency completion.
        var statements = world.Host.Statements.Snapshot();
        statements.Count.ShouldBeGreaterThan(0);
        statements.Count.ShouldBeLessThanOrEqualTo(16);
    }
}
