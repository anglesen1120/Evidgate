using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>Tests the public token exchange boundary.</summary>
public sealed partial class CiAuthTests
{
    [Fact]
    public async Task Exchange_RefusesLegacyCredentialsAndMixedRequests_WithoutOutboundHttp()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var original = api;
        var created = await CreateTokenAsync(client, user, org, project);
        created.Status.ShouldBe(HttpStatusCode.Created);
        var secret = created.Text("secret")!;
        int outboundRequests = 0;
        await using var guarded = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(http =>
                http.PrimaryHandler = new OutboundGuard(() => Interlocked.Increment(ref outboundRequests))))));
        using var guardedClient = guarded.CreateClient();
        using var rsa = RSA.Create(2048);
        var bearer = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://token.actions.githubusercontent.com",
            Audience = "evidgate",
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "legacy-key" }, SecurityAlgorithms.RsaSha256),
        });
        Action<HttpRequestHeaders> legacy = headers => headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        foreach (var body in new object?[] { null, new { binding_challenge = "legacy" }, new { ingest_token = secret } })
        {
            var refusal = await guardedClient.SendAsync(HttpMethod.Post, "/v1/ci/token", legacy, body);
            refusal.Status.ShouldBe(HttpStatusCode.Unauthorized);
            refusal.ErrorCode.ShouldBe("token_invalid");
        }

        foreach (var body in new object?[] { null, new { }, new { ingest_token = (string?)null }, new { ingest_token = string.Empty } })
        {
            var missing = await guardedClient.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }, body);
            missing.Status.ShouldBe(HttpStatusCode.Unauthorized);
            missing.ErrorCode.ShouldBe("token_invalid");
        }

        foreach (var challenge in new string?[] { null, "legacy" })
        {
            var unknownMember = await guardedClient.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = secret, binding_challenge = challenge });
            unknownMember.Status.ShouldBe(HttpStatusCode.BadRequest);
        }
        (await ExchangeIngestAsync(guardedClient, secret)).Status.ShouldBe(HttpStatusCode.OK);
        Volatile.Read(ref outboundRequests).ShouldBe(0);
    }

    private sealed class OutboundGuard(Action attempted) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            attempted();
            throw new InvalidOperationException("The project token exchange must never use outbound HTTP.");
        }
    }

    [Fact]
    public async Task Exchange_MintedProducer_CannotEscapeItsScope()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var other = await CreateProjectAsync(client, user, org, "other-project");
        var created = await CreateTokenAsync(client, user, org, project);
        var token = (await ExchangeIngestAsync(client, created.Text("secret")!)).Text("token")!;

        Action<HttpRequestHeaders> producer = headers => headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        // Authenticated as a service caller bound to the org, yet nothing outside the ci_producer set is allowed.
        (await client.SendAsync(HttpMethod.Post, $"/v1/orgs/{org}/memberships", producer, new { email = "x@example.test", roles = Array.Empty<string>() }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{other}", producer)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{Guid.CreateVersion7()}", producer)).Status.ShouldBe(HttpStatusCode.NotFound);

        // A changed signature authenticates nobody.
        var tampered = token[..^3] + (token[^3] == 'A' ? "BBB" : "AAA");
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", h => h.TryAddWithoutValidation("Authorization", "Bearer " + tampered)))
            .Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Exchange_WhenCiAuthIsNotConfigured_Is503()
    {
        var database = _dedicated!.Name;
        await using var api = _hosts.RealResolver(database);
        using var client = api.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = "egt_abcdefghijkl_" + new string('a', 43) });
        answer.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Exchange_IsRateLimited_WithRetryAfter()
    {
        var database = _dedicated!.Name;
        // The window lives in the database now, so the count starts at zero only on a fresh private Ci host.
        await using var api = TenancyApiFactory.Ci(
            _fixture.AppConnectionString(database),
            new Dictionary<string, string?> { ["EVIDGATE:Identity:RateLimits:CiExchangeLimit"] = "3" });
        using var client = api.CreateClient();

        var answers = new List<ApiAnswer>();
        for (var i = 0; i < 5; i++)
        {
            answers.Add(await client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }));
        }

        answers.Take(3).ShouldAllBe(answer => answer.Status == HttpStatusCode.Unauthorized);
        var limited = answers[3];
        limited.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.ErrorCode.ShouldBe("rate_limited");
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }
}
