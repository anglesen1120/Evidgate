using System.Net;
using System.Text;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.GitHub;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.GitHub;

/// <summary>
/// 02.3.1-T1.3 / AC1 (github half) + AC8: the provider's two halves against a fake provider. The
/// authorization URL carries exactly the OAuth2 + PKCE parameters the story fixes and no nonce (GitHub
/// returns no ID token), the code exchange posts the PKCE form and reads only the <c>access_token</c>, and
/// every token-endpoint failure lands on the typed mapping — a 4xx (a rejected or reused code) is
/// <c>token_invalid</c>, while 429, 5xx, a redirect, a timeout, a transport error or an oversized body is
/// <c>unavailable</c>.
/// </summary>
/// <remarks>
/// Pure: both clients are <see cref="GitHubTestHttpHandler"/> stand-ins on the real allowlisted hosts, so no
/// socket is opened and the code is never retried (a single-use code would be burned).
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GitHubProviderFlowTests
{
    [Fact]
    public void GitHubProvider_IsNamedGitHub_AndIsConfiguredWhenBothCredentialsExist()
    {
        var harness = new GitHubProviderHarness();

        harness.Provider.Name.ShouldBe("github");
        harness.Provider.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public void GitHubProvider_AuthorizationUrl_CarriesTheOauth2AndPkceParameters()
    {
        var harness = new GitHubProviderHarness();

        var url = harness.Provider.BuildAuthorizationUrl(GitHubTestHarness.AuthorizationRequest());

        url.Scheme.ShouldBe(Uri.UriSchemeHttps);
        url.Host.ShouldBe("github.com");
        url.AbsolutePath.ShouldBe("/login/oauth/authorize");

        var query = Query(url);
        query["response_type"].ShouldBe("code");
        query["client_id"].ShouldBe(GitHubTestHarness.ClientId);
        query["redirect_uri"].ShouldBe(GitHubTestHarness.RedirectUri.AbsoluteUri);
        query["scope"].ShouldBe("read:user user:email");
        query["state"].ShouldBe("test-state-1");
        query["code_challenge"].ShouldBe("test-code-challenge-1");
        query["code_challenge_method"].ShouldBe("S256");

        // GitHub is OAuth2, not OIDC: there is no ID token to carry a nonce, so the parameter must not be
        // sent (a provider that ignores it would hide a protocol mistake).
        query.ContainsKey("nonce").ShouldBeFalse("OAuth2 has no nonce: T6");
        query.ContainsKey("prompt").ShouldBeFalse("the story fixes the parameter set");
        query.ContainsKey("login_hint").ShouldBeFalse();
    }

    [Fact]
    public void GitHubProvider_AuthorizationUrl_DoesNotRequireANonce()
    {
        var harness = new GitHubProviderHarness();
        var withoutNonce = new LoginAuthorizationRequest("test-state-1", string.Empty, "test-code-challenge-1", GitHubTestHarness.RedirectUri);

        var url = harness.Provider.BuildAuthorizationUrl(withoutNonce);

        Query(url)["state"].ShouldBe("test-state-1");
    }

    [Fact]
    public void GitHubProvider_AuthorizationUrl_EscapesEveryValue()
    {
        var harness = new GitHubProviderHarness();
        var request = new LoginAuthorizationRequest("a&b=c d", "nonce&1", "challenge+=1", GitHubTestHarness.RedirectUri);

        var url = harness.Provider.BuildAuthorizationUrl(request);

        url.Query.IndexOf("a&b=c d", StringComparison.Ordinal).ShouldBe(
            -1, "the state must be percent-encoded, so no raw '&', '=' or space reaches the query");
        var query = Query(url);
        query["state"].ShouldBe("a&b=c d");
        query["code_challenge"].ShouldBe("challenge+=1");
    }

    [Fact]
    public void GitHubProvider_AuthorizationUrl_RequiresStateAndChallenge()
    {
        var harness = new GitHubProviderHarness();

        Should.Throw<ArgumentException>(() => harness.Provider.BuildAuthorizationUrl(
            new LoginAuthorizationRequest(string.Empty, string.Empty, "challenge", GitHubTestHarness.RedirectUri)));
        Should.Throw<ArgumentException>(() => harness.Provider.BuildAuthorizationUrl(
            new LoginAuthorizationRequest("state", string.Empty, string.Empty, GitHubTestHarness.RedirectUri)));
    }

    [Fact]
    public async Task GitHubProvider_HappyPath_ExchangesTheCodeAndReturnsTheIdentity()
    {
        var harness = new GitHubProviderHarness();

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBeNull();
        var identity = result.Identity.ShouldNotBeNull();
        identity.Provider.ShouldBe("github");
        identity.Subject.ShouldBe(GitHubTestHarness.Subject);
        identity.Email.ShouldBe(GitHubTestHarness.Email);
        identity.EmailVerified.ShouldBeTrue();
        identity.DisplayName.ShouldBe(GitHubTestHarness.DisplayName);

        harness.TokenHandler.Calls.ShouldBe(1);
        harness.ApiHandler.Calls.ShouldBe(2, "the subject comes from /user and the email from /user/emails");
    }

    [Fact]
    public async Task GitHubProvider_RejectedCode_IsTokenInvalid_AndIsNeverRetried()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json("""{"error":"bad_verification_code","error_description":"provider-detail"}""", HttpStatusCode.BadRequest)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.TokenHandler.Calls.ShouldBe(1, "an authorization code is single-use, so the POST is never retried");
        harness.ApiHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_TokenEndpointTimeout_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => throw new TaskCanceledException("the provider did not answer"));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_TransportError_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => throw new HttpRequestException("the connection was reset"));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_ApiTransportError_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(api: (_, _) => throw new HttpRequestException("the connection was reset"));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_RedirectResponse_IsNotFollowed()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://token.evidgate-attacker.test/token", UriKind.Absolute) },
        }));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
        harness.TokenHandler.Calls.ShouldBe(1, "a redirect must never move the exchange to another host");
        harness.ApiHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_OversizedTokenResponse_IsProviderUnavailable()
    {
        var padded = GitHubTestHarness.TokenJson() + new string(' ', 4096);

        // A declared Content-Length over the cap is refused before the body is read.
        var declared = new GitHubProviderHarness(
            token: (_, _) => Task.FromResult(GitHubTestHttpHandler.Json(padded)),
            settings: GitHubTestHarness.Options(maxResponseBytes: 1024));

        (await declared.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken)).Failure
            .ShouldBe(LoginFailure.ProviderUnavailable);

        // And a body that declares no length at all is refused once the capped read passes the cap.
        var streamedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(padded))),
        };
        streamedResponse.Content.Headers.ContentLength = null;

        var streamed = new GitHubProviderHarness(
            token: (_, _) => Task.FromResult(streamedResponse),
            settings: GitHubTestHarness.Options(maxResponseBytes: 1024));

        (await streamed.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken)).Failure
            .ShouldBe(LoginFailure.ProviderUnavailable);

        declared.ApiHandler.Calls.ShouldBe(0);
        streamed.ApiHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_OversizedUserResponse_IsProviderUnavailable()
    {
        var padded = GitHubTestHarness.UserJson() + new string(' ', 4096);

        var harness = new GitHubProviderHarness(
            api: (_, _) => Task.FromResult(GitHubTestHttpHandler.Json(padded)),
            settings: GitHubTestHarness.Options(maxResponseBytes: 1024));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_UnconfiguredProvider_IsProviderUnavailable_WithoutAnOutboundCall()
    {
        var harness = new GitHubProviderHarness(settings: GitHubTestHarness.Options(clientSecret: null));

        harness.Provider.IsConfigured.ShouldBeFalse();
        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
        harness.TokenHandler.Calls.ShouldBe(0);
        harness.ApiHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_ShortCode_IsTokenInvalid_WithoutAnOutboundCall()
    {
        var harness = new GitHubProviderHarness();

        var result = await harness.Provider.CompleteAsync(
            GitHubTestHarness.Callback(code: string.Empty),
            TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.TokenHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_OversizedCode_IsTokenInvalid_WithoutAnOutboundCall()
    {
        var harness = new GitHubProviderHarness();

        var result = await harness.Provider.CompleteAsync(
            GitHubTestHarness.Callback(code: new string('c', 2049)),
            TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.TokenHandler.Calls.ShouldBe(0);
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            separator.ShouldBeGreaterThan(-1, "every parameter of this query is a name=value pair");

            result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return result;
    }
}
