using System.Net;
using System.Text;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1-T1.2 / AC2 + AC8 (google half): the provider's two halves against a fake provider. The
/// authorization URL carries exactly the OIDC + PKCE parameters the story fixes, the code exchange posts
/// the PKCE form and reads only the <c>id_token</c>, and every token-endpoint failure lands on the typed
/// mapping — 4xx (a rejected or reused code) is <c>token_invalid</c>, while 429, 5xx, a redirect, a
/// timeout, a transport error, an oversized body or a non-JSON body is <c>unavailable</c>.
/// </summary>
/// <remarks>
/// Pure: both provider endpoints are <see cref="GoogleTestHttpHandler"/> stand-ins on the real allowlisted
/// hosts, so no socket is opened and the code is never retried (a single-use code would be burned).
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GoogleProviderFlowTests
{
    [Fact]
    public void GoogleProvider_IsNamedGoogle_AndIsConfiguredWhenBothCredentialsExist()
    {
        var harness = new FlowHarness();

        harness.Provider.Name.ShouldBe("google");
        harness.Provider.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public void GoogleProvider_AuthorizationUrl_CarriesTheOidcAndPkceParameters()
    {
        var harness = new FlowHarness();

        var url = harness.Provider.BuildAuthorizationUrl(AuthorizationRequest());

        url.Scheme.ShouldBe(Uri.UriSchemeHttps);
        url.Host.ShouldBe("accounts.google.com");
        var query = Query(url);
        query["response_type"].ShouldBe("code");
        query["client_id"].ShouldBe(GoogleTestHarness.ClientId);
        query["redirect_uri"].ShouldBe(GoogleTestHarness.RedirectUri.AbsoluteUri);
        query["scope"].ShouldBe("openid email profile");
        query["state"].ShouldBe("test-state-1");
        query["nonce"].ShouldBe(GoogleTestHarness.Nonce);
        query["code_challenge"].ShouldBe("test-code-challenge-1");
        query["code_challenge_method"].ShouldBe("S256");
        query.ContainsKey("prompt").ShouldBeFalse("the story fixes the parameter set: no prompt, no login_hint");
        query.ContainsKey("login_hint").ShouldBeFalse();
    }

    [Fact]
    public void GoogleProvider_AuthorizationUrl_EscapesEveryValue()
    {
        var harness = new FlowHarness();
        var request = new LoginAuthorizationRequest("a&b=c d", "nonce&1", "challenge+=1", GoogleTestHarness.RedirectUri);

        var url = harness.Provider.BuildAuthorizationUrl(request);

        url.Query.IndexOf("a&b=c d", StringComparison.Ordinal).ShouldBe(
            -1, "the state must be percent-encoded, so no raw '&', '=' or space reaches the query");
        var query = Query(url);
        query["state"].ShouldBe("a&b=c d");
        query["nonce"].ShouldBe("nonce&1");
        query["code_challenge"].ShouldBe("challenge+=1");
    }

    [Fact]
    public void GoogleProvider_AuthorizationUrl_RequiresStateNonceAndChallenge()
    {
        var harness = new FlowHarness();
        var incomplete = new LoginAuthorizationRequest(string.Empty, GoogleTestHarness.Nonce, "challenge", GoogleTestHarness.RedirectUri);

        Should.Throw<ArgumentException>(() => harness.Provider.BuildAuthorizationUrl(incomplete));
    }

    [Fact]
    public async Task GoogleProvider_HappyPath_ExchangesTheCodeAndReturnsTheIdentity()
    {
        var harness = new FlowHarness();

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBeNull();
        var identity = result.Identity.ShouldNotBeNull();
        identity.Provider.ShouldBe("google");
        identity.Subject.ShouldBe(GoogleTestHarness.Subject);
        identity.Email.ShouldBe(GoogleTestHarness.Email);
        identity.EmailVerified.ShouldBeTrue();
        identity.DisplayName.ShouldBe(GoogleTestHarness.DisplayName);

        harness.TokenHandler.Calls.ShouldBe(1);
        harness.JwksHandler.Calls.ShouldBe(1);

        var form = Form(harness.TokenHandler.Bodies.Single());
        form["grant_type"].ShouldBe("authorization_code");
        form["code"].ShouldBe("test-authorization-code");
        form["redirect_uri"].ShouldBe(GoogleTestHarness.RedirectUri.AbsoluteUri);
        form["client_id"].ShouldBe(GoogleTestHarness.ClientId);
        form["client_secret"].ShouldBe(GoogleTestHarness.ClientSecret);
        form["code_verifier"].ShouldBe("test-code-verifier-0123456789abcdef");
    }

    [Fact]
    public async Task GoogleProvider_RejectedCode_IsTokenInvalid_AndIsNeverRetried()
    {
        var harness = new FlowHarness(token: (_, _) => Task.FromResult(
            GoogleTestHttpHandler.Json("""{"error":"invalid_grant","error_description":"provider-detail"}""", HttpStatusCode.BadRequest)));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.TokenHandler.Calls.ShouldBe(1, "an authorization code is single-use, so the POST is never retried");
        harness.JwksHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GoogleProvider_TooManyRequests_IsProviderUnavailable()
    {
        var harness = new FlowHarness(token: (_, _) => Task.FromResult(
            GoogleTestHttpHandler.Json("""{"error":"rate_limited"}""", HttpStatusCode.TooManyRequests)));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GoogleProvider_TokenEndpointFailure_IsProviderUnavailable()
    {
        var harness = new FlowHarness(token: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GoogleProvider_JwksOutage_IsProviderUnavailable_NotTokenInvalid()
    {
        // AC8 end to end: Google being down is our unavailability (503), never a dead token (401), even
        // though the token itself was fine and only its signature could not be checked.
        var harness = new FlowHarness(jwks: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
        harness.TokenHandler.Calls.ShouldBe(1);
        harness.JwksHandler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GoogleProvider_TokenEndpointTimeout_IsProviderUnavailable()
    {
        var harness = new FlowHarness(token: (_, _) => throw new TaskCanceledException("the provider did not answer"));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GoogleProvider_TransportError_IsProviderUnavailable()
    {
        var harness = new FlowHarness(token: (_, _) => throw new HttpRequestException("the connection was reset"));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GoogleProvider_RedirectResponse_IsNotFollowed()
    {
        var harness = new FlowHarness(token: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://token.evidgate-attacker.test/token", UriKind.Absolute) },
        }));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
        harness.TokenHandler.Calls.ShouldBe(1, "a redirect must never move the exchange to another host");
    }

    [Fact]
    public async Task GoogleProvider_OversizedResponse_IsProviderUnavailable()
    {
        var tokens = new GoogleTokenFactory(new MutableTimeProvider(GoogleTestHarness.Now), new GoogleTestKeys());
        var padded = TokenJson(tokens.Mint()) + new string(' ', 4096);

        // A declared Content-Length over the cap is refused before the body is read.
        var declared = new FlowHarness(
            token: (_, _) => Task.FromResult(GoogleTestHttpHandler.Json(padded)),
            settings: GoogleTestHarness.Options(maxResponseBytes: 1024));

        (await declared.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken)).Failure
            .ShouldBe(LoginFailure.ProviderUnavailable);

        // And a body without a Content-Length is refused once the capped read passes the cap.
        var streamed = new FlowHarness(
            token: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new LengthlessContent(padded),
            }),
            settings: GoogleTestHarness.Options(maxResponseBytes: 1024));

        (await streamed.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken)).Failure
            .ShouldBe(LoginFailure.ProviderUnavailable);

        declared.JwksHandler.Calls.ShouldBe(0);
        streamed.JwksHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GoogleProvider_NonJsonResponse_IsProviderUnavailable()
    {
        var harness = new FlowHarness(token: (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html") }));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GoogleProvider_ResponseWithoutIdToken_IsProviderUnavailable()
    {
        var harness = new FlowHarness(token: (_, _) => Task.FromResult(
            GoogleTestHttpHandler.Json("""{"access_token":"ignored","token_type":"Bearer"}""")));

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GoogleProvider_IdTokenWithAnotherNonce_IsTokenInvalid()
    {
        var harness = new FlowHarness();
        harness.TokenOverride = () => harness.Tokens.Mint((_, claims) => claims["nonce"] = "another-nonce");

        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.JwksHandler.Calls.ShouldBe(1, "the signature and claims are still checked before the nonce is refused");
    }

    [Fact]
    public async Task GoogleProvider_UnconfiguredProvider_IsProviderUnavailable_WithoutAnOutboundCall()
    {
        var harness = new FlowHarness(settings: GoogleTestHarness.Options(clientSecret: null));

        harness.Provider.IsConfigured.ShouldBeFalse();
        var result = await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
        harness.TokenHandler.Calls.ShouldBe(0);
        harness.JwksHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GoogleProvider_ShortCode_IsTokenInvalid_WithoutAnOutboundCall()
    {
        var harness = new FlowHarness();

        var result = await harness.Provider.CompleteAsync(
            Callback(code: string.Empty),
            TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.TokenHandler.Calls.ShouldBe(0);
    }

    private static LoginAuthorizationRequest AuthorizationRequest() =>
        new("test-state-1", GoogleTestHarness.Nonce, "test-code-challenge-1", GoogleTestHarness.RedirectUri);

    private static LoginCallbackRequest Callback(string code = "test-authorization-code") => new(
        Code: code,
        CodeVerifier: "test-code-verifier-0123456789abcdef",
        Nonce: GoogleTestHarness.Nonce,
        RedirectUri: GoogleTestHarness.RedirectUri);

    private static string TokenJson(string idToken) =>
        $$"""{"access_token":"ignored","token_type":"Bearer","expires_in":3599,"id_token":"{{idToken}}"}""";

    private static Dictionary<string, string> Query(Uri uri) => Decode(uri.Query.TrimStart('?'), plusAsSpace: false);

    private static Dictionary<string, string> Form(string body) => Decode(body, plusAsSpace: true);

    private static Dictionary<string, string> Decode(string encoded, bool plusAsSpace)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            separator.ShouldBeGreaterThan(-1, "every parameter of this form is a name=value pair");

            var name = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(plusAsSpace ? pair[(separator + 1)..].Replace('+', ' ') : pair[(separator + 1)..]);
            result[name] = value;
        }

        return result;
    }

    /// <summary>The provider under test with both of its endpoints replaced by stand-ins.</summary>
    private sealed class FlowHarness
    {
        internal FlowHarness(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? token = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? jwks = null,
            GoogleLoginOptions? settings = null)
        {
            Settings = settings ?? GoogleTestHarness.Options();
            Clock = new MutableTimeProvider(GoogleTestHarness.Now);
            Keys = new GoogleTestKeys();
            Tokens = new GoogleTokenFactory(Clock, Keys);

            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> defaultToken =
                (_, _) => Task.FromResult(GoogleTestHttpHandler.Json(TokenJson(TokenOverride?.Invoke() ?? Tokens.Mint())));
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> defaultJwks =
                (_, _) => Task.FromResult(GoogleTestHttpHandler.Json(Keys.JwksJson()));

            TokenHandler = new GoogleTestHttpHandler(token ?? defaultToken);
            JwksHandler = new GoogleTestHttpHandler(jwks ?? defaultJwks);

            var options = Options.Create(Settings);
            var cache = new HttpGoogleJwksCache(
                new HttpClient(JwksHandler),
                options,
                Clock,
                NullLogger<HttpGoogleJwksCache>.Instance);

            Provider = new GoogleLoginProvider(
                new HttpClient(TokenHandler),
                new GoogleIdTokenValidator(cache, options, Clock),
                options,
                NullLogger<GoogleLoginProvider>.Instance);
        }

        internal GoogleLoginOptions Settings { get; }

        internal MutableTimeProvider Clock { get; }

        internal GoogleTestKeys Keys { get; }

        internal GoogleTokenFactory Tokens { get; }

        internal GoogleTestHttpHandler TokenHandler { get; }

        internal GoogleTestHttpHandler JwksHandler { get; }

        /// <summary>Lets a test replace the ID token its default token endpoint returns.</summary>
        internal Func<string>? TokenOverride { get; set; }

        internal GoogleLoginProvider Provider { get; }
    }
}
