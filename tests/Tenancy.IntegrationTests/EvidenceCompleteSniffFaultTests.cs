// 06.3.1-T1 media-sniff FAULT branches at `complete` (review F-C4): the S-F2 final-key sniff the plan §7 and
// AC5 add is exercised through the real host, a real PostgreSQL and a real SeaweedFS S3 container
// (Testcontainers; never the shared dev stack) with an IObjectStorage decorator over the real adapter that
// injects the three branches a real store cannot be made to produce on demand:
//   (a) the bytes swapped between the staging read and the copy, so the FINAL sniff sees a mismatching prefix;
//   (b) the final object vanished between the post-copy HEAD and the final prefix read, so the read is null;
//   (c) the staging prefix read finds the object gone (the 24 h lifecycle race), which must fall through to the
//       copy/retry path rather than reject.
//   (d) both prefix reads carry the ETag the preceding HEAD reported (the T2.2 pin), and
//   (e) a 412 PreconditionFailed on either pinned read is a terminal rejection: the object was replaced, so the
//       bytes are no longer the verified ones (staging: the copy never runs; final: already promoted, still
//       rejected, never ready).
// The decorator only wraps the adapter (ConfigureTestServices); every other seam is production. Expectations
// come from the plan (06.3.1 §2 AC5, §7) and the shared contract, not from the current behaviour.
#pragma warning disable CA1861
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1 (review F-C4): the final-key sniff branches of <c>ArtifactCompletionVerifier</c> and the staging
/// prefix-null fall-through, driven through the real complete route with a fault-injecting
/// <see cref="FaultInjectingObjectStorage"/> over the real store. Each test names the plan line it verifies.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceCompleteSniffFaultTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string XmlType = "application/xml";

    /// <summary>The caller's token id (the slot-binding identity, T9).</summary>
    private const string Jti = "jti-complete-sniff-fault-1";

    private const string ProducerId = "shard-1";

    /// <summary>The kind-policy cap every seeded policy row carries; every uploaded body is well under it.</summary>
    private const long Cap = 1024;

    private const string SecretScanTask = "artifact_secret_scan";

    private const string StagingDeleteTask = "artifact_staging_delete";

    /// <summary>PNG magic (89 50 4E 47 0D 0A 1A 0A) plus filler: a signature the XML family refuses.</summary>
    private static readonly byte[] PngBody = BuildPngBody();

    /// <summary>A valid XML prefix the XML sniffer accepts.</summary>
    private static readonly byte[] XmlBody = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><report/>");

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private sealed record World(
        TenancyApiFactory Api,
        TenancyApiFactory BaseHost,
        WebApplicationFactory<Program> Host,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        FaultInjectingObjectStorage Storage) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            await BaseHost.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    /// <summary>A service principal holding <c>artifact:write</c> and a token id (the slot-binding identity).</summary>
    private static RequestPrincipal Writer() =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: Jti);

    /// <summary>
    /// One migrated database with an org and a project created through the real registry routes, plus a host
    /// whose requests carry the writer principal and the object-store configuration, decorated with the
    /// fault-injecting storage the tests configure per case.
    /// </summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var storage = new FaultInjectingObjectStorage();
        var baseHost = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database), org, project, Writer(), _store.Configuration());
        var host = baseHost.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => FaultInjectingObjectStorage.Decorate(services, storage)));

        return new World(api, baseHost, host, host.CreateClient(), database, user, org, project, storage);
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

    /// <summary>One committed kind-policy row for <paramref name="world"/>'s project accepting <see cref="XmlType"/>.</summary>
    private async Task InsertXmlPolicyAsync(World world)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, capture_enabled) "
            + $"values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'trace', {Cap}, array['{XmlType}'], 'normal', true)"))
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

    // --------------------------------------------------- (a) final-key mismatch after the copy

    /// <summary>
    /// 06.3.1 AC5 / S-F2: the staging object is valid (the staging sniff matches, so the promotion runs), but the
    /// bytes the FINAL sniff reads contradict the declared type — the copy is not a verification. The completion
    /// is a committed 200 <c>rejected</c>/<c>media_type_mismatch</c> even though the promoted HEAD is intact, so a
    /// swap between the staging read and the copy can never commit <c>ready</c>.
    /// </summary>
    [Fact]
    public async Task Complete_FinalSniffSeesSwappedBytes_RejectedMediaTypeMismatch()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertXmlPolicyAsync(world);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        // The store holds the declared XML bytes; only the final prefix read is answered with PNG magic, the
        // bytes a swap between the staging read and the copy would leave behind.
        world.Storage.PrefixOverride = (key, _) => key == finalKey ? PngBody : null;

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("media_type_mismatch");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("media_type_mismatch");

        // The promotion ran (the staging sniff matched), so the final object exists; the rejection writes no
        // outbox row, so neither the scan nor the staging delete is enqueued.
        (await _store.ExistsAsync(finalKey)).ShouldBeTrue("the promotion ran before the final sniff refused it");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);

        // Two prefix reads: the staging sniff (match) and the final sniff (the injected mismatch).
        world.Storage.PrefixReads.ShouldBe(2);
    }

    // --------------------------------------------------- (b) final prefix null -> object_missing

    /// <summary>
    /// 06.3.1 AC5 / S-F2: the final object's post-copy <c>HEAD</c> verifies, but the final prefix read finds the
    /// object gone (it vanished between the <c>HEAD</c> and the read). The completion answers a committed 200
    /// <c>rejected</c>/<c>object_missing</c>, never a 500 and never <c>ready</c>.
    /// </summary>
    [Fact]
    public async Task Complete_FinalPrefixReadIsNull_RejectedObjectMissing()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertXmlPolicyAsync(world);

        var (artifactId, _, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        world.Storage.AbsentPrefixKeys.Add(finalKey);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("object_missing");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("object_missing");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);

        world.Storage.PrefixReads.ShouldBe(2);
    }

    // --------------------------------------------------- (c) staging prefix null -> copy/retry path

    /// <summary>
    /// 06.3.1 AC5 / plan §7: the staging <c>HEAD</c> finds the object but the staging prefix read finds it gone
    /// (the 24 h lifecycle race). The sniff is skipped, the promotion proceeds from the real store, and the
    /// artifact completes <c>ready</c> — the race is not turned into a rejection or a 500.
    /// </summary>
    [Fact]
    public async Task Complete_StagingPrefixReadIsNull_FallsThroughToTheCopyPath_Ready()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertXmlPolicyAsync(world);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        // Only the staging prefix read is answered null; the staging HEAD still sees the object, so the verifier
        // takes the staging branch, skips the sniff and copies.
        world.Storage.AbsentPrefixKeys.Add(stagingKey);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");
        answer.Body.GetProperty("bytes").GetInt64().ShouldBe(XmlBody.LongLength);
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("ready");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(1);
        (await _store.ReadAsync(finalKey)).ShouldBe(XmlBody);

        // Two reads: the null staging read, then the matching final read.
        world.Storage.PrefixReads.ShouldBe(2);
    }

    // ------------------------------------------- (d) the conditional read carries the HEAD ETag (T2.2 pinning)

    /// <summary>
    /// 06.3.1-T2.2: both prefix reads are pinned to the ETag the preceding <c>HEAD</c> reported — the staging read
    /// to the staging <c>HEAD</c>, the final read to the post-copy re-<c>HEAD</c> of the promoted object. The
    /// assertion compares against the ETags the store actually reported, so passing <c>null</c> (an unpinned read)
    /// fails it.
    /// </summary>
    [Fact]
    public async Task Complete_PinsBothPrefixReadsToTheHeadETags()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertXmlPolicyAsync(world);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("ready");

        var reads = world.Storage.ReadCalls.ToArray();
        reads.Length.ShouldBe(2, "a signed declared type reads the staging prefix, then the final prefix");
        reads[0].Key.ShouldBe(stagingKey);
        reads[1].Key.ShouldBe(finalKey);

        var stagingETag = world.Storage.HeadETags[stagingKey];
        var finalETag = world.Storage.HeadETags[finalKey];
        stagingETag.ShouldNotBeNullOrWhiteSpace("SeaweedFS reports an ETag on the staging HEAD");
        finalETag.ShouldNotBeNullOrWhiteSpace("SeaweedFS reports an ETag on the final re-HEAD");

        reads[0].IfMatch.ShouldBe(stagingETag, "the staging read is pinned to the staging HEAD ETag");
        reads[1].IfMatch.ShouldBe(finalETag, "the final read is pinned to the post-copy re-HEAD ETag");
    }

    // ------------------------------------------- (e) a 412 on the pinned read is terminal (T2.2)

    /// <summary>
    /// 06.3.1-T2.2: the staging prefix read is pinned to the staging <c>HEAD</c> ETag; a store that answers
    /// <c>412 PreconditionFailed</c> (the object was replaced between the <c>HEAD</c> and the read) means the bytes
    /// a copy would promote are not the verified bytes. The completion is a committed 200
    /// <c>rejected</c>/<c>object_missing</c> and the COPY NEVER RUNS — the final key is never created.
    /// </summary>
    [Fact]
    public async Task Complete_StagingPrefixReadPreconditionFailed_RejectedObjectMissing_NoCopy()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertXmlPolicyAsync(world);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        world.Storage.PreconditionFailedKeys.Add(stagingKey);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("object_missing");
        (await ArtifactColumnAsync(world, artifactId, "state")).ShouldBe("rejected");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("object_missing");

        // The staging read carried the pin the staging HEAD reported (a null would not be pinned).
        world.Storage.ReadCalls.Single().IfMatch.ShouldBe(
            world.Storage.HeadETags[stagingKey], "the refused read was pinned to the staging HEAD ETag");

        // Terminal: the refusal is decided before the promotion, so the copy never ran.
        (await _store.ExistsAsync(finalKey)).ShouldBeFalse("a replaced staging object is never promoted");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }

    /// <summary>
    /// 06.3.1-T2.2: the final prefix read is pinned to the ETag the post-copy re-<c>HEAD</c> reported; a
    /// <c>412</c> means the final object is no longer the one just verified, so the completion is a committed 200
    /// <c>rejected</c>/<c>object_missing</c> — never <c>ready</c>, even though the promotion already ran.
    /// </summary>
    [Fact]
    public async Task Complete_FinalPrefixReadPreconditionFailed_RejectedObjectMissing()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertXmlPolicyAsync(world);

        var (artifactId, stagingKey, finalKey) = await MintAndUploadAsync(world, runId, XmlBody, XmlType);

        world.Storage.PreconditionFailedKeys.Add(finalKey);

        var answer = await CompleteAsync(world, artifactId, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("state").ShouldBe("rejected");
        answer.Text("failure_code").ShouldBe("object_missing");
        (await ArtifactColumnAsync(world, artifactId, "failure_code")).ShouldBe("object_missing");

        var reads = world.Storage.ReadCalls.ToArray();
        reads.Length.ShouldBe(2, "the staging read matched, then the final read was refused");
        reads[0].Key.ShouldBe(stagingKey);
        reads[0].IfMatch.ShouldBe(world.Storage.HeadETags[stagingKey], "the staging read is pinned");
        reads[1].Key.ShouldBe(finalKey);
        reads[1].IfMatch.ShouldBe(world.Storage.HeadETags[finalKey], "the final read is pinned to the re-HEAD ETag");

        (await _store.ExistsAsync(finalKey)).ShouldBeTrue("the promotion ran before the final read refused it");
        (await OutboxCountAsync(world, SecretScanTask, artifactId)).ShouldBe(0);
        (await OutboxCountAsync(world, StagingDeleteTask, artifactId)).ShouldBe(0);
    }

    private static byte[] BuildPngBody()
    {
        var body = new byte[256];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(body, 0);
        return body;
    }
}

