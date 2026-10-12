// 06.1.2-T1 QA: negative, boundary and concurrency cases for POST /v1/artifacts/upload-url.
// Expectations come from the plan (06.1.2 §2 AC1/AC2/AC6/AC8, §5, §8, §9) and the coordinator rulings,
// not from the current behaviour. A failing test here is a defect to report, not a reason to weaken it.
#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class EvidenceUploadUrlQaTests
{
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ZipType = "application/zip";
    private const string Jti = "jti-upload-1";
    private const string OtherJti = "jti-upload-2";
    private const string ProducerId = "shard-1";
    private const long Cap = 1024;

    private static readonly Dictionary<string, string?> ObjectStorage = new(StringComparer.Ordinal)
    {
        ["EVIDGATE:Object:Bucket"] = "evidgate-staging",
        ["EVIDGATE:Object:Region"] = "us-east-1",
        ["EVIDGATE:Object:Endpoint"] = "http://127.0.0.1:9",
        ["EVIDGATE:Object:ForcePathStyle"] = "true",
        ["EVIDGATE:OBJECT:ACCESSKEY"] = "test-access-key",
        ["EVIDGATE:OBJECT:SECRETKEY"] = "test-secret-key",
    };

    private readonly PostgresTenancyFixture _fixture;

    public EvidenceUploadUrlQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        TenancyApiFactory Host,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    private static RequestPrincipal Writer(string jti = Jti) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti);

    /// <summary>A caller with <c>artifact:write</c> but no token identity (no <c>jti</c> to bind a slot to).</summary>
    private static RequestPrincipal NoIdentityWriter() =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: null);

    private async Task<World> StartAsync(RequestPrincipal principal)
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, "acme");
            project = await CreateProjectAsync(setup, user, org, "checkout");
        }

        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database), org, project, principal, ObjectStorage);

        return new World(api, host, host.CreateClient(), database, user, org, project);
    }

    private async Task<Guid> SeedRunAsync(World world, string executionKey)
    {
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var testRevision = await scope.ScalarAsync<Guid>(
            "select id from public.test_revision where org_id = @org_id limit 1", ("org_id", world.Org));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId), ("execution_key", executionKey),
            ("case_revision_id", testRevision), ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        (await scope.ExecuteAsync(
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
            + "values (@id, @org_id, @run_id, 1, @producer_id, null)",
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId), ("producer_id", ProducerId))).ShouldBe(1);

        await scope.CommitAsync();

        return runId;
    }

    private async Task InsertKindPolicyAsync(World world, string kind, long maxBytes = Cap)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{kind}', {maxBytes}, array['{ZipType}'], 'normal', true)"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>The request body with every numeric and text field open to a raw value (boundary inputs).</summary>
    private static object Body(
        Guid runId,
        object attemptNo,
        object bytes,
        string checksum = Checksum,
        string executionKey = ExecutionKey,
        string producerId = ProducerId,
        string kind = "trace",
        string mediaType = ZipType) =>
        new
        {
            run_id = runId,
            producer_id = producerId,
            execution_key = executionKey,
            attempt_no = attemptNo,
            kind,
            media_type = mediaType,
            bytes,
            sha256 = checksum,
        };

    private static Task<ApiAnswer> UploadUrlAsync(World world, object body, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/artifacts/upload-url", _ => { }, body, idempotencyKey);

    private Task<long> ArtifactRowsAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.artifact");

    private Task<string?> SlotBindingAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<string?>(world.Database, $"select bound_token_jti from public.producer_slot where run_id = '{runId:D}'");

    /// <summary>A 4xx refusal: the client was refused, and the server did not fail (no 5xx).</summary>
    private static void ShouldBeClientRefusal(ApiAnswer answer)
    {
        var refused = answer.Status >= HttpStatusCode.BadRequest && answer.Status < HttpStatusCode.InternalServerError;
        refused.ShouldBeTrue($"expected a 4xx refusal; status={(int)answer.Status} {answer.ErrorMessage}");
    }

    // ------------------------------------------------------------------ AC1 boundaries: attempt_no, bytes

    [Fact]
    public async Task UploadUrl_AttemptNoZero_IsRefused_NoRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 0, bytes: Cap), Guid.NewGuid().ToString("N"));

        ShouldBeClientRefusal(answer);
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_AttemptNoAboveInt32_IsRefused_NoRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 2147483648L, bytes: Cap), Guid.NewGuid().ToString("N"));

        ShouldBeClientRefusal(answer);
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_NegativeBytes_IsSizeOutOfRange_422()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: -1L), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("size_out_of_range");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_Int64MaxBytes_IsPayloadTooLarge_413()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: long.MaxValue), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("payload_too_large");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC1 boundaries: checksum and text

    [Fact]
    public async Task UploadUrl_ChecksumUpperCaseHex_IsChecksumInvalid_AndSlotStaysUnbound()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var upper = "sha256:" + new string('B', 64);
        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap, checksum: upper), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("checksum_invalid");
        (await ArtifactRowsAsync(world)).ShouldBe(0);

        // The checksum shape check runs before the producer-slot bind (F1), so the slot is never bound.
        (await SlotBindingAsync(world, runId)).ShouldBeNull();
    }

    [Fact]
    public async Task UploadUrl_ChecksumWithoutPrefix_IsChecksumInvalid()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var bare = new string('b', 64);
        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap, checksum: bare), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("checksum_invalid");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_ChecksumWithTrailingNewline_IsChecksumInvalid()
    {
        // ArtifactShape anchors with \A...\z; a $-anchored pattern would accept the trailing newline.
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var withNewline = "sha256:" + new string('b', 64) + "\n";
        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap, checksum: withNewline), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("checksum_invalid");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_ExecutionKeyUpperCaseHex_IsRefused_NoRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(
            world,
            Body(runId, attemptNo: 1, bytes: Cap, executionKey: ExecutionKey.ToUpperInvariant()),
            Guid.NewGuid().ToString("N"));

        ShouldBeClientRefusal(answer);
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_ProducerIdWithTrailingNewline_IsRefusedAsCharsetViolation_422()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(
            world,
            Body(runId, attemptNo: 1, bytes: Cap, producerId: ProducerId + "\n"),
            Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        // The id is shape-checked by the binder against ProducerSlotLimits.ProducerIdPattern (\z-anchored):
        // a trailing newline is a charset violation (ProducerRegistry.cs:126-127), not an unregistered producer.
        answer.ErrorCode.ShouldBe("request_invalid");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_MediaTypeWithTrailingNewline_IsMediaTypeNotAllowed_422()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(
            world,
            Body(runId, attemptNo: 1, bytes: Cap, mediaType: ZipType + "\n"),
            Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("media_type_not_allowed");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_KindWithoutPolicyRow_IsKindDisabled_NotKindUnknown()
    {
        // The policy is the only authority: a KNOWN kind with no policy row fails closed as kind_disabled
        // (coordinator ruling). An unknown kind is refused earlier as kind_unknown (see the test below).
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);

        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap, kind: "trace"), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_disabled");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadUrl_UnknownKind_IsKindUnknown_422_NoRow_SlotUnbound()
    {
        // The kind shape check runs before any database work (F1), so an unknown kind is kind_unknown even with no
        // policy row, and the producer slot is never touched.
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);

        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap, kind: "bogus_kind"), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_unknown");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
        (await SlotBindingAsync(world, runId)).ShouldBeNull();
    }

    [Fact]
    public async Task UploadUrl_OverLongKindAndChecksum_AreRequestInvalid_422()
    {
        // The validator caps the kind at 64 and the checksum at 128 code points (F4): an over-long value is a
        // request_invalid before the handler runs, while an at-cap malformed value still gets its named code.
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var overLongKind = await UploadUrlAsync(
            world, Body(runId, attemptNo: 1, bytes: Cap, kind: new string('k', 65)), Guid.NewGuid().ToString("N"));
        var overLongChecksum = await UploadUrlAsync(
            world, Body(runId, attemptNo: 1, bytes: Cap, checksum: "sha256:" + new string('b', 122)), Guid.NewGuid().ToString("N"));

        overLongKind.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        overLongKind.ErrorCode.ShouldBe("request_invalid");
        overLongChecksum.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        overLongChecksum.ErrorCode.ShouldBe("request_invalid");

        var atCapKind = await UploadUrlAsync(
            world, Body(runId, attemptNo: 1, bytes: Cap, kind: new string('k', 64)), Guid.NewGuid().ToString("N"));
        var atCapChecksum = await UploadUrlAsync(
            world, Body(runId, attemptNo: 1, bytes: Cap, checksum: "sha256:" + new string('b', 121)), Guid.NewGuid().ToString("N"));

        atCapKind.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        atCapKind.ErrorCode.ShouldBe("kind_unknown");
        atCapChecksum.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        atCapChecksum.ErrorCode.ShouldBe("checksum_invalid");

        (await ArtifactRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC2: identity and slot binding

    [Fact]
    public async Task UploadUrl_TokenWithoutJti_IsForbidden_NoRow_SlotUnbound()
    {
        await using var world = await StartAsync(NoIdentityWriter());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await ArtifactRowsAsync(world)).ShouldBe(0);
        (await SlotBindingAsync(world, runId)).ShouldBeNull();
    }

    [Fact]
    public async Task UploadUrl_TwoIdentitiesRaceForUnboundSlot_ExactlyOneWins()
    {
        await using var world = await StartAsync(Writer(Jti));
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        await using var second = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project, Writer(OtherJti), ObjectStorage);
        using var secondClient = second.CreateClient();

        var answers = await Task.WhenAll(
            UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap), Guid.NewGuid().ToString("N")),
            secondClient.SendAsync(
                HttpMethod.Post,
                "/v1/artifacts/upload-url",
                _ => { },
                Body(runId, attemptNo: 1, bytes: Cap),
                Guid.NewGuid().ToString("N")));

        var summary = string.Join(" | ", answers.Select(a => $"{(int)a.Status}:{a.ErrorCode}"));
        answers.Count(a => a.Status == HttpStatusCode.OK).ShouldBe(1, summary);
        answers.Count(a => a.Status == HttpStatusCode.Forbidden && a.ErrorCode == "producer_slot_taken").ShouldBe(1, summary);
        (await ArtifactRowsAsync(world)).ShouldBe(1);

        var winnerJti = answers[0].Status == HttpStatusCode.OK ? Jti : OtherJti;
        (await SlotBindingAsync(world, runId)).ShouldBe(winnerJti);
    }

    [Fact]
    public async Task UploadUrl_PathFirstWriteBindsTheSlot_ThenForeignIdentityIsForbidden()
    {
        // T85/T9: 06.1.2's route is a first-write path of its own - the first accepted upload-url claims the
        // slot through the same BindOrCheckAsync the event batch uses, and the foreign identity is refused.
        await using var world = await StartAsync(Writer(Jti));
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var accepted = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap), Guid.NewGuid().ToString("N"));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await SlotBindingAsync(world, runId)).ShouldBe(Jti);

        await using var second = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project, Writer(OtherJti), ObjectStorage);
        using var secondClient = second.CreateClient();

        var foreign = await secondClient.SendAsync(
            HttpMethod.Post,
            "/v1/artifacts/upload-url",
            _ => { },
            Body(runId, attemptNo: 2, bytes: Cap),
            Guid.NewGuid().ToString("N"));

        foreign.Status.ShouldBe(HttpStatusCode.Forbidden, foreign.ErrorMessage);
        foreign.ErrorCode.ShouldBe("producer_slot_taken");
        (await ArtifactRowsAsync(world)).ShouldBe(1);
        (await SlotBindingAsync(world, runId)).ShouldBe(Jti);
    }

    [Fact]
    public async Task UploadUrl_SameKeyConcurrent_NoServerError_OneArtifactRow()
    {
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");
        var key = Guid.NewGuid().ToString("N");

        var answers = await Task.WhenAll(
            UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap), key),
            UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap), key));

        var summary = string.Join(" | ", answers.Select(a => $"{(int)a.Status}:{a.ErrorCode}"));
        answers.ShouldAllBe(a => (int)a.Status < 500, summary);
        answers.Count(a => a.Status == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1, summary);
        answers.Where(a => a.Status == HttpStatusCode.OK).Select(a => a.Id("artifact_id")).Distinct().Count()
            .ShouldBe(1, summary);
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC8 / §8: the presigned URL

    [Fact]
    public async Task UploadUrl_PresignedUrl_IsOnConfiguredEndpoint_PathStyle_WithFixedExpiry()
    {
        // The host and path come only from configuration; the body carries no host, so a client cannot
        // redirect the PUT (§8 presign detail). ForcePathStyle=true puts the bucket in the path.
        await using var world = await StartAsync(Writer());
        var runId = await SeedRunAsync(world, ExecutionKey);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId, attemptNo: 1, bytes: Cap), Guid.NewGuid().ToString("N"));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var artifactId = answer.Id("artifact_id");
        var uploadUrl = answer.Text("upload_url")!;
        uploadUrl.ShouldStartWith($"http://127.0.0.1:9/evidgate-staging/pending/{world.Org:D}/{artifactId:D}?");
        uploadUrl.ShouldContain("X-Amz-Expires=900");
    }
}
