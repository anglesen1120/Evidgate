// 06.1.2-T3.1: the request-body limit of POST /v1/artifacts/upload-url. The endpoint refuses a body over
// UploadUrlLimits.MaxBodyBytes (8 KiB) with the 00 §9.2 413 `payload_too_large` envelope BEFORE it deserialises —
// a declared Content-Length over the limit without reading the body at all, and a chunked or length-less body
// while it is read — so an oversized request touches no database, binds no producer slot and echoes no body byte.
// The legal body stays under 2.5 KiB, so the boundary is far from any real request. Every test drives the real
// host and a real PostgreSQL; expectations come from the plan (06.1.2 §10 T3.1, the coordinator decision
// D-S46-Q1), never from the current behaviour.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T3.1 (AC1): the upload-url body limit. A body of exactly the limit is accepted and minted; one byte over
/// it is 413 <c>payload_too_large</c> with no artifact row and no slot binding, whether the client declared a
/// <c>Content-Length</c> (the endpoint refuses without reading) or streamed the body chunked / without a length
/// (the counting stream trips at the byte that passes the limit).
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceUploadUrlBodyLimitTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    : IClassFixture<TenancyHostPool>
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A well-formed declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string ZipType = "application/zip";

    /// <summary>The caller's token id (the slot-binding identity, T9).</summary>
    private const string Jti = "jti-body-limit-1";

    private const string ProducerId = "shard-1";

    private sealed record World(
        TenancyApiFactory Api,
        TenancyApiFactory Host,
        HttpClient Client,
        string Database,
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
    private static RequestPrincipal Writer() =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: Jti);

    private async Task<World> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = hosts.RealResolver(database);
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var host = hosts.WithPrincipalConfigured(
            database, org, project, Writer(), ObjectStorage);

        return new World(api, host, host.CreateClient(), database, org, project);
    }

    /// <summary>
    /// One committed run, one planned execution and one producer slot — the parents the mint needs. No kind-policy
    /// row is written: <c>trace</c> carries a static default (06.1.2-T3.1), so the limit is what decides here.
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

    /// <summary>
    /// The object-store configuration the endpoint needs: an unreachable local endpoint (presigning never
    /// connects), path-style addressing, and the two credentials under the <c>SecretNames</c> spelling.
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

    /// <summary>
    /// A valid upload-url body of exactly <paramref name="totalBytes"/> bytes: the JSON the endpoint accepts,
    /// padded with trailing whitespace (whitespace after the value is legal JSON and is what makes the boundary
    /// exact without changing what the body means).
    /// </summary>
    private static string PaddedBody(Guid runId, int totalBytes)
    {
        var json = $$"""{"run_id":"{{runId:D}}","producer_id":"{{ProducerId}}","execution_key":"{{ExecutionKey}}","attempt_no":1,"kind":"trace","media_type":"{{ZipType}}","bytes":1024,"sha256":"{{Checksum}}"}""";
        var length = Encoding.UTF8.GetByteCount(json);

        length.ShouldBeLessThan(totalBytes, "the padding must leave room for the whole body");

        return json + new string(' ', totalBytes - length);
    }

    /// <summary>Sends one raw body verbatim (the object-body helper of the registry suite cannot express padding).</summary>
    private static async Task<ApiAnswer> SendRawAsync(HttpClient client, string raw, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/artifacts/upload-url");

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Content = new StringContent(raw, Encoding.UTF8, "application/json");

        return await AnswerAsync(client, request);
    }

    /// <summary>Sends a body whose length the client cannot know (a non-seekable stream), so no <c>Content-Length</c> travels.</summary>
    private static async Task<ApiAnswer> SendStreamedAsync(HttpClient client, byte[] raw, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/artifacts/upload-url");

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        using var content = new StreamContent(new NoLengthStream(raw));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        return await AnswerAsync(client, request);
    }

    private static async Task<ApiAnswer> AnswerAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private Task<long> ArtifactRowsAsync(World world) =>
        fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.artifact where org_id = '{world.Org:D}'");

    private Task<string?> SlotBindingAsync(World world, Guid runId) =>
        fixture.ScalarAsync<string?>(world.Database, $"select bound_token_jti from public.producer_slot where run_id = '{runId:D}'");

    /// <summary>The refusal of an oversized body: the 00 §9.2 413 envelope, nothing written, nothing bound, nothing echoed.</summary>
    private static void ShouldBeOverTheLimit(ApiAnswer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("payload_too_large");
        answer.Body.GetRawText().ShouldNotContain(ExecutionKey, Case.Sensitive, "the refusal must never echo the request body");
    }

    // ------------------------------------------------------------------ the declared Content-Length arm

    /// <summary>A body of exactly 8 KiB is accepted and minted: the limit is inclusive.</summary>
    [Fact]
    public async Task UploadUrl_BodyExactlyAtTheLimit_IsAccepted()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var raw = PaddedBody(runId, UploadUrlLimits.MaxBodyBytes);
        Encoding.UTF8.GetByteCount(raw).ShouldBe(UploadUrlLimits.MaxBodyBytes);

        var answer = await SendRawAsync(world.Client, raw, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await ArtifactRowsAsync(world)).ShouldBe(1);
        (await SlotBindingAsync(world, runId)).ShouldBe(Jti);
    }

    /// <summary>
    /// One byte over the limit (with the Content-Length the client declares) is 413 <c>payload_too_large</c>: the
    /// endpoint refuses before deserialising, so no artifact row is minted and no producer slot is bound.
    /// </summary>
    [Fact]
    public async Task UploadUrl_BodyOneByteOverTheLimit_Is413PayloadTooLarge_NoRow_NoSlot()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var raw = PaddedBody(runId, UploadUrlLimits.MaxBodyBytes + 1);
        Encoding.UTF8.GetByteCount(raw).ShouldBe(UploadUrlLimits.MaxBodyBytes + 1);

        var answer = await SendRawAsync(world.Client, raw, NewKey());

        ShouldBeOverTheLimit(answer);
        (await ArtifactRowsAsync(world)).ShouldBe(0);
        (await SlotBindingAsync(world, runId)).ShouldBeNull();
    }

    /// <summary>A far-oversized body is the same 413 however large the client declares it: it is never read or parsed.</summary>
    [Fact]
    public async Task UploadUrl_FarOversizedBodyWithContentLength_Is413_NoRow()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var raw = PaddedBody(runId, 64 * 1024);

        var answer = await SendRawAsync(world.Client, raw, NewKey());

        ShouldBeOverTheLimit(answer);
        (await ArtifactRowsAsync(world)).ShouldBe(0);
        (await SlotBindingAsync(world, runId)).ShouldBeNull();
    }

    // ------------------------------------------------------------------ the streamed arm (no Content-Length)

    /// <summary>
    /// A body streamed from a source whose length cannot be known (a non-seekable stream, so HttpClient sends it
    /// chunked with no <c>Content-Length</c>) carries nothing the declared-length check can see, so only the
    /// endpoint's own counting can bound it: exactly the limit is still accepted and minted, one byte over it is
    /// the same 413 with nothing written and no new slot binding.
    /// </summary>
    [Fact]
    public async Task UploadUrl_StreamedBodyBoundary_AtTheLimitAccepted_OneByteOverIs413()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var atLimit = await SendStreamedAsync(
            world.Client, Encoding.UTF8.GetBytes(PaddedBody(runId, UploadUrlLimits.MaxBodyBytes)), NewKey());

        atLimit.Status.ShouldBe(HttpStatusCode.OK, atLimit.ErrorMessage);
        (await ArtifactRowsAsync(world)).ShouldBe(1);
        (await SlotBindingAsync(world, runId)).ShouldBe(Jti);

        var overLimit = await SendStreamedAsync(
            world.Client, Encoding.UTF8.GetBytes(PaddedBody(runId, UploadUrlLimits.MaxBodyBytes + 1)), NewKey());

        ShouldBeOverTheLimit(overLimit);
        (await ArtifactRowsAsync(world)).ShouldBe(1);
        (await SlotBindingAsync(world, runId)).ShouldBe(Jti);
    }

    /// <summary>
    /// A read-only stream of <paramref name="bytes"/> that reports no length and cannot seek, so its content is
    /// sent without a <c>Content-Length</c> — the shape the declared-length check cannot see. It overrides only the
    /// synchronous read and leaves the async ones to <see cref="Stream"/>'s fallback, as the other stream doubles
    /// of this suite do.
    /// </summary>
    private sealed class NoLengthStream(byte[] bytes) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offsetInBuffer, int count)
        {
            var taken = Math.Min(count, bytes.Length - position);
            bytes.AsSpan(position, taken).CopyTo(buffer.AsSpan(offsetInBuffer, taken));
            position += taken;

            return taken;
        }

        public override void Flush()
        {
            // Read-only.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offsetInBuffer, int count) => throw new NotSupportedException();
    }
}