/// <summary>
/// A test <see cref="IObjectStorage"/> over the real adapter (06.3.1-T1 review F-P2/F-C4): it counts the
/// prefix reads the completion issues and, per case, answers one read with fault bytes (the bytes swapped
/// between the staging read and the copy) or a <c>null</c> (the object vanished). Registered through
/// <c>ConfigureTestServices</c>, so the rest of the pipeline stays production; the real adapter is built by the
/// container and attached to the decorator on first resolution.
/// </summary>
internal sealed class FaultInjectingObjectStorage : IObjectStorage, IDisposable
{
    private readonly ConcurrentDictionary<string, int> _readsPerKey = new(StringComparer.Ordinal);
    private IObjectStorage? _inner;
    private int _prefixReads;

    /// <summary>Every <see cref="ReadPrefixAsync"/> call made since the host started (staging and final).</summary>
    public int PrefixReads => Volatile.Read(ref _prefixReads);

    /// <summary>Keys whose prefix read answers <c>null</c> (the object vanished), regardless of the store.</summary>
    public HashSet<string> AbsentPrefixKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys whose conditional prefix read answers <c>412 PreconditionFailed</c> (the object no longer carries the
    /// ETag the preceding <c>HEAD</c> reported), regardless of the store (06.3.1-T2.2).
    /// </summary>
    public HashSet<string> PreconditionFailedKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>The ETag the last <c>HEAD</c> of each key reported, so a test can prove a read was pinned to it.</summary>
    public ConcurrentDictionary<string, string?> HeadETags { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Every prefix read in order, with the <c>If-Match</c> the caller passed: the pinning proof (06.3.1-T2.2).
    /// The entry is recorded before any short-circuit, so an injected <c>null</c> or <c>412</c> read still shows
    /// the ETag the verifier passed.
    /// </summary>
    public ConcurrentQueue<(string Key, string? IfMatch)> ReadCalls { get; } = new();

    /// <summary>
    /// Replaces the prefix response for a key's nth read (1-based) when it returns a non-null array; returning
    /// <c>null</c> delegates to the store. Set after the artifact is minted so the final key is known.
    /// </summary>
    public Func<string, int, byte[]?>? PrefixOverride { get; set; }

    /// <summary>
    /// Replaces the host's <see cref="IObjectStorage"/> registration with <paramref name="decorator"/> over the
    /// real adapter the container builds, so the decorator sees every storage call the completion makes.
    /// </summary>
    /// <param name="services">The host's service collection, inside <c>ConfigureTestServices</c>.</param>
    /// <param name="decorator">The decorator instance the test holds to configure and inspect.</param>
    public static void Decorate(IServiceCollection services, FaultInjectingObjectStorage decorator)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorator);

        var descriptor = services.Last(service => service.ServiceType == typeof(IObjectStorage));
        services.Remove(descriptor);
        services.AddSingleton<IObjectStorage>(provider =>
        {
            decorator.Attach((IObjectStorage)ActivatorUtilities.CreateInstance(
                provider, descriptor.ImplementationType
                    ?? throw new InvalidOperationException("the IObjectStorage registration has no implementation type")));

            return decorator;
        });
    }

