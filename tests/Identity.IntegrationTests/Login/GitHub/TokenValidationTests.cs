using System.Net;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.GitHub;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.GitHub;

/// <summary>
/// 02.3.1-T1.3 / AC4 + AC8 (github half): what the GitHub provider accepts from the token endpoint, from
/// <c>GET /user</c> and from <c>GET /user/emails</c>. GitHub returns no ID token (ruling T6), so every
/// identity fact is read from those two documents and every refusal is the documented one: a rejected code,
/// a missing or blank <c>access_token</c>, an unsupported <c>token_type</c>, an id that is not a positive
/// integer, a 401 or a dead token is <c>token_invalid</c>; an outage, a 5xx, a 429, a body that is not JSON
/// or a document of the wrong shape is <c>unavailable</c>.
/// </summary>
/// <remarks>
/// Pure: both clients are <see cref="GitHubTestHttpHandler"/> stand-ins on the real allowlisted hosts, so no
/// socket is opened. The done-when filter of the task file is <c>TokenValidationTests.GitHub*</c>.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class TokenValidationTests
{
    // -----------------------------------------------------------------------------------------------
    // The token exchange: only a usable access token gets past it.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GitHubProvider_MissingAccessToken_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json("""{"token_type":"bearer"}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.ApiHandler.Calls.ShouldBe(0, "an exchange without a token never reaches the API");
    }

    [Fact]
    public async Task GitHubProvider_BlankAccessToken_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json("""{"access_token":"   ","token_type":"bearer"}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.ApiHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_ErrorBodyOnSuccess_IsTokenInvalid()
    {
        // GitHub answers a rejected code with 200 and an error body when the caller asked for JSON.
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json("""{"error":"bad_verification_code","error_description":"provider-said-this"}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.ApiHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_NonJsonTokenBody_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("access_token=abc&token_type=bearer", System.Text.Encoding.UTF8, "text/plain"),
            }));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Theory]
    [InlineData("mac")]
    [InlineData("")]
    public async Task GitHubProvider_UnsupportedTokenType_IsTokenInvalid(string tokenType)
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json(
                $$"""{"access_token":"{{GitHubTestHarness.AccessToken}}","token_type":"{{tokenType}}"}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.ApiHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_TokenEndpoint5xx_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_TokenEndpoint429_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json("""{"error":"rate_limited"}""", HttpStatusCode.TooManyRequests)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_TokenEndpoint408_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.RequestTimeout)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable, "a slow upstream is an outage, not a rejected code");
    }

    [Fact]
    public async Task GitHubProvider_PkceVerifierRedirectUriAndClientSecretTravelInTheBody()
    {
        var harness = new GitHubProviderHarness();

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull();

        var post = harness.TokenHandler.Requests.ShouldHaveSingleItem();
        post.Uri.ShouldNotBeNull();
        post.Uri.AbsoluteUri.ShouldBe(
            "https://github.com/login/oauth/access_token",
            "every credential of the exchange is a body field, never a query parameter a log would keep");
        post.Accept.ShouldBe("application/json", "GitHub answers form-encoded unless the caller asks for JSON");

        var form = Form(post.Body.ShouldNotBeNull());
        form["client_id"].ShouldBe(GitHubTestHarness.ClientId);
        form["client_secret"].ShouldBe(GitHubTestHarness.ClientSecret);
        form["code"].ShouldBe("test-authorization-code");
        form["redirect_uri"].ShouldBe(GitHubTestHarness.RedirectUri.AbsoluteUri);
        form["code_verifier"].ShouldBe("test-code-verifier-0123456789abcdef");
    }

    [Fact]
    public async Task GitHubProvider_TokenPostIsNeverRetried()
    {
        var harness = new GitHubProviderHarness(token: (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        harness.TokenHandler.Calls.ShouldBe(1, "the authorization code is single-use, so the POST is never retried");
    }

    [Fact]
    public async Task GitHubProvider_ApiCalls_CarryBearerTokenVersionAndUserAgent()
    {
        var harness = new GitHubProviderHarness();

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull();

        var requests = harness.ApiHandler.Requests;
        requests.Count.ShouldBe(2);
        requests[0].Uri!.AbsoluteUri.ShouldBe("https://api.github.com/user");
        requests[1].Uri!.AbsoluteUri.ShouldBe("https://api.github.com/user/emails");

        foreach (var request in requests)
        {
            request.Authorization.ShouldBe($"Bearer {GitHubTestHarness.AccessToken}");
            request.Accept.ShouldBe("application/vnd.github+json");
            request.ApiVersion.ShouldBe("2022-11-28");
            request.UserAgent.ShouldBe("Evidgate", "GitHub refuses an API call without a user agent");
        }
    }

    // -----------------------------------------------------------------------------------------------
    // GET /user: the subject is the numeric id, nothing else.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GitHubProvider_SubjectIsTheDecimalId()
    {
        var harness = new GitHubProviderHarness();

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBeNull();
        var identity = result.Identity.ShouldNotBeNull();
        identity.Provider.ShouldBe("github");
        identity.Subject.ShouldBe(GitHubTestHarness.Subject);
        identity.Email.ShouldBe(GitHubTestHarness.Email);
        identity.EmailVerified.ShouldBeTrue("a handler returns an identity only when the address is verified");
        identity.DisplayName.ShouldBe(GitHubTestHarness.DisplayName);
    }

    [Fact]
    public async Task GitHubProvider_SubjectMissing_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json("""{"login":"test-user","name":"Test User"}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
        harness.ApiHandler.Calls.ShouldBe(1, "an id that cannot be read stops the flow before the emails call");
    }

    [Fact]
    public async Task GitHubProvider_SubjectAsString_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json("""{"id":"123456789","login":"test-user"}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GitHubProvider_SubjectAsFloat_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json("""{"id":123456789.5,"login":"test-user"}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task GitHubProvider_SubjectNotPositive_IsTokenInvalid(string id)
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json(GitHubTestHarness.UserJson(id))));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GitHubProvider_UserWithoutName_FallsBackToLogin()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json(GitHubTestHarness.UserJson(name: null))));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull().DisplayName.ShouldBe(GitHubTestHarness.Login);
    }

    [Fact]
    public async Task GitHubProvider_BlankDisplayName_IsNull()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json("""{"id":123456789,"login":"   ","name":"  "}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull().DisplayName.ShouldBeNull();
    }

    [Fact]
    public async Task GitHubProvider_LongDisplayName_IsTruncatedTo200Characters()
    {
        var longName = new string('n', 260);
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json(GitHubTestHarness.UserJson(name: longName))));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull().DisplayName.ShouldBe(new string('n', 200));
    }

    // -----------------------------------------------------------------------------------------------
    // GET /user/emails: only a verified address may bind (AC4).
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GitHubProvider_NoVerifiedEmail_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(
            api: Api(emails: () => GitHubTestHttpHandler.Json(GitHubTestHarness.EmailsJson(verified: false))));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GitHubProvider_PrimaryButUnverified_NeverBinds()
    {
        // AC4 exactly: the primary address is unverified, so it is not a candidate even though it is primary.
        var harness = new GitHubProviderHarness(
            api: Api(emails: () => GitHubTestHttpHandler.Json(
                """[{"email":"primary@evidgate.test","primary":true,"verified":false}]""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GitHubProvider_VerifiedNonPrimary_IsSelectedWhenPrimaryIsUnverified()
    {
        var harness = new GitHubProviderHarness(
            api: Api(emails: () => GitHubTestHttpHandler.Json(
                """
                [
                  {"email":"primary@evidgate.test","primary":true,"verified":false},
                  {"email":"second@evidgate.test","primary":false,"verified":true}
                ]
                """)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull().Email.ShouldBe("second@evidgate.test");
    }

    [Fact]
    public async Task GitHubProvider_VerifiedPrimary_IsPreferredOverAnEarlierVerifiedEntry()
    {
        var harness = new GitHubProviderHarness(
            api: Api(emails: () => GitHubTestHttpHandler.Json(
                """
                [
                  {"email":"first@evidgate.test","primary":false,"verified":true},
                  {"email":"primary@evidgate.test","primary":true,"verified":true}
                ]
                """)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull().Email.ShouldBe("primary@evidgate.test");
    }

    [Fact]
    public async Task GitHubProvider_VerifiedFlagAsString_IsNotAVerification()
    {
        // "true" is not the JSON boolean true: a document that cannot assert verification never binds.
        var harness = new GitHubProviderHarness(
            api: Api(emails: () => GitHubTestHttpHandler.Json(
                """[{"email":"user@evidgate.test","primary":true,"verified":"true"}]""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GitHubProvider_EmailIsTrimmedAndLowerCased()
    {
        var harness = new GitHubProviderHarness(
            api: Api(emails: () => GitHubTestHttpHandler.Json(
                """[{"email":"  User@Evidgate.TEST  ","primary":true,"verified":true}]""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldNotBeNull().Email.ShouldBe("user@evidgate.test");
    }

    [Fact]
    public async Task GitHubProvider_EmailLongerThan320Characters_IsNotUsable()
    {
        var tooLong = new string('a', 312) + "@evidgate.test";
        tooLong.Length.ShouldBeGreaterThan(320, "the fixture must really exceed the cap");

        var harness = new GitHubProviderHarness(
            api: Api(emails: () => GitHubTestHttpHandler.Json(GitHubTestHarness.EmailsJson(tooLong))));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GitHubProvider_EmailsResponseNotAnArray_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(
            api: Api(emails: () => GitHubTestHttpHandler.Json("""{"message":"Not Found"}""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(
            LoginFailure.ProviderUnavailable,
            "a document of the wrong shape is the provider misbehaving (503), not a dead token (401)");
    }

    // -----------------------------------------------------------------------------------------------
    // The API status mapping.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GitHubProvider_UserEndpoint401_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GitHubProvider_UserEndpoint403_IsProviderUnavailable()
    {
        // GitHub uses 403 for its secondary rate limit and abuse detection: "later", not "bad token".
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_UserEndpoint408_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => new HttpResponseMessage(HttpStatusCode.RequestTimeout)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_UserEndpoint404_IsTokenInvalid()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.TokenInvalid);
    }

    [Fact]
    public async Task GitHubProvider_Emails5xx_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(
            api: Api(emails: () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_UserDocumentNotAnObject_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(
            api: Api(user: () => GitHubTestHttpHandler.Json("""[{"id":123456789}]""")));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_NonJsonApiBody_IsProviderUnavailable()
    {
        var harness = new GitHubProviderHarness(
            api: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>not json</html>", System.Text.Encoding.UTF8, "text/html"),
            }));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    /// <summary>An API responder with the two documents scripted separately; the path decides which one answers.</summary>
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Api(
        Func<HttpResponseMessage>? user = null,
        Func<HttpResponseMessage>? emails = null) =>
        (request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath.EndsWith("/user/emails", StringComparison.Ordinal) == true
                ? (emails ?? (() => GitHubTestHarness.DefaultApi(request)))()
                : (user ?? (() => GitHubTestHarness.DefaultApi(request)))());

    private static Dictionary<string, string> Form(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            separator.ShouldBeGreaterThan(-1, "every field of this form is a name=value pair");

            result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return result;
    }
}
