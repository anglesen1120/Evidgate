using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The real credential path for a test caller: create an ingest token, exchange it at <c>POST /v1/ci/token</c> and send
/// the minted <c>ci_producer</c> bearer. It needs a host built with <see cref="TenancyApiFactory.Ci"/>, because only
/// that host authenticates the bearer with the production authenticator and nothing else.
/// </summary>
internal static class IngestBearer
{
    /// <summary>One minute is the shortest bearer life the options allow, so a test outlives a bearer by moving a fake clock.</summary>
    internal static IReadOnlyDictionary<string, string?> ShortLived { get; } = BuildShortLived();

    /// <summary>Past one minute of life plus the validator's 30 s skew.</summary>
    internal static readonly TimeSpan PastLifetime = TimeSpan.FromMinutes(2);

    private static Dictionary<string, string?> BuildShortLived()
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in TenancyApiFactory.Keys.Configuration)
        {
            configuration[key] = value;
        }

        configuration["EVIDGATE:Ci:TokenLifetimeMinutes"] = "1";

        return configuration;
    }

    /// <summary>One ingest token of <paramref name="project"/>, created by the org's admin: its one-time secret and its stable id.</summary>
    internal static async Task<(string Secret, Guid TokenId)> CreateTokenAsync(
        HttpClient client, Action<HttpRequestHeaders> admin, Guid project, string name)
    {
        var created = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", admin, new { name });
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return (created.Text("secret")!, created.Body.GetProperty("token").GetProperty("id").GetGuid());
    }

    internal static Task<ApiAnswer> ExchangeAsync(HttpClient client, string secret) =>
        client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = secret });

    /// <summary>A client of <paramref name="host"/> that sends <paramref name="bearer"/> on every request.</summary>
    internal static HttpClient WithBearer(WebApplicationFactory<Program> host, string bearer)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return client;
    }

    /// <summary>Exchanges <paramref name="secret"/> through <paramref name="anonymous"/> and returns a client carrying the minted bearer.</summary>
    internal static async Task<HttpClient> BearerClientAsync(WebApplicationFactory<Program> host, HttpClient anonymous, string secret)
    {
        var answer = await ExchangeAsync(anonymous, secret);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        return WithBearer(host, answer.Text("token")!);
    }
}