    /// <inheritdoc />
    public PresignedPut PresignPut(PresignPutRequest request) => Inner().PresignPut(request);

    public PresignedGet PresignGet(string objectKey) => Inner().PresignGet(objectKey);

    /// <inheritdoc />
    public async Task<ObjectHead?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var head = await Inner().HeadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (head is not null)
        {
            HeadETags[objectKey] = head.ETag;
        }

        return head;
    }

    /// <inheritdoc />
    public Task<byte[]?> ReadPrefixAsync(
        string objectKey, int maxBytes, CancellationToken cancellationToken, string? ifMatch = null)
    {
        Interlocked.Increment(ref _prefixReads);
        var index = _readsPerKey.AddOrUpdate(objectKey, 1, (_, previous) => previous + 1);
        ReadCalls.Enqueue((objectKey, ifMatch));

        if (AbsentPrefixKeys.Contains(objectKey))
        {
            return Task.FromResult<byte[]?>(null);
        }

        if (PreconditionFailedKeys.Contains(objectKey))
        {
            throw new ObjectStorageException("read-prefix", "PreconditionFailed", HttpStatusCode.PreconditionFailed);
        }

        if (PrefixOverride?.Invoke(objectKey, index) is { } replacement)
        {
            return Task.FromResult<byte[]?>(replacement);
        }

        return Inner().ReadPrefixAsync(objectKey, maxBytes, cancellationToken, ifMatch);
    }

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken cancellationToken) =>
        Inner().GetAsync(objectKey, maxBytes, cancellationToken);

    /// <inheritdoc />
    public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken) =>
        Inner().CopyAsync(sourceKey, destinationKey, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        Inner().DeleteAsync(objectKey, cancellationToken);

    /// <summary>Disposes the real adapter the decorator built (the container disposes this singleton).</summary>
    public void Dispose() => (_inner as IDisposable)?.Dispose();

    /// <summary>Attaches the real adapter the decorator delegates to (called once by the DI factory).</summary>
    private void Attach(IObjectStorage inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private IObjectStorage Inner() =>
        _inner ?? throw new InvalidOperationException("the decorator was used before its inner adapter was attached");
}
