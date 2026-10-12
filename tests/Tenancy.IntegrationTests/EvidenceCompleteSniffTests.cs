// 06.3.1-T2.2 media sniff at `complete`: the endpoint's ArtifactCompletionVerifier sniffs the PENDING object
// before the promotion (plan §7 — a mismatching artifact is never copied to the final key) and sniffs the FINAL
// object again after the post-copy HEAD (S-F2), so a mismatched signature is a committed 200
// `rejected`/`media_type_mismatch` — never `ready`, never counted as evidence, never promoted (06.3.1 §2 AC5).
// The table is complete for every accepted media type (XML/HTML/JSON, zip, PNG, JPEG, MP4) and a declared type
// outside it is a mismatch (fail closed, T2.2), so a type the sniffer cannot check never becomes evidence.
// Every test drives the real host, a real PostgreSQL and a real SeaweedFS S3 container (Testcontainers; never
// the shared dev stack) through the same harness the sibling complete suites use, and asserts the database state
// by SQL. Expectations come from the plan (06.3.1 §1/§2 AC5, §5, §7) and the shared contract, not from the
// current behaviour.
#pragma warning disable CA1861
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Microsoft.AspNetCore.TestHost;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T2.2 (AC5): the media sniff the completion runs on the pending object before the promotion and again on
/// the final object after the post-copy HEAD. A declared type whose bytes do not match is rejected with
/// <c>media_type_mismatch</c>, with no outbox row and no promotion; a matching one is <c>ready</c>; a declared
/// type outside the sniff table (an opaque or unknown type) is a mismatch too — fail closed — so it can never
/// become evidence. The table now covers zip, PNG, JPEG and MP4 as well as the XML/HTML/JSON families, so the
/// sibling complete suites declare a real zip signature.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class EvidenceCompleteSniffTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string XmlType = "application/xml";

    private const string HtmlType = "text/html";

    private const string JsonType = "application/json";

    /// <summary>A sniffed archive type (06.3.1-T2.2): the bytes must begin with the zip signature <c>PK\x03\x04</c>.</summary>
    private const string ZipType = "application/zip";

    /// <summary>A sniffed image type (06.3.1-T2.2): the bytes must begin with the 8-byte PNG signature.</summary>
    private const string PngType = "image/png";

    /// <summary>A declared type the sniff table does not know (06.3.1-T2.2 fail closed): it can never become evidence.</summary>
    private const string OutOfTableType = "application/octet-stream";

    /// <summary>The caller's token id (the slot-binding identity, T9).</summary>
    private const string Jti = "jti-complete-sniff-1";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries; every uploaded body is well under it.</summary>
    private const long Cap = 1024;

    private const string SecretScanTask = "artifact_secret_scan";

    private const string StagingDeleteTask = "artifact_staging_delete";

    /// <summary>PNG magic (89 50 4E 47 0D 0A 1A 0A) plus filler: a signature the XML family refuses.</summary>
    private static readonly byte[] PngBody = BuildPngBody();

    /// <summary>A valid HTML prefix the HTML sniffer accepts.</summary>
    private static readonly byte[] HtmlBody =
        Encoding.UTF8.GetBytes("<!doctype html><html><head></head><body>ok</body></html>");

    /// <summary>A valid XML prefix the XML sniffer accepts.</summary>
    private static readonly byte[] XmlBody = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><report/>");

    /// <summary>HTML markup declared as JSON: the JSON family refuses a leading <c>&lt;</c>.</summary>
    private static readonly byte[] HtmlAsJsonBody = Encoding.UTF8.GetBytes("<html><body>x</body></html>");

    /// <summary>Bytes that are not a zip (no <c>PK\x03\x04</c>), declared <c>application/zip</c>: a mismatch.</summary>
    private static readonly byte[] NotZipBody = Encoding.UTF8.GetBytes(new string('A', 256));

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

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

    /// <summary>
    /// One migrated database with an org and a project created through the real registry routes, plus a host
    /// whose requests carry the writer principal and the object-store configuration. A fresh database per test
    /// keeps each policy row local (no shared policy leaks into a sibling test).
    /// </summary>
    private async Task<World> StartAsync()
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
            _fixture.AppConnectionString(database), org, project, Writer(), _store.Configuration());

        return new World(api, host, host.CreateClient(), database, user, org, project);
    }

    /// <summary>
    /// One committed run, one planned execution and one producer slot for <paramref name="world"/>'s org
    /// (through <see cref="RunRowSeeder"/>), the parents the artifact mint needs.
    /// </summary>
    private async Task<Guid> SeedRunAsync(World world)
    {
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
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

    /// <summary>
    /// One committed kind-policy row for <paramref name="world"/>'s project, as the app role: cap
    /// <see cref="Cap"/>, exactly <paramref name="mediaTypes"/> accepted, capture on. The declared media type
    /// must be in this list or the mint refuses it (06.1.2 §5), so each test names the type it declares.
    /// </summary>
    private async Task InsertKindPolicyAsync(World world, params string[] mediaTypes)
    {
        var allowed = string.Join("','", mediaTypes);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'trace', {Cap}, array['{allowed}'], 'normal', true)"))
            .ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>The upload-url request body (00 §9.3, snake_case wire contract) for <paramref name="body"/>.</summary>
    private static object UploadBody(Guid runId, byte[] body, string mediaType) =>
        new
        {
            run_id = runId,
            producer_id = ProducerId,
            execution_key = ExecutionKey,
            attempt_no = 1,
            kind = "trace",
            media_type = mediaType,
            bytes = body.LongLength,
            sha256 = Checksum(body),
        };

    private static string Checksum(byte[] body) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));

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
                // Content-Length travels with the content; a different length is exactly what T18 refuses.
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

    /// <summary>Mints one artifact for <paramref name="body"/> and PUTs the bytes through the presigned URL.</summary>
    private async Task<(Guid ArtifactId, string StagingKey, string FinalKey)> MintAndUploadAsync(
        World world, Guid runId, byte[] body, string mediaType)
    {
        var minted = await UploadUrlAsync(world, UploadBody(runId, body, mediaType), NewKey());
        minted.Status.ShouldBe(HttpStatusCode.OK, minted.ErrorMessage);

        var artifactId = minted.Id("artifact_id");
        var stagingKey = minted.Text("object_key")!;
        stagingKey.ShouldBe($"pending/{world.Org:D}/{artifactId:D}");

        using var put = await PutAsync(minted, body);
        put.IsSuccessStatusCode.ShouldBeTrue(
            $"the signed PUT must be accepted by the store; status={(int)put.StatusCode}");

        var finalKey = await ArtifactColumnAsync(world, artifactId, "object_key");
        return (artifactId, stagingKey, finalKey!);
    }

    private Task<string?> ArtifactColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string?>(world.Database, $"select {column} from public.artifact where id = '{artifactId:D}'");

    private Task<long> OutboxCountAsync(World world, string taskType, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where task_type = '{taskType}' and payload->>'artifact_id' = '{artifactId:D}'");

    // ------------------------------------------------------------------ AC5: mismatch rejects, never ready

    /// <summary>
    /// 06.3.1 AC5 / plan §7 (the plan's <c>CompleteArtifactCommandTests.MismatchedMediaIsRejected</c>):
    /// <c>application/xml</c> declared, PNG magic stored. The size and checksum match (the bytes are exactly what
    /// was declared), so only the sniff refuses it — a committed 200 <c>rejected</c> with
    /// <c>failure_code: media_type_mismatch</c>, no outbox row, never <c>ready</c>, and the mismatching bytes are
    /// never copied to the final key (the sniff runs on the pending object before the promotion).
    /// </summary>
    [Fact]
    public async Task MismatchedMediaIsRejected()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, XmlType);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, PngBody, XmlType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("media_type_mismatch");

        // A rejection writes no outbox row: no scan and no staging delete, so the staging object survives.
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("a sniff rejection is not a staging delete");

        // The sniff runs on the pending object BEFORE the promotion, so the mismatching bytes are never promoted.
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    /// <summary>
    /// 06.3.1 AC5: <c>application/json</c> declared, HTML markup stored — the JSON family refuses a leading
    /// <c>&lt;</c>, so the artifact is rejected with <c>media_type_mismatch</c> and nothing is promoted.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredJsonWithHtmlBytes_RejectedMediaTypeMismatch()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, JsonType);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, HtmlAsJsonBody, JsonType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("a sniff rejection is not a staging delete");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    /// <summary>
    /// 06.3.1 AC5: a replay of the same <c>Idempotency-Key</c> after the staged object is gone still answers the
    /// stored <c>rejected</c>/<c>media_type_mismatch</c>. The verdict is part of the idempotency hash, so this
    /// only holds if the verifier derived it from the row (the stored outcome) and never read the store again —
    /// a second read would have answered <c>object_missing</c> and conflicted with the first key.
    /// </summary>
    [Fact]
    public async Task Complete_MismatchedMedia_ReplayAfterObjectsDeleted_ReturnsStoredOutcome()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, XmlType);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, PngBody, XmlType);
        var key = NewKey();

        var first = await CompleteAsync(world, artifactId, key);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Text("state").ShouldBe("rejected");
        first.Text("failure_code").ShouldBe("media_type_mismatch");

        // The staging object survives the rejection (no staging-delete outbox row) and the final key was never
        // created; the lifecycle may collect the staging object since, and the stored outcome must not need it.
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse();
        await _store.DeleteAsync(stagingKey);
        await _store.DeleteAsync(finalKey);
        (await _store.ExistsAsync(stagingKey)).ShouldBeFalse();
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse();

        var replay = await CompleteAsync(world, artifactId, key);

        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        replay.Text("state").ShouldBe("rejected");
        replay.Text("failure_code").ShouldBe("media_type_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }

    // ------------------------------------- AC5: match accepts; a type outside the table rejects (fail closed)

    /// <summary>06.3.1 AC5: <c>text/html</c> declared with valid markup — the sniff matches, so it is <c>ready</c>.</summary>
    [Fact]
    public async Task Complete_DeclaredHtmlWithMarkupPrefix_Ready()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, HtmlType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, HtmlBody, HtmlType);

        world.Host.Statements.Clear();
        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        answer.Body.GetProperty("bytes").GetInt64().ShouldBe(HtmlBody.LongLength);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBeNull();
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(HtmlBody);

        // P5 (00 §5.7): the sniff's ReadPrefixAsync runs with no transaction open and is a network call, never
        // SQL — the completion stays within the same bounded statement count Complete_BoundedStatements derives.
        var statements = world.Host.Statements.Snapshot();
        statements.Count.ShouldBeGreaterThan(0);
        statements.Count.ShouldBeLessThanOrEqualTo(16);
    }

    /// <summary>06.3.1 AC5: <c>application/xml</c> declared with a valid XML prefix — the sniff matches.</summary>
    [Fact]
    public async Task Complete_DeclaredXmlWithXmlPrefix_Ready()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, XmlType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(XmlBody);
    }

    /// <summary>
    /// 06.3.1-T2.2 AC5: <c>image/png</c> declared with the PNG signature stored — the sniff matches, so it is
    /// <c>ready</c>. The table is no longer the XML/HTML/JSON families only.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredPngWithPngSignature_Ready()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, PngType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, PngBody, PngType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        answer.Body.GetProperty("failure_code").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(PngBody);
    }

    /// <summary>
    /// 06.3.1-T2.2 AC5: <c>application/zip</c> is now sniffed, so non-zip bytes under that declaration are a
    /// committed 200 <c>rejected</c>/<c>media_type_mismatch</c> — never <c>ready</c>, never promoted.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredZipWithNonZipBytes_RejectedMediaTypeMismatch()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, ZipType);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, NotZipBody, ZipType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("media_type_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("a sniff rejection is not a staging delete");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    /// <summary>
    /// 06.3.1-T2.2 AC5 fail closed: <c>application/octet-stream</c> is accepted by the policy but the sniff table
    /// has no signature for it, so its bytes are a <c>Mismatch</c> — the artifact is <c>rejected</c> with
    /// <c>media_type_mismatch</c> and never becomes evidence. This is the T1 <c>NotSniffed</c> fail-open closed.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredTypeOutsideTheSniffTable_RejectedMediaTypeMismatch()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, OutOfTableType);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, NotZipBody, OutOfTableType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("media_type_mismatch");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("a sniff rejection is not a staging delete");
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    // ------------------------ AC5/T2.2: every declared type is read (fail closed), staging then final

    /// <summary>
    /// 06.3.1-T2.2: <c>application/zip</c> is sniffed now, so the completion reads the staging prefix before the
    /// copy — and the mismatching bytes are rejected there, before the promotion. The reads are counted by a
    /// decorator over the real adapter.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredZip_IssuesAStagingPrefixReadAndRejects()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, ZipType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, NotZipBody, ZipType);

        var storage = new FaultInjectingObjectStorage();
        await using var host = world.Host.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => FaultInjectingObjectStorage.Decorate(services, storage)));
        using var client = host.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");

        // Only the staging read runs: the mismatch refuses the completion before the promotion, so the final
        // object is never created and its prefix is never read.
        storage.PrefixReads.ShouldBe(1, "the staging sniff refuses the completion before the promotion");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    /// <summary>
    /// 06.3.1-T2.2 fail closed: a declared type outside the sniff table is still read (there is no
    /// <c>IsSniffed</c> skip any more), and the read's verdict is a mismatch, so the artifact is rejected and never
    /// promoted. The T1 "zero prefix reads for an unknown type" behaviour is gone.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredTypeOutsideTheSniffTable_IssuesAPrefixReadAndRejects()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, OutOfTableType);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, NotZipBody, OutOfTableType);

        var storage = new FaultInjectingObjectStorage();
        await using var host = world.Host.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => FaultInjectingObjectStorage.Decorate(services, storage)));
        using var client = host.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        storage.PrefixReads.ShouldBeGreaterThan(0, "an out-of-table type is read and fails closed, never skipped");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a mismatching artifact is never copied to the final key");
    }

    /// <summary>
    /// 06.3.1-T2.2 AC5: a declared type the profile sniffs and that MATCHES (<c>application/xml</c>) reads the
    /// prefix twice — once on the staging object before the copy and once on the final object after it.
    /// </summary>
    [Fact]
    public async Task Complete_DeclaredXmlFamily_IssuesTwoPrefixReads()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, XmlType);

        var (artifactId, _, _) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        var storage = new FaultInjectingObjectStorage();
        await using var host = world.Host.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => FaultInjectingObjectStorage.Decorate(services, storage)));
        using var client = host.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Post, $"/v1/artifacts/{artifactId:D}/complete", _ => { }, null, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        storage.PrefixReads.ShouldBe(2, "a sniffed type reads the staging prefix before the copy and the final prefix after it");
    }

    // ------------------------------------------------------------------ P5: no storage call before the key check

    /// <summary>
    /// 06.3.1-T1 ordering (T92, 00 §5.7 P5): the endpoint validates the <c>Idempotency-Key</c> before the
    /// verifier runs, so a missing key answers 400 <c>request_invalid</c> without a single storage read or
    /// write — no object is promoted, the staging object survives, the row is untouched, and the verifier's
    /// row-facts read never reaches the database (so <c>ReadPrefixAsync</c> is never reached).
    /// </summary>
    [Fact]
    public async Task Complete_MissingIdempotencyKey_400_BeforeAnyStorageRead()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertKindPolicyAsync(world, XmlType);

        // A matching object is staged, so only the missing key can refuse the completion.
        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        world.Host.Statements.Clear();
        var answer = await CompleteAsync(world, artifactId);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("pending_upload");

        // No storage call: the verifier's promotion never ran, so the final key is empty and the staging
        // object is untouched — and no statement reads the row facts the verifier would need.
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("the key is refused before the verifier promotes bytes");
        (await _store.ExistsAsync(stagingKey)).ShouldBeTrue("the refusal happens before any storage call");
        world.Host.Statements.Snapshot()
            .Any(statement => statement.Contains("artifact_kind_policy", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse("the verifier's row-facts read must not run for a refused key");
    }

    private static byte[] BuildPngBody()
    {
        var body = new byte[256];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(body, 0);
        return body;
    }
}
