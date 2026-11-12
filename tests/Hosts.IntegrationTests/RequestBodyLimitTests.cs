using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// N9: a route's body limit is applied before any handler reads, and the contract's <c>x-max-bytes</c> is the only
/// place a contract-capped route's number is written.
/// </summary>
public sealed class RequestBodyLimitTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    /// <summary>Route → contract schema, for every route whose cap the contract declares.</summary>
    private static readonly (string Method, string Pattern, string SchemaKey)[] ContractRoutes =
    [
        ("POST", "/v1/runs/{r}/events", ContractSchemaKeys.EventEnvelope),
        ("POST", "/v1/projects/{p}/environments/{e}/revisions", ContractSchemaKeys.EnvironmentRevision),
        ("POST", "/v1/projects/{p}/policies", ContractSchemaKeys.PolicyRevision),
        ("PUT", "/v1/healing/switch", ContractSchemaKeys.HealingSwitchSet),
    ];

    [Fact]
    public async Task Contract_Routes_Carry_The_Contract_Limit()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();
        var schemas = factory.Services.GetRequiredService<ContractSchemaSet>();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();

        foreach (var (method, pattern, schemaKey) in ContractRoutes)
        {
            var endpoint = Assert.Single(
                endpoints,
                candidate => candidate.RoutePattern.RawText == pattern
                    && candidate.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var metadata = Assert.IsType<RequestBodyLimitMetadata>(endpoint.Metadata.GetMetadata<RequestBodyLimitMetadata>());

            Assert.Equal(schemaKey, metadata.SchemaKey);
            Assert.Equal(schemas.MaxBytes(schemaKey), metadata.Resolve(schemas));
        }
    }

    [Fact]
    public async Task Event_Batch_Over_The_Contract_Limit_Is_Refused_Without_Reading_The_Body()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();
        var limit = factory.Services.GetRequiredService<ContractSchemaSet>().MaxBytes(ContractSchemaKeys.EventEnvelope)!.Value;

        var body = new CountingStream(limit + 1);
        using var content = new StreamContent(body);
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentLength = limit + 1;

        using var response = await client.PostAsync($"/v1/runs/{Guid.NewGuid()}/events", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains(
            "\"code\":\"payload_too_large\"",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Equal(0, body.BytesRead);
    }

    /// <summary>A body of <paramref name="length"/> zero bytes that records how much of it was read.</summary>
    private sealed class CountingStream(long length) : Stream
    {
        private long _position;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, take);
            _position += take;
            BytesRead += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
