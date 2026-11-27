using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Authoring.Trackers.Application;
using Evidgate.Modules.Authoring.Trackers.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class TrackerProviderTests
{
    [Theory]
    [InlineData(TrackerProvider.JiraCloud, "read:jira-work offline_access", "auth.atlassian.com")]
    [InlineData(TrackerProvider.Linear, "read", "linear.app")]
    internal void Authorize_RequestsExactlyReadScopesAndFixedHost(TrackerProvider provider, string scope, string host)
    {
        using var client = new HttpClient(new StubHandler());
        var sut = Provider(client);
        var uri = new Uri(sut.Authorize(provider, "state", "verifier"));
        uri.Host.ShouldBe(host);
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["scope"].ToString().ShouldBe(scope);
        query["response_type"].ToString().ShouldBe("code");
        if (provider == TrackerProvider.Linear)
        {
            query["code_challenge_method"].ToString().ShouldBe("S256");
            query["code_challenge"].ToString().ShouldBe(WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes("verifier"))));
        }
        else
        {
            query["audience"].ToString().ShouldBe("api.atlassian.com");
            query["prompt"].ToString().ShouldBe("consent");
            query.ContainsKey("code_challenge").ShouldBeFalse();
        }
        uri.AbsoluteUri.ShouldNotContain("client-secret");
    }

    [Fact]
    public async Task Linear_PkceMismatch_RefusesTokens()
    {
        using var handler = new StubHandler(HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler);
        var sut = Provider(client);
        (await sut.ExchangeAsync(TrackerProvider.Linear, "code", "wrong-verifier", CancellationToken.None)).ShouldBeNull();
        handler.Body.ShouldContain("code_verifier=wrong-verifier");
        handler.Uri.ShouldBe(new Uri("https://api.linear.app/oauth/token"));
    }

    [Theory]
    [InlineData(TrackerProvider.JiraCloud)]
    [InlineData(TrackerProvider.Linear)]
    internal async Task Exchange_Success_ReadsBoundedTokensAndExactScopes(TrackerProvider provider)
    {
        var scope = provider == TrackerProvider.Linear ? "read" : "read:jira-work offline_access";
        using var handler = new StubHandler(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = "token-canary",
            refresh_token = "refresh-canary",
            scope,
            token_type = "Bearer",
            expires_in = 3600,
        }));
        using var client = new HttpClient(handler);
        var token = await Provider(client).ExchangeAsync(provider, "code", "verifier", CancellationToken.None);
        token.ShouldNotBeNull();
        token.AccessToken.ShouldBe("token-canary");
        string.Join(' ', token.GrantedScopes).ShouldBe(scope);
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Exchange_Redirect_DoesNotSendSecondCredentialedRequest()
    {
        using var handler = new StubHandler(HttpStatusCode.Redirect, "token-canary");
        using var client = new HttpClient(handler);
        (await Provider(client).ExchangeAsync(TrackerProvider.Linear, "code", "verifier", CancellationToken.None)).ShouldBeNull();
        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("http://api.linear.app/oauth/token")]
    [InlineData("https://api.linear.app.evil.test/oauth/token")]
    [InlineData("https://user@api.linear.app/oauth/token")]
    [InlineData("https://127.0.0.1/oauth/token")]
    [InlineData("https://api.linear.app:444/oauth/token")]
    public async Task Outbound_NonAllowlistedHost_IsNeverSent(string destination)
    {
        using var handler = new StubHandler();
        using var guard = new TrackerOutboundAllowlistHandler { InnerHandler = handler };
        using var client = new HttpClient(guard);
        await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync(destination));
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Exchange_OversizedOrWriteScopeResponse_IsRefused()
    {
        foreach (var body in new[] { new string('x', 65537), "{\"access_token\":\"token-canary\",\"scope\":\"read write\"}" })
        {
            using var handler = new StubHandler(HttpStatusCode.OK, body);
            using var client = new HttpClient(handler);
            (await Provider(client).ExchangeAsync(TrackerProvider.Linear, "code", "verifier", CancellationToken.None)).ShouldBeNull();
        }
    }

    [Fact]
    public void Options_AbsentProviders_AreValidButUnconfigured()
    {
        var jira = new JiraCloudTrackerOptions();
        var linear = new LinearTrackerOptions();
        var validator = new TrackerOAuthOptionsValidator();
        validator.Validate(null, jira).Succeeded.ShouldBeTrue();
        validator.Validate(null, linear).Succeeded.ShouldBeTrue();
        using var client = new HttpClient(new StubHandler());
        var sut = new TrackerProviderClient(client, Options.Create(jira), Options.Create(linear), TimeProvider.System);
        sut.IsConfigured(TrackerProvider.Linear).ShouldBeFalse();
        sut.IsConfigured(TrackerProvider.JiraCloud).ShouldBeFalse();
    }

    [Fact]
    public void Options_RedirectUriOfAnotherPath_IsRefusedAtBootWithoutEchoingValues()
    {
        var result = new TrackerOAuthOptionsValidator().Validate(null, new LinearTrackerOptions
        {
            ClientId = "client",
            ClientSecret = "secret-canary",
            RedirectUri = "https://evidgate.test/v1/tracker-connections/jira_cloud/callback",
        });

        result.Failed.ShouldBeTrue();
        string.Join(' ', result.Failures!).ShouldNotContain("secret-canary");
        string.Join(' ', result.Failures!).ShouldContain("EVIDGATE:TRACKERS:LINEAR:RedirectUri");
    }

    private static TrackerProviderClient Provider(HttpClient client) => new(
        client,
        Options.Create(new JiraCloudTrackerOptions { ClientId = "client", ClientSecret = "client-secret", RedirectUri = "https://evidgate.test/v1/tracker-connections/jira_cloud/callback" }),
        Options.Create(new LinearTrackerOptions { ClientId = "client", ClientSecret = "client-secret", RedirectUri = "https://evidgate.test/v1/tracker-connections/linear/callback" }),
        TimeProvider.System);

    private sealed class StubHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{}") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Body { get; private set; } = "";
        public Uri? Uri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.Headers.Location = new Uri("https://evil.test/collect");
            return response;
        }
    }
}
