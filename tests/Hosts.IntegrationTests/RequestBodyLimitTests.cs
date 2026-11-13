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
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2";

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
    public async Task Handler_Refusing_Routes_Are_Not_Pre_Empted_By_The_Host()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();
        var limit = factory.Services.GetRequiredService<ContractSchemaSet>().MaxBytes(ContractSchemaKeys.EventEnvelope)!.Value;

        // The event handler answers an over-cap batch with batch_too_large and its own counter; the host must not
        // pre-empt it, so a declared length over the cap passes the host (the anonymous caller is then a 401).
        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentLength = limit + 1;
        using var response = await client.PostAsync($"/v1/runs/{Guid.NewGuid()}/events", content, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Environment_Revision_Over_The_Contract_Limit_Is_Refused_413()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();
        var limit = factory.Services.GetRequiredService<ContractSchemaSet>().MaxBytes(ContractSchemaKeys.EnvironmentRevision)!.Value;

        using var content = new StringContent(new string(' ', checked((int)limit + 1)), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/v1/projects/{Guid.NewGuid()}/environments/{Guid.NewGuid()}/revisions",
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains(
            "\"code\":\"payload_too_large\"",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }
}
