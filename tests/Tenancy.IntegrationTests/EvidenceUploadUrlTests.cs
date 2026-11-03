// 06.1.2-T1: POST /v1/artifacts/upload-url through the real host and a real PostgreSQL.
// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T1 (AC1, AC2, AC6, AC8) through the real C2 pipeline and a real PostgreSQL: the presigned
/// <c>POST /v1/artifacts/upload-url</c> — run scope, the caller's bound producer slot, the manifest's planned
/// <c>(execution_key, attempt_no)</c>, the kind policy, the minted <c>pending/</c> staging key and the signed
/// <c>Content-Length</c> + checksum headers. The object-store adapter is CPU-only (presigning is SigV4
/// arithmetic), so no storage container is needed; the endpoint is configured with an unreachable local
/// endpoint and the two credentials through the same in-memory configuration the CI-key tests use.
/// <para>
/// The caller is published by a test startup filter (the same seam <see cref="TenancyApiFactory"/> uses for its
/// header stand-in), because a service principal with a <c>CredentialId</c> (the slot-binding identity, T9) and
/// an optional <see cref="CiRunReference"/> cannot be expressed through request headers.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceUploadUrlTests
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A second well-formed execution key, never planned in the seeded run.</summary>
    private const string OtherExecutionKey = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>A well-formed declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string ZipType = "application/zip";

    /// <summary>The caller's token id (the slot-binding identity, T9).</summary>
    private const string Jti = "jti-upload-1";

    /// <summary>Another identity's token id, for the <c>producer_slot_taken</c> case.</summary>
    private const string OtherJti = "jti-upload-2";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries.</summary>
    private const long Cap = 1024;

    /// <summary>
    /// The object-store configuration the endpoint needs: an unreachable local endpoint (presigning never
    /// connects), path-style addressing, and the two credentials under the <c>SecretNames</c> configuration
    /// spelling the environment secret provider reads (10.1.1-T2).
    /// </summary>
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

    public EvidenceUploadUrlTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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

    /// <summary>A service principal holding <c>artifact:write</c> and a token id (the slot-binding identity).</summary>
    private static RequestPrincipal Writer(string jti = Jti) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti);

    /// <summary>A service principal without <c>artifact:write</c>.</summary>
    private static RequestPrincipal Reader() =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Read], credentialId: Jti);

    /// <summary>
    /// One migrated database with an org and a project created through the real registry routes, plus a host
    /// whose requests carry <paramref name="principal"/> and the object-store configuration.
    /// </summary>
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

    /// <summary>A second org with a project, created through the real routes (the cross-tenant case).</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherOrgAsync(World world, string slug)
    {
        using var setup = world.Api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(setup, user, slug);

        return (org, await CreateProjectAsync(setup, user, org, slug + "-project"));
    }

    /// <summary>
    /// One committed run (through <see cref="RunRowSeeder"/>), one planned execution and one producer slot for
    /// <paramref name="org"/> — the parents 0031's composite keys need. <paramref name="project"/> moves the run
    /// to another project of the same org (the run-of-another-project case).
    /// </summary>
    private async Task<Guid> SeedRunAsync(
        World world,
        string executionKey,
        Guid? org = null,
        string producerId = ProducerId,
        string? boundJti = null,
        Guid? project = null)
    {
        var owningOrg = org ?? world.Org;
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, owningOrg);

        if (project is { } moved)
        {
            await _fixture.ExecuteAsync(
                world.Database,
                $"update public.run set project_id = '{moved:D}' where id = '{runId:D}'");
        }

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), owningOrg);
        var testRevision = await scope.ScalarAsync<Guid>(
            "select id from public.test_revision where org_id = @org_id limit 1", ("org_id", owningOrg));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", owningOrg), ("run_id", runId), ("execution_key", executionKey),
            ("case_revision_id", testRevision), ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        (await scope.ExecuteAsync(
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
            + "values (@id, @org_id, @run_id, 1, @producer_id, @jti)",
            ("id", Guid.CreateVersion7()), ("org_id", owningOrg), ("run_id", runId), ("producer_id", producerId),
            ("jti", (object?)boundJti ?? DBNull.Value))).ShouldBe(1);

        await scope.CommitAsync();

        return runId;
    }

    /// <summary>One committed kind-policy row for <paramref name="world"/>'s project, as the app role.</summary>
    private async Task InsertKindPolicyAsync(
        World world,
        string kind,
        long maxBytes = Cap,
        string[]? mediaTypes = null,
        bool captureEnabled = true)
    {
        var types = mediaTypes ?? [ZipType];
        var array = "array[" + string.Join(",", types.Select(type => $"'{type}'")) + "]";

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{kind}', {maxBytes}, {array}, 'normal', {(captureEnabled ? "true" : "false")})"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>The upload-url request body (00 §9.3, snake_case wire contract).</summary>
    private static object Body(
        Guid runId,
        string executionKey = ExecutionKey,
        string producerId = ProducerId,
        int attemptNo = 1,
        string kind = "trace",
        string mediaType = ZipType,
        long bytes = Cap,
        string checksum = Checksum) =>
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

    /// <summary>One upload-url request; the caller is the host's published principal.</summary>
    private static Task<ApiAnswer> UploadUrlAsync(World world, object body, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/artifacts/upload-url", _ => { }, body, idempotencyKey);

    private Task<long> ArtifactRowsAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.artifact");

    private Task<string> ArtifactColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string>(world.Database, $"select {column} from public.artifact where id = '{artifactId:D}'");

    private Task<string?> SlotBindingAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<string?>(world.Database, $"select bound_token_jti from public.producer_slot where run_id = '{runId:D}'");

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
        (await SlotBindingAsync(world, runId)).ShouldBe(Jti);
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
        var runId = await SeedRunAsync(world, ExecutionKey, boundJti: OtherJti);
        await InsertKindPolicyAsync(world, "trace");

        var answer = await UploadUrlAsync(world, Body(runId), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("producer_slot_taken");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
        (await SlotBindingAsync(world, runId)).ShouldBe(OtherJti);
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
        (await SlotBindingAsync(world, runId)).ShouldBe(Jti);
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
        var (otherOrg, _) = await CreateOtherOrgAsync(world, "globex");
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
        await using var other = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project, Writer(OtherJti), ObjectStorage);
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
