using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Invites;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1-T2 (Q1): <c>POST /v1/auth/invite-token</c> stages an invite token in the
/// <c>__Host-evidgate_invite</c> cookie — a <c>__Host-</c> cookie (Secure, <c>Path=/</c>, no <c>Domain</c>) plus
/// <c>HttpOnly</c>, <c>SameSite=Lax</c> and a bounded lifetime of at most ten minutes — and answers 204. A
/// malformed token is 422 <c>request_invalid</c> with no cookie at all, an oversized body is 413, and the route
/// spends the client's exchange window like the login routes do (429 <c>rate_limited</c> + <c>Retry-After</c>).
/// The staged value is unreadable: the token never appears in the <c>Set-Cookie</c> it writes.
/// </summary>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class InviteTokenStagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Staging_Answers_204_With_The_Q1_Host_Cookie()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();
        var generated = InviteTokenFormat.Generate();

        using var response = await LoginTestHost.SendAsync(client, Stage(generated.Full));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LoginTestHost.BodyAsync(response)).ShouldBeNullOrWhiteSpace("the answer carries no body");
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        var cookies = LoginTestHost.InviteCookies(response);
        cookies.Count.ShouldBe(1);

        var header = cookies[0];
        header.ShouldStartWith(InviteTokenCookie.Name + "=");
        header.ShouldContain("secure", Case.Insensitive);
        header.ShouldContain("httponly", Case.Insensitive);
        header.ShouldContain("samesite=lax", Case.Insensitive);
        header.ShouldContain("path=/", Case.Insensitive);
        header.ShouldContain("max-age=600", Case.Insensitive);
        header.ShouldNotContain("domain=", Case.Insensitive);

        // The secret is not readable: neither the whole token nor its secret half appears in what the browser
        // is handed, and the value is not the token wrapped in quotes either.
        header.ShouldNotContain(generated.Full, Case.Sensitive);
        header.ShouldNotContain(generated.Secret, Case.Sensitive);
        header.ShouldNotContain(Uri.EscapeDataString(generated.Full), Case.Sensitive);
    }

    [Fact]
    public void The_Staged_Lifetime_Is_At_Most_Ten_Minutes()
    {
        // Q1 bounds the window, and the bound is a constant rather than a configuration key on purpose: no
        // deployment may widen it.
        InviteTokenCookie.LifetimeSeconds.ShouldBeLessThanOrEqualTo(600);
        InviteTokenCookie.Name.ShouldBe("__Host-evidgate_invite");
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("evgi_tooshort_abcdefghij")]
    [InlineData("evii_ABCDEFGHIJKL_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLM")]
    [InlineData("evgi_ABCDEFGHIJKL_short")]
    public async Task A_Malformed_Token_Is_422_And_Stages_Nothing(string token)
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, Stage(token));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.RequestInvalid);
        (await ErrorPathsAsync(response)).ShouldBe(["invite_token"], "the contract names the field to fix");
        LoginTestHost.InviteCookies(response).ShouldBeEmpty("a refused token must leave no cookie behind");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await LoginTestHost.BodyAsync(response)).ShouldNotContain(token, Case.Sensitive);
    }

    /// <summary>
    /// The <c>details[].path</c> each refused body must name (contracts/openapi.yaml,
    /// <c>POST /auth/invite-token</c>): the FIELD for a body whose <c>invite_token</c> is unusable, the BODY
    /// itself when it is not JSON, not a JSON object, or not <c>application/json</c>.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="path">The <c>details[].path</c> the refusal must carry.</param>
    [Theory]
    [InlineData("{\"invite_token\": 42}", "invite_token")]
    [InlineData("{\"invite_token\": true}", "invite_token")]
    [InlineData("{\"invite_token\": {\"token\": \"evgi_ABCDEFGHIJKL_secret\"}}", "invite_token")]
    [InlineData("{\"invite_token\": [\"evgi_ABCDEFGHIJKL_secret\"]}", "invite_token")]
    [InlineData("{\"invite_token\": null}", "invite_token")]
    [InlineData("{}", "invite_token")]
    [InlineData("this is not json", "body")]
    [InlineData("[]", "body")]
    [InlineData("", "body")]
    public async Task A_Body_Without_A_Usable_Token_Is_422_And_Names_The_Field_Or_The_Body(string body, string path)
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, StageRaw(body));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.RequestInvalid);
        (await ErrorPathsAsync(response)).ShouldBe([path]);
        LoginTestHost.InviteCookies(response).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Form_Encoded_Body_Is_Refused_So_A_Cross_Site_Form_Cannot_Stage_A_Token()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();
        var staged = InviteTokenFormat.Generate().Full;

        // The cross-site vector: an `enctype=text/plain` form post needs no CORS preflight and the browser —
        // not the page — chooses the Content-Type, so only a media-type guard keeps an attacker's token out of
        // a victim's cookie jar (SameSite=Lax governs what the browser sends, not what a response may set).
        using var response = await LoginTestHost.SendAsync(
            client,
            new HttpRequestMessage(HttpMethod.Post, "/v1/auth/invite-token")
            {
                Content = new StringContent("{\"invite_token\":\"" + staged + "\"}", Encoding.UTF8, "text/plain"),
            });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.RequestInvalid);
        (await ErrorPathsAsync(response)).ShouldBe(["body"], "the media type is the body's own fault");
        LoginTestHost.InviteCookies(response).ShouldBeEmpty("a form post must never stage a token");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task An_Oversized_Body_Is_413_And_Stages_Nothing()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();
        var oversized = new string('a', 5000);

        using var response = await LoginTestHost.SendAsync(client, StageRaw("{\"invite_token\": \"" + oversized + "\"}"));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.PayloadTooLarge);
        LoginTestHost.InviteCookies(response).ShouldBeEmpty();
    }

    [Fact]
    public async Task Staging_Answers_429_With_Retry_After_Once_The_Clients_Budget_Is_Spent()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();
        var burst = host.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.InviteLimit;

        for (var attempt = 1; attempt <= burst; attempt++)
        {
            using var allowed = await LoginTestHost.SendAsync(client, Stage(InviteTokenFormat.Generate().Full));
            allowed.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                $"staging {attempt} of the {burst} the window allows");
        }

        using var refused = await LoginTestHost.SendAsync(client, Stage(InviteTokenFormat.Generate().Full));

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await LoginTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.RateLimited);
        refused.Headers.RetryAfter.ShouldNotBeNull();
        refused.Headers.CacheControl!.NoStore.ShouldBeTrue();
        LoginTestHost.InviteCookies(refused).ShouldBeEmpty("a throttled staging writes no cookie");
    }

    [Fact]
    public async Task Each_Staging_Replaces_The_Token_The_Browser_Carries()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var first = await LoginTestHost.SendAsync(client, Stage(InviteTokenFormat.Generate().Full));
        using var second = await LoginTestHost.SendAsync(client, Stage(InviteTokenFormat.Generate().Full));

        var firstCookie = LoginTestHost.InviteCookies(first).Single();
        var secondCookie = LoginTestHost.InviteCookies(second).Single();

        // A browser replaces a cookie by its (name, path, domain) triple, so the replacement is only
        // guaranteed while every staging writes that same shape: no Domain (which `__Host-` forbids) and
        // the root path. A staging that changed either would leave the previous token alive beside it.
        foreach (var cookie in new[] { firstCookie, secondCookie })
        {
            cookie.ShouldContain("path=/", Case.Insensitive);
            cookie.ShouldNotContain("domain=", Case.Insensitive);
        }

        var firstValue = firstCookie.Split(';', 2)[0];
        var secondValue = secondCookie.Split(';', 2)[0];

        secondValue.ShouldNotBe(firstValue, "a new staging must not reuse the previous cookie value");
        secondCookie.ShouldNotContain("expires=Thu, 01 Jan 1970", Case.Insensitive);
    }

    /// <summary>A staging request carrying <paramref name="token"/> in the documented JSON body.</summary>
    private static HttpRequestMessage Stage(string token) =>
        StageRaw(JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal) { ["invite_token"] = token }));

    /// <summary>A staging request with a raw body, including the malformed ones.</summary>
    private static HttpRequestMessage StageRaw(string body) =>
        new(HttpMethod.Post, "/v1/auth/invite-token")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// The <c>error.details[].path</c> values of a response body, in order — what tells a client which member
    /// of the request to fix (00 §9.2).
    /// </summary>
    /// <param name="response">The refused response.</param>
    private static async Task<List<string?>> ErrorPathsAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await LoginTestHost.BodyAsync(response);
        using var document = JsonDocument.Parse(body);

        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.Array
            ? [.. details.EnumerateArray().Select(detail =>
                detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("path", out var path)
                    ? path.GetString()
                    : null)]
            : [];
    }
}
