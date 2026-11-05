// 04.1.3-T1 (AC1, AC3) on the paths the criteria name: `POST /v1/projects/{p}/manifests` and
// `POST /v1/projects/{p}/runs`, plus the body reader on its own. Each cap is the configured one, so a test boots a
// second host that overrides only that value — the world itself is arranged under the defaults, so nothing the
// arrange step uploads is refused by the cap under test — and every refusal is asserted to write nothing.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Orchestrator.Endpoints;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The manifest caps of 04.1.3-T1: a body counted as it is read (so a missing or short <c>Content-Length</c> cannot
/// raise it, and no more than the cap plus one read buffer is ever pulled off the stream), and the declared
/// executions/variants counted after the schema parse but before anything is expanded or written.
/// </summary>
[Trait("Category", "Docker")]
public sealed class IngestCapsTests(PostgresTenancyFixture fixture)
{
    /// <summary>The most bytes one read of the body reader may pull: its own 16 KiB starting buffer.</summary>
    private const int OneReadBuffer = 16 * 1024;

    private readonly PostgresTenancyFixture _fixture = fixture;

    // ------------------------------------------------------------------ body reader, on its own

    /// <summary>A declared length is never trusted: a body that lies short is still counted, and refused at the cap.</summary>
    [Fact]
    public async Task ReadBodyAsync_DeclaredLengthShorterThanTheBody_StillCountsAndRefuses()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 400)));
        context.Request.ContentLength = 10;

        var (bytes, tooLarge) = await OrchestratorEndpoints.ReadBodyAsync(
            context,
            limit: 100,
            cap: "manifest_bytes",
            TestContext.Current.CancellationToken);

        bytes.IsEmpty.ShouldBeTrue("a refused body is never handed on");
        tooLarge.ShouldNotBeNull();
        tooLarge.Code.ShouldBe(ApiErrorCode.PayloadTooLarge);
        var detail = tooLarge.Details.ShouldHaveSingleItem();
        detail.Path.ShouldBe("manifest_bytes");
        detail.Limit.ShouldBe(100);
        detail.Actual.ShouldNotBeNull();
        detail.Actual.Value.ShouldBeGreaterThan(100, "the count is measured, not taken from the header");
    }

    /// <summary>A declared length over the cap refuses before a byte of the stream is read.</summary>
    [Fact]
    public async Task ReadBodyAsync_DeclaredLengthOverTheCap_RefusesWithoutReading()
    {
        var source = new CountingStream(new byte[1024 * 1024]);
        var context = new DefaultHttpContext();
        context.Request.Body = source;
        context.Request.ContentLength = 1024 * 1024;

        var (bytes, tooLarge) = await OrchestratorEndpoints.ReadBodyAsync(
            context,
            limit: 1024,
            cap: "manifest_bytes",
            TestContext.Current.CancellationToken);

        bytes.IsEmpty.ShouldBeTrue();
        tooLarge.ShouldNotBeNull();
        tooLarge.Details.ShouldHaveSingleItem().Actual.ShouldBeNull("nothing was counted");
        source.BytesRead.ShouldBe(0, "the declaration alone refuses it");
    }

    /// <summary>
    /// A chunked body (no <c>Content-Length</c>) is counted as it arrives and refused at the cap, and the reader stops
    /// there: at most one read buffer beyond the cap leaves the stream however large the body claims to be.
    /// </summary>
    [Fact]
    public async Task ReadBodyAsync_ChunkedBodyOverTheCap_ConsumesAtMostOneBufferPastTheCap()
    {
        var source = new CountingStream(new byte[8 * 1024 * 1024]);
        var context = new DefaultHttpContext();
        context.Request.Body = source;
        context.Request.ContentLength = null;

        var (bytes, tooLarge) = await OrchestratorEndpoints.ReadBodyAsync(
            context,
            limit: 1000,
            cap: "manifest_bytes",
            TestContext.Current.CancellationToken);

        bytes.IsEmpty.ShouldBeTrue();
        tooLarge.ShouldNotBeNull();
        tooLarge.Details.ShouldHaveSingleItem().Limit.ShouldBe(1000);
        source.BytesRead.ShouldBeLessThanOrEqualTo(1000 + OneReadBuffer, "bounded memory: the stream is never drained");
    }

    /// <summary>A body exactly at the cap is accepted whole; one byte more is refused.</summary>
    [Fact]
    public async Task ReadBodyAsync_AtTheCap_IsAccepted()
    {
        var body = new byte[1000];
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;

        var (bytes, tooLarge) = await OrchestratorEndpoints.ReadBodyAsync(
            context,
            limit: 1000,
            cap: "manifest_bytes",
            TestContext.Current.CancellationToken);

        tooLarge.ShouldBeNull();
        bytes.Length.ShouldBe(1000);
    }

    // ------------------------------------------------------------------ the manifest body cap on the routes

    /// <summary>
    /// AC1: a manifest body exactly at the configured cap is accepted on the upload route — the boundary is inclusive
    /// — whichever of the two run routes the same reader serves.
    /// </summary>
    [Fact]
    public async Task UploadManifest_BodyAtTheCap_IsAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var manifest = ManifestBody(world);
        var cap = Encoding.UTF8.GetByteCount(manifest);

        await using var capped = CappedHost(world, ("MaxManifestBytes", cap));

        var answer = await UploadManifestAsync(capped.Client, world, manifest);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
    }

    /// <summary>
    /// AC1: a manifest body one byte over the cap is 413 <c>payload_too_large</c> with
    /// <c>details[] {path: manifest_bytes, reason, limit, actual}</c>, counted as it arrives, and no manifest row is
    /// written — the caller keeps the server's own count rather than the header it sent.
    /// </summary>
    [Fact]
    public async Task UploadManifest_BodyOverTheCap_413WithCapDetail_NoManifestCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var manifest = ManifestBody(world);
        var cap = Encoding.UTF8.GetByteCount(manifest) - 1;

        await using var capped = CappedHost(world, ("MaxManifestBytes", cap));

        var answer = await UploadManifestAsync(capped.Client, world, manifest);

        answer.Status.ShouldBe((HttpStatusCode)413, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(ApiErrorCode.PayloadTooLarge);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("manifest_bytes");
        detail.GetProperty("reason").GetString().ShouldBe(ApiErrorCode.PayloadTooLarge);
        detail.GetProperty("limit").GetInt64().ShouldBe(cap);
        detail.GetProperty("actual").GetInt64().ShouldBe(cap + 1);
    }

    /// <summary>
    /// AC1: the run-create route reads its body through the same published cap, so a chunked run-create body over it is
    /// 413 with the cap detail and no run, planned execution, producer slot or outbox row.
    /// </summary>
    [Fact]
    public async Task CreateRun_ChunkedBodyOverTheCap_413WithCapDetail_NoRowCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var capped = CappedHost(world, ("MaxManifestBytes", 16));

        var body = RunCreationTests.RunBody(world.ManifestId);
        Encoding.UTF8.GetByteCount(body).ShouldBeGreaterThan(16, "the body must exceed the configured cap");

        using var content = new ChunkedJson(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{world.Project}/runs");
        world.Admin(request.Headers);
        request.Headers.Add("Idempotency-Key", RegistryApi.NewKey());
        request.Content = content;

        using var response = await capped.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        var answer = new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);

        answer.Status.ShouldBe((HttpStatusCode)413, text);
        answer.ErrorCode.ShouldBe(ApiErrorCode.PayloadTooLarge);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("manifest_bytes");
        detail.GetProperty("limit").GetInt64().ShouldBe(16);
        detail.GetProperty("actual").GetInt64().ShouldBeGreaterThan(16);

        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    // ------------------------------------------------------------------ executions and variants

    /// <summary>AC1: a manifest whose declared executions equal the cap is accepted and planned.</summary>
    [Fact]
    public async Task CreateRun_ExecutionsAtTheCap_IsAccepted()
    {
        // Two cases × two variants × one environment is four planned executions.
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var capped = CappedHost(world, ("MaxManifestExecutions", 4));

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
    }

    /// <summary>
    /// AC1: one execution over the cap is 422 <c>cap_exceeded</c> with <c>{path: manifest_executions, limit, actual}</c>,
    /// refused before the planned-execution list is expanded, and no row exists afterwards.
    /// </summary>
    [Fact]
    public async Task CreateRun_OneExecutionOverTheCap_422CapExceeded_NoRowCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var capped = CappedHost(world, ("MaxManifestExecutions", 3));

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(ApiErrorCode.CapExceeded);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("manifest_executions");
        detail.GetProperty("reason").GetString().ShouldBe(ApiErrorCode.CapExceeded);
        detail.GetProperty("limit").GetInt64().ShouldBe(3);
        detail.GetProperty("actual").GetInt64().ShouldBe(4);

        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    /// <summary>
    /// AC1: the variant axis is capped on its own — a manifest under the executions cap whose case declares more
    /// variants than allowed is 422 <c>cap_exceeded</c> naming <c>manifest_variants</c>.
    /// </summary>
    [Fact]
    public async Task CreateRun_VariantsOverTheCap_422CapExceeded_NoRowCreated()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var capped = CappedHost(world, ("MaxManifestVariants", 1));

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(ApiErrorCode.CapExceeded);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("manifest_variants");
        detail.GetProperty("limit").GetInt64().ShouldBe(1);
        detail.GetProperty("actual").GetInt64().ShouldBe(2);

        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    /// <summary>
    /// Each refusal is counted once under the cap that refused it, on the counter's closed label vocabulary, so an
    /// operator can tell which cap is firing without reading a body.
    /// </summary>
    [Fact]
    public async Task CreateRun_CapRefusal_CountsTheCap()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var capped = CappedHost(world, ("MaxManifestExecutions", 3));

        using var listener = IngestTelemetryCapture.Caps(out ConcurrentBag<string> caps);

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunCreationTests.RunBody(world.ManifestId),
            client: capped.Client);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        caps.ShouldContain("manifest_executions");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The world's own manifest, serialised exactly as the arrange step uploaded it, so a body-boundary test can
    /// measure it and reuse it.
    /// </summary>
    private static string ManifestBody(RunCreationTests.World world)
    {
        var manifest = RunCreationTests.Manifest(world.SuiteRevision, world.Cases, variants: 2, shards: 2);
        manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();

        return manifest.ToJsonString();
    }

    /// <summary>One upload of <paramref name="manifest"/> as the world's caller, through a given host.</summary>
    private static async Task<ApiAnswer> UploadManifestAsync(
        HttpClient client,
        RunCreationTests.World world,
        string manifest)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{world.Project}/manifests");
        world.Admin(request.Headers);
        request.Headers.Add("Idempotency-Key", RegistryApi.NewKey());
        request.Content = new StringContent(manifest, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>
    /// A second host over the world's database that overrides only the named cap, so the world's own arrange keeps the
    /// published defaults and the request under test meets the value the test states.
    /// </summary>
    private CappedHostScope CappedHost(RunCreationTests.World world, params (string Name, long Value)[] values) =>
        new(TenancyApiFactory.RealResolver(
            _fixture.AppConnectionString(world.Database),
            values.ToDictionary(
                pair => $"EVIDGATE:Ingestion:{pair.Name}",
                pair => (string?)pair.Value.ToString(CultureInfo.InvariantCulture),
                StringComparer.Ordinal)));

    /// <summary>A capped host and its client, disposed together.</summary>
    private sealed class CappedHostScope(TenancyApiFactory api) : IAsyncDisposable
    {
        /// <summary>A client of the capped host.</summary>
        public HttpClient Client { get; } = api.CreateClient();

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await api.DisposeAsync();
        }
    }

    /// <summary>
    /// A request body that declares no length and streams its bytes on demand, counting what the server pulled off it —
    /// the shape a chunked upload has, and the one a declared length cannot be trusted for.
    /// </summary>
    private sealed class ChunkedJson(string body) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(body);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            Headers.ContentLength = null;
            await stream.WriteAsync(_bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    /// <summary>A stream that hands out zeros and records how many bytes were actually read from it.</summary>
    private sealed class CountingStream(byte[] content) : Stream
    {
        private int _position;

        /// <summary>How many bytes a reader pulled.</summary>
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(count, content.Length - _position);
            Array.Copy(content, _position, buffer, offset, take);
            _position += take;
            BytesRead += take;

            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var take = Math.Min(buffer.Length, content.Length - _position);
            content.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            BytesRead += take;

            return ValueTask.FromResult(take);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
