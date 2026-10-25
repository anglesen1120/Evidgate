using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1-T1.1, 02.3.1 §9 (<c>PkceTests</c>): the state, nonce and PKCE half of the login protocol — the
/// S256 challenge of the verifier the cookie carries, the one-time <c>state</c>, and the 401
/// <c>token_invalid</c> every mismatch, replay or tampered cookie gets (AC2's replay half, AC8's shape).
/// </summary>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class PkceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_Sends_State_Nonce_And_The_S256_Challenge_Of_The_Cookie_Verifier()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake", "/runs/42?x=1#a"));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();

        var (payload, _) = host.PendingLogin(response);
        payload.Provider.ShouldBe("fake");
        payload.ReturnUrl.ShouldBe("/runs/42?x=1#a");
        payload.Verifier.Length.ShouldBeGreaterThanOrEqualTo(43);
        payload.Verifier.Length.ShouldBeLessThanOrEqualTo(128);
        payload.State.Length.ShouldBeGreaterThanOrEqualTo(43);
        payload.Nonce.Length.ShouldBeGreaterThanOrEqualTo(43);

        var query = QueryOf(response.Headers.Location!);
        query["response_type"].ShouldBe("code");
        query["state"].ShouldBe(payload.State);
        query["nonce"].ShouldBe(payload.Nonce);
        query["code_challenge_method"].ShouldBe("S256");
        query["code_challenge"].ShouldBe(S256(payload.Verifier));
        // 02.3.1 §5: the redirect URI is the deployment's, built from EVIDGATE:Login:PublicBaseUrl.
        query["redirect_uri"].ShouldBe(LoginTestHost.PublicBaseUrl + "/v1/auth/oidc/fake/callback");
    }

    [Fact]
    public async Task Callback_Hands_The_Cookie_Verifier_And_Nonce_To_The_Provider()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        var completion = new FakeLoginCompletion { Target = "/runs/42?x=1#a" };
        using var host = LoginTestHost.Start(new MutableClock(Now), completion, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake", "/runs/42?x=1#a"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        callback.Headers.Location!.OriginalString.ShouldBe("/runs/42?x=1#a");

        provider.Completions.ShouldBe(1);
        provider.LastCallback.ShouldNotBeNull();
        provider.LastCallback!.CodeVerifier.ShouldBe(payload.Verifier);
        provider.LastCallback.Nonce.ShouldBe(payload.Nonce);
        provider.LastCallback.RedirectUri.ToString().ShouldBe(LoginTestHost.PublicBaseUrl + "/v1/auth/oidc/fake/callback");

        completion.Calls.ShouldBe(1);
        completion.Last.ShouldNotBeNull();
        completion.Last!.Identity.Subject.ShouldBe("subject-1");
        completion.Last.ReturnUrl.ShouldBe("/runs/42?x=1#a");
    }

    [Fact]
    public async Task Callback_Of_The_Same_State_Twice_Is_Rejected_As_A_Replay()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        var completion = new FakeLoginCompletion();
        using var host = LoginTestHost.Start(new MutableClock(Now), completion, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var first = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));
        first.StatusCode.ShouldBe(HttpStatusCode.Found);

        // A client that kept a copy of the cookie and the state replays both: the state is spent.
        using var replay = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(1);
        completion.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Callback_With_A_Different_State_Is_Token_Invalid()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginCompletion(), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (_, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, "not-the-state"));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Callback_With_A_Tampered_Cookie_Is_Token_Invalid()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginCompletion(), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", FlipFirstCharacter(cookieValue), payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Callback_After_The_Cookie_Lifetime_Is_Token_Invalid()
    {
        var clock = new MutableClock(Now);
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(clock, new FakeLoginCompletion(), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        // Past EVIDGATE:Login:StateLifetimeSeconds (600 in this host).
        clock.Advance(TimeSpan.FromSeconds(601));

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Callback_Naming_Another_Provider_Than_The_Cookie_Is_Token_Invalid()
    {
        var started = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        var other = new FakeLoginProvider("other")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("other")),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginCompletion(), started, other);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("other", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        other.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Callback_Carrying_The_Providers_Error_Is_Token_Invalid_And_Never_Echoes_It()
    {
        var provider = new FakeLoginProvider("fake");
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(
                "fake",
                cookieValue,
                payload.State,
                code: null,
                error: "access_denied_" + FakeLoginProvider.Canary));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        (await LoginTestHost.BodyAsync(callback)).ShouldNotContain(FakeLoginProvider.Canary);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Callback_Without_A_Code_Or_With_An_Oversized_One_Is_Token_Invalid()
    {
        var provider = new FakeLoginProvider("fake");
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var missing = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State, code: null));
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(missing)).ShouldBe(ApiErrorCode.TokenInvalid);

        using var oversized = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State, code: new string('c', 2049)));
        oversized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(oversized)).ShouldBe(ApiErrorCode.TokenInvalid);

        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Callback_Without_Any_Cookie_Is_Token_Invalid()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue: null, state: "any-state"));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
    }

    [Fact]
    public async Task Start_Refuses_An_Off_App_ReturnUrl_And_Falls_Back_To_The_Root()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.StartRequest("fake", "//evil.example/steal"));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var (payload, _) = host.PendingLogin(response);
        payload.ReturnUrl.ShouldBe(ReturnUrlPolicy.Root);
    }

    [Fact]
    public async Task Start_For_An_Unknown_Provider_Is_404_Not_Found()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("saml"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.NotFound);
    }

    /// <summary>The provider's authorization URL, split into its query pairs.</summary>
    private static Dictionary<string, string> QueryOf(Uri location)
    {
        var query = location.Query.TrimStart('?');

        return query.Length == 0
            ? []
            : query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(
                    pair => Uri.UnescapeDataString(pair[0]),
                    pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty,
                    StringComparer.Ordinal);
    }

    /// <summary>The RFC 7636 S256 challenge, computed here rather than taken from the implementation.</summary>
    private static string S256(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// A cookie value whose first character no longer matches its signature. The first character is flipped
    /// on purpose: in the last one the low bits of a base64url group can be padding, so a change there might
    /// decode to the very same bytes.
    /// </summary>
    private static string FlipFirstCharacter(string value) =>
        (value[0] == 'A' ? 'B' : 'A') + value[1..];
}
