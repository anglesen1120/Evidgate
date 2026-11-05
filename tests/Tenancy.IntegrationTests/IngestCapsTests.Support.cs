using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Orchestrator.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class IngestCapsTests
{
    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A manifest of the world's suite and cases, serialised so a body-boundary test can measure it and reuse it. The
    /// axes are parameters because a test that counts manifest rows needs a plan of its own.
    /// </summary>
    private static string ManifestBody(RunCreationTests.World world, int variants = 2, int shards = 2)
    {
        var manifest = RunCreationTests.Manifest(world.SuiteRevision, world.Cases, variants, shards);
        manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();

        return manifest.ToJsonString();
    }

    /// <summary>The manifest rows the world's org holds.</summary>
    private async Task<long> ManifestCountAsync(RunCreationTests.World world) =>
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.manifest where org_id = '{world.Org}'");

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

    /// <summary>A host body-size feature whose limit and read-only state a test sets.</summary>
    private sealed class BodySizeFeature(long? maxRequestBodySize, bool readOnly = false) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; } = readOnly;

        public long? MaxRequestBodySize { get; set; } = maxRequestBodySize;
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
