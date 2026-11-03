// 06.1.2-T3.1/T3.2: the static per-kind defaults (DECISIONS §8.1 D-A9, coordinator decision D-S46-Q1) resolved
// against a project's `artifact_kind_policy` row — the mint's 413/422 boundary and the completion's re-check come
// from the one resolution, the endpoint's request-body limit is published as a contract constant, and the four
// kinds D-A9 fixes no cap for stay fail-closed without a row. Every test drives the real host, a real PostgreSQL
// and (for the completion cases) a real SeaweedFS S3 container through the same harness the sibling 06.1.2 suites
// use. Expectations come from the plan (06.1.2 §10 T3, D-A9), the coordinator decision and the published
// contract, never from the current behaviour.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T3.1/T3.2 (AC1): a kind with a static default is captured with it when the project has no policy row —
/// at the default cap, with the default media types and the kind's default sensitivity — while a project row
/// still wins where the decision says it must (its media types replace the default list, its capture switch
/// refuses the kind, its sensitivity is inherited, and its cap may lower but never raise the default). The four
/// kinds without a default keep the fail-closed 422 <c>kind_disabled</c>, and the completion re-checks the same
/// effective policy before it promotes anything.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceKindDefaultsTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A well-formed declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>The caller's token id (the slot-binding identity, T9).</summary>
    private const string Jti = "jti-kind-defaults-1";

    private const string ProducerId = "shard-1";

    private const string ZipType = "application/zip";
    private const string Mp4Type = "video/mp4";
    private const string PngType = "image/png";
    private const string JpegType = "image/jpeg";
    private const string XmlType = "application/xml";
    private const string TextXmlType = "text/xml";
    private const string JsonType = "application/json";
    private const string HtmlType = "text/html";

    /// <summary>
    /// One defaulted kind as D-A9 and D-S46-Q1 fix it: the cap, the accepted media types, the sensitivity
    /// <c>ArtifactSensitivity.ForKind</c> gives it, and a media type the kind must refuse.
    /// </summary>
    private sealed record DefaultKind(string Kind, long Cap, string[] MediaTypes, string Sensitivity, string RefusedMediaType);

    /// <summary>The four kinds with a static default, with the published numbers (not read back from the code).</summary>
    private static readonly DefaultKind[] Defaults =
    [
        new("trace", 100L * 1024 * 1024, [ZipType], "sensitive", Mp4Type),
        new("video", 200L * 1024 * 1024, [Mp4Type], "normal", ZipType),
        new("screenshot", 10L * 1024 * 1024, [PngType, JpegType], "normal", "image/gif"),
        new("raw_report", 256L * 1024 * 1024, [XmlType, TextXmlType, JsonType, HtmlType, ZipType], "normal", PngType),
    ];

    /// <summary>The four 00 §7 kinds D-A9 fixes no cap for: they keep the fail-closed refusal (06.1.2-T3.1).</summary>
    private static readonly string[] NoDefaultKinds = ["dom_snapshot", "a11y_snapshot", "patch", "ui_hierarchy"];

    private sealed record World(
        TenancyApiFactory Api,
        TenancyApiFactory Host,
        HttpClient Client,
        DedicatedDatabase Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => WorldDisposal.DisposeAsync(Client, Database, Host, Api);
    }

    /// <summary>A service principal holding <c>artifact:write</c> and a token id (the slot-binding identity).</summary>
    private static RequestPrincipal Writer(string jti = Jti) =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: jti);

    /// <summary>
    /// One migrated database with an org and a project created through the real registry routes, plus a host
    /// whose requests carry <paramref name="principal"/> and the SeaweedFS container's object-store configuration.
    /// </summary>
    /// <remarks>The "no row minted" assertions count every artifact of the database, so each world owns one.</remarks>
    private Task<World> StartAsync(RequestPrincipal? principal = null) =>
        fixture.StartDedicatedAsync(async database =>
        {
            var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
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
                fixture.AppConnectionString(database), org, project, principal ?? Writer(), store.Configuration());

            return new World(api, host, host.CreateClient(), database, user, org, project);
        });

    /// <summary>
    /// One committed run, one planned execution and one producer slot for the world's org — the parents the
    /// artifact mint needs (through <see cref="RunRowSeeder"/>).
    /// </summary>
    private async Task<Guid> SeedRunAsync(World world)
    {
        var runId = await RunRowSeeder.SeedRunAsync(fixture, world.Database, world.Org);

        await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org);
        var testRevision = await scope.ScalarAsync<Guid>(
            "select id from public.test_revision where org_id = @org_id limit 1", ("org_id", world.Org));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId), ("execution_key", ExecutionKey),
            ("case_revision_id", testRevision), ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        (await scope.ExecuteAsync(
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
            + "values (@id, @org_id, @run_id, 1, @producer_id, null)",
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId), ("producer_id", ProducerId))).ShouldBe(1);

        await scope.CommitAsync();

        return runId;
    }

    /// <summary>One committed kind-policy row for the world's project, as the app role.</summary>
    private async Task InsertKindPolicyAsync(
        World world,
        string kind,
        long maxBytes,
        string[] mediaTypes,
        bool captureEnabled = true,
        string sensitivity = "normal")
    {
        var array = "array[" + string.Join(",", mediaTypes.Select(type => $"'{type}'")) + "]";

        await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{kind}', {maxBytes}, {array}, '{sensitivity}', {(captureEnabled ? "true" : "false")})"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>Removes the project's row for <paramref name="kind"/>: what a policy edit between mint and completion looks like.</summary>
    private Task DeleteKindPolicyAsync(World world, string kind) =>
        fixture.ExecuteAsync(
            world.Database,
            $"delete from public.artifact_kind_policy where project_id = '{world.Project:D}' and kind = '{kind}'");

    /// <summary>The upload-url request body (00 §9.3, snake_case wire contract).</summary>
    private static object Body(
        Guid runId,
        string kind,
        string mediaType,
        long bytes,
        string checksum = Checksum) =>
        new
        {
            run_id = runId,
            producer_id = ProducerId,
            execution_key = ExecutionKey,
            attempt_no = 1,
            kind,
            media_type = mediaType,
            bytes,
            sha256 = checksum,
        };

    private static Task<ApiAnswer> UploadUrlAsync(World world, object body, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/artifacts/upload-url", _ => { }, body, idempotencyKey);

    private static Task<ApiAnswer> CompleteAsync(World world, Guid artifactId, string? idempotencyKey = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, idempotencyKey);

    /// <summary>Sends the bytes to the presigned URL with exactly the required headers (T18).</summary>
    private static async Task<HttpResponseMessage> PutAsync(ApiAnswer minted, byte[] body)
    {
        using var http = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Put, minted.Text("upload_url"))
        {
            Content = new ByteArrayContent(body),
        };

        foreach (var header in minted.Body.GetProperty("required_headers").EnumerateObject())
        {
            if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = header.Value.GetString()!;
            if (!message.Headers.TryAddWithoutValidation(header.Name, value))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        return await http.SendAsync(message, TestContext.Current.CancellationToken);
    }

    /// <summary>Mints one artifact of <paramref name="kind"/> and PUTs the bytes through the presigned URL.</summary>
    private async Task<(Guid ArtifactId, string StagingKey, string FinalKey)> MintAndUploadAsync(
        World world, Guid runId, string kind, string mediaType, byte[] body)
    {
        var minted = await UploadUrlAsync(
            world, Body(runId, kind, mediaType, body.LongLength, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body))), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);

        var artifactId = minted.Id("artifact_id");
        var stagingKey = minted.Text("object_key")!;
        stagingKey.ShouldBe($"pending/{world.Org:D}/{artifactId:D}");

        using var put = await PutAsync(minted, body);
        put.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted by the store; status={(int)put.StatusCode}");

        return (artifactId, stagingKey, (await ArtifactColumnAsync(world, artifactId, "object_key"))!);
    }

    /// <summary>A zip-signature body of exactly <paramref name="length"/> bytes (the sniffable <c>application/zip</c>).</summary>
    private static byte[] ZipBody(int length) =>
        [0x50, 0x4B, 0x03, 0x04, .. Encoding.UTF8.GetBytes(new string('A', length - 4))];

    private Task<long> ArtifactRowsAsync(World world) =>
        fixture.ScalarAsync<long>(world.Database, "select count(*) from public.artifact");

    private Task<string?> ArtifactColumnAsync(World world, Guid artifactId, string column) =>
        fixture.ScalarAsync<string?>(world.Database, $"select {column} from public.artifact where id = '{artifactId:D}'");

    private Task<long?> ArtifactLongAsync(World world, Guid artifactId, string column) =>
        fixture.ScalarAsync<long?>(world.Database, $"select {column} from public.artifact where id = '{artifactId:D}'");

    private Task<string?> SlotBindingAsync(World world, Guid runId) =>
        fixture.ScalarAsync<string?>(world.Database, $"select bound_token_jti from public.producer_slot where run_id = '{runId:D}'");

    // ------------------------------------------------------------------ no row: the defaults apply

    /// <summary>
    /// AC1 / D-A9: with no policy row, every defaulted kind is captured at exactly its default cap (and the row
    /// carries the kind's default sensitivity) and refused 413 <c>payload_too_large</c> one byte over it, with no
    /// artifact row minted.
    /// </summary>
    [Fact]
    public async Task UploadUrl_NoPolicyRow_DefaultKind_AtTheDefaultCapAccepted_OneOver413()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var minted = 0L;

        foreach (var kind in Defaults)
        {
            var atCap = await UploadUrlAsync(world, Body(runId, kind.Kind, kind.MediaTypes[0], kind.Cap), NewKey());

            atCap.Status.ShouldBe(HttpStatusCode.OK, $"{kind.Kind} at its default cap: {atCap.ErrorMessage}");
            var artifactId = atCap.Id("artifact_id");
            (await ArtifactLongAsync(world, artifactId, "bytes")).ShouldBe(kind.Cap);
            (await ArtifactColumnAsync(world, artifactId, "sensitivity")).ShouldBe(kind.Sensitivity);
            minted++;

            var overCap = await UploadUrlAsync(world, Body(runId, kind.Kind, kind.MediaTypes[0], kind.Cap + 1), NewKey());

            overCap.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, $"{kind.Kind} one byte over its default cap");
            overCap.ErrorCode.ShouldBe("payload_too_large");
            (await ArtifactRowsAsync(world)).ShouldBe(minted, $"{kind.Kind} one byte over must mint no row");
        }
    }

    /// <summary>
    /// AC1 / D-A9: with no policy row, every media type of a kind's default list is accepted and a type outside it
    /// is 422 <c>media_type_not_allowed</c> with no artifact row.
    /// </summary>
    [Fact]
    public async Task UploadUrl_NoPolicyRow_EveryDefaultMediaTypeAccepted_AnUnknownOne422()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var minted = 0L;

        foreach (var kind in Defaults)
        {
            foreach (var mediaType in kind.MediaTypes)
            {
                var accepted = await UploadUrlAsync(world, Body(runId, kind.Kind, mediaType, 1024), NewKey());

                accepted.Status.ShouldBe(HttpStatusCode.OK, $"{kind.Kind} {mediaType}: {accepted.ErrorMessage}");
                minted++;
            }

            var refused = await UploadUrlAsync(world, Body(runId, kind.Kind, kind.RefusedMediaType, 1024), NewKey());

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{kind.Kind} {kind.RefusedMediaType}");
            refused.ErrorCode.ShouldBe("media_type_not_allowed");
            (await ArtifactRowsAsync(world)).ShouldBe(minted, $"{kind.Kind} {kind.RefusedMediaType} must mint no row");
        }
    }

    /// <summary>
    /// AC1 / 06.1.2-T3.1: the four kinds D-A9 fixes no cap for have no default, so with no row each stays the
    /// fail-closed 422 <c>kind_disabled</c> and the producer slot is never bound.
    /// </summary>
    [Fact]
    public async Task UploadUrl_NoRow_KindWithoutDefault_422KindDisabled_AndNoSlot()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        foreach (var kind in NoDefaultKinds)
        {
            var answer = await UploadUrlAsync(world, Body(runId, kind, JsonType, 1024), NewKey());

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, kind);
            answer.ErrorCode.ShouldBe("kind_disabled", kind);
        }

        (await ArtifactRowsAsync(world)).ShouldBe(0);
        (await SlotBindingAsync(world, runId)).ShouldBeNull();
    }

    // ------------------------------------------------------------------ a project row: what it wins and what it cannot

    /// <summary>
    /// D-A9 "configurable down, never up without a decision": a row ABOVE a kind's default still caps at the
    /// default, and the row's <c>media_types</c> replace the default list wholesale (a default type the row omits
    /// is refused; a type only the row lists is accepted).
    /// </summary>
    [Fact]
    public async Task UploadUrl_RowAboveTheDefault_StillCapsAtTheDefault_AndItsMediaTypesReplaceTheDefaults()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var traceCap = Defaults.Single(kind => kind.Kind == "trace").Cap;

        // A 1 GiB row for a kind whose default is 100 MiB.
        await InsertKindPolicyAsync(world, "trace", maxBytes: 1024L * 1024 * 1024, mediaTypes: [ZipType]);

        var atDefault = await UploadUrlAsync(world, Body(runId, "trace", ZipType, traceCap), NewKey());
        var overDefault = await UploadUrlAsync(world, Body(runId, "trace", ZipType, traceCap + 1), NewKey());

        atDefault.Status.ShouldBe(HttpStatusCode.OK, atDefault.ErrorMessage);
        overDefault.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        overDefault.ErrorCode.ShouldBe("payload_too_large");
        (await ArtifactRowsAsync(world)).ShouldBe(1);

        // raw_report's default accepts text/html; a row that lists only application/xml refuses it and accepts
        // application/xml even though the row's cap (4 KiB) is below the default's 256 MiB.
        await InsertKindPolicyAsync(world, "raw_report", maxBytes: 4096, mediaTypes: [XmlType]);

        var replaced = await UploadUrlAsync(world, Body(runId, "raw_report", HtmlType, 1024), NewKey());
        var rowOnly = await UploadUrlAsync(world, Body(runId, "raw_report", XmlType, 1024), NewKey());

        replaced.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        replaced.ErrorCode.ShouldBe("media_type_not_allowed");
        rowOnly.Status.ShouldBe(HttpStatusCode.OK, rowOnly.ErrorMessage);
        (await ArtifactRowsAsync(world)).ShouldBe(2);
    }

    /// <summary>D-A9: a row BELOW the kind's default lowers the cap — the row's cap is accepted, one byte over it is 413.</summary>
    [Fact]
    public async Task UploadUrl_RowBelowTheDefault_LowersTheCap()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, "trace", maxBytes: 1024, mediaTypes: [ZipType]);

        var atRowCap = await UploadUrlAsync(world, Body(runId, "trace", ZipType, 1024), NewKey());
        var overRowCap = await UploadUrlAsync(world, Body(runId, "trace", ZipType, 1025), NewKey());

        atRowCap.Status.ShouldBe(HttpStatusCode.OK, atRowCap.ErrorMessage);
        overRowCap.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        overRowCap.ErrorCode.ShouldBe("payload_too_large");
        (await ArtifactRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>A row's <c>capture_enabled = false</c> refuses the kind even though the kind has a default (06.2.2 AC2).</summary>
    [Fact]
    public async Task UploadUrl_RowCaptureDisabled_BeatsTheDefault_422KindDisabled()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, "trace", maxBytes: 1024, mediaTypes: [ZipType], captureEnabled: false);

        var answer = await UploadUrlAsync(world, Body(runId, "trace", ZipType, 1024), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_disabled");
        (await ArtifactRowsAsync(world)).ShouldBe(0);
        (await SlotBindingAsync(world, runId)).ShouldBeNull();
    }

    /// <summary>
    /// The sensitivity a minted artifact inherits: the kind's default with no row (<c>trace</c> is
    /// <c>sensitive</c>), the row's <c>default_sensitivity</c> once the project has one.
    /// </summary>
    [Fact]
    public async Task UploadUrl_Sensitivity_IsTheKindDefault_UntilARowSuppliesOne()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var byDefault = await UploadUrlAsync(world, Body(runId, "trace", ZipType, 1024), NewKey());
        byDefault.Status.ShouldBe(HttpStatusCode.OK, byDefault.ErrorMessage);
        (await ArtifactColumnAsync(world, byDefault.Id("artifact_id"), "sensitivity")).ShouldBe("sensitive");

        await InsertKindPolicyAsync(world, "trace", maxBytes: 1024, mediaTypes: [ZipType], sensitivity: "normal");

        var byRow = await UploadUrlAsync(world, Body(runId, "trace", ZipType, 1024), NewKey());
        byRow.Status.ShouldBe(HttpStatusCode.OK, byRow.ErrorMessage);
        (await ArtifactColumnAsync(world, byRow.Id("artifact_id"), "sensitivity")).ShouldBe("normal");
    }

    // ------------------------------------------------------------------ the completion resolves the same policy

    /// <summary>
    /// AC3 / 06.1.2-T3.1: an artifact minted with the static default (no row at all) completes — the effective cap
    /// and media type list are the default's, so a small zip verifies and reaches <c>ready</c> at the final key.
    /// </summary>
    [Fact]
    public async Task Complete_NoPolicyRow_DefaultKind_VerifiesToReady()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var body = ZipBody(64);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, "trace", ZipType, body);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        answer.Text("failure_code").ShouldBeNull();
        answer.Body.GetProperty("bytes").GetInt64().ShouldBe(body.LongLength);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await store.ExistsAsync(finalKey)).ShouldBeTrue("a verified object must be promoted to the final key");
    }

    /// <summary>
    /// AC3 / 06.1.2-T3.1: an artifact of a kind with no default is refused 422 <c>kind_disabled</c> at completion
    /// once the project's row for it is gone — the same answer the mint would give — with nothing promoted.
    /// </summary>
    [Fact]
    public async Task Complete_NoRow_KindWithoutDefault_Is422KindDisabled_NoPromotion()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        // The artifact was minted while the project still had a dom_snapshot row (the kind has no default), and
        // the row is deleted before the completion, which re-resolves the policy.
        await InsertKindPolicyAsync(world, "dom_snapshot", maxBytes: 1024, mediaTypes: [JsonType]);
        var body = Encoding.UTF8.GetBytes("""{"snapshot":"dom"}""");
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, "dom_snapshot", JsonType, body);

        await DeleteKindPolicyAsync(world, "dom_snapshot");

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_disabled");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");
        (await store.ExistsAsync(finalKey)).ShouldBeFalse("a kind with no row and no default must not be promoted");
        (await store.ExistsAsync(stagingKey)).ShouldBeTrue("the refusal happens before any storage call");
    }
}
