using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Options;
using Identity.IntegrationTests.Forwarding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1-T1.1 (F1/M1/M2): the per-client-address budget both login routes spend before any other work —
/// the 00 §9.2 429 <c>rate_limited</c> envelope with <c>Retry-After</c>, the stricter budget of the route
/// that calls the provider, and the fact that another client address keeps its own window — plus the
/// redirect sink guard of F4/CR5.
/// </summary>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class LoginThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_Answers_429_With_Retry_After_Once_The_Clients_Budget_Is_Spent()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();
        var burst = Burst(host);

        for (var attempt = 1; attempt <= burst; attempt++)
        {
            using var allowed = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
            allowed.StatusCode.ShouldBe(HttpStatusCode.Found, $"start {attempt} of the {burst} the window allows");
        }

        using var refused = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await LoginTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.RateLimited);
        refused.Headers.RetryAfter.ShouldNotBeNull();
        refused.Headers.CacheControl.ShouldNotBeNull();
        refused.Headers.CacheControl!.NoStore.ShouldBeTrue();

        // Refused before the cookie is written: a throttled attempt leaves no pending login behind.
        LoginTestHost.StateCookies(refused).ShouldBeEmpty();
    }

    [Fact]
    public async Task Callback_Spends_Two_Units_So_It_Is_Stricter_Than_Start()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();
        var callbackBudget = Burst(host) / 2;

        // Without a cookie every served callback is the uniform 401; its two units still come off the window.
        for (var attempt = 1; attempt <= callbackBudget; attempt++)
        {
            using var allowed = await LoginTestHost.SendAsync(
                client,
                LoginTestHost.CallbackRequest("fake", cookieValue: null, state: "state-1"));

            allowed.StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                $"callback {attempt} of the {callbackBudget} the window allows");
        }

        using var refused = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue: null, state: "state-1"));

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await LoginTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.RateLimited);
        refused.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Exhausting_One_Route_Leaves_The_Other_Routes_Budget()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();
        var burst = Burst(host);

        for (var attempt = 1; attempt <= burst + 1; attempt++)
        {
            using var response = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));

            response.StatusCode.ShouldBe(
                attempt <= burst ? HttpStatusCode.Found : HttpStatusCode.TooManyRequests,
                $"start {attempt} of a {burst}-attempt window");
        }

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue: null, state: "state-1"));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Another_Client_Address_Keeps_Its_Own_Budget()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));

        // The per-address window cannot be proven through LoginTestHost's client (TestServer leaves the
        // client address null, so every request of one host shares one key), so this variant of the same
        // host only sets the address from a header — no second login configuration.
        using var remote = host.WithWebHostBuilder(builder =>
            ForwardedHeadersTestHost.Configure(builder, proxies: "none"));
        using var client = ForwardedHeadersTestHost.CreateClient(remote);
        var burst = Burst(remote);

        for (var attempt = 1; attempt <= burst; attempt++)
        {
            using var allowed = await LoginTestHost.SendAsync(client, Start("198.51.100.7"));
            allowed.StatusCode.ShouldBe(HttpStatusCode.Found);
        }

        using var refused = await LoginTestHost.SendAsync(client, Start("198.51.100.7"));
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // The window is the client's, not the process': another address is untouched by that flood.
        using var other = await LoginTestHost.SendAsync(client, Start("198.51.100.8"));
        other.StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    [Fact]
    public async Task An_Off_App_Completion_Target_Redirects_To_The_Root()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        var completion = new FakeLoginCompletion { Target = "https://evil.example/steal?session=1" };
        using var host = LoginTestHost.Start(new MutableClock(Now), completion, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        callback.Headers.Location!.ToString().ShouldBe(ReturnUrlPolicy.Root);
        (await LoginTestHost.BodyAsync(callback)).ShouldNotContain("evil.example", Case.Insensitive);
    }

    [Fact]
    public async Task An_App_Path_Completion_Target_Is_Redirected_To_Unchanged()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        var completion = new FakeLoginCompletion { Target = "/runs/42?tab=x" };
        using var host = LoginTestHost.Start(new MutableClock(Now), completion, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        callback.Headers.Location!.ToString().ShouldBe("/runs/42?tab=x");
    }

    [Fact]
    public async Task A_Start_Whose_Authorization_Url_Cannot_Be_Built_Writes_No_Cookie()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new UnbuildableProvider());
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));

        // An unusable provider is an outage, not a bad request, and the failed attempt leaves nothing at
        // all in the browser: the cookie is written only after the URL exists (02.3.1-T1 nit, CR6).
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unavailable);
        LoginTestHost.StateCookies(response).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_One_Unit_Budget_Never_Refuses_The_First_Callback()
    {
        using var configured = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var host = HostWithBudget(configured, burst: 1);
        using var client = host.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        start.StatusCode.ShouldBe(HttpStatusCode.Found);
        var pair = LoginTestHost.StateCookieValue(start)
            ?? throw new InvalidOperationException("the start response set no pending-login cookie");
        var cookieValue = pair[(LoginStateCookie.Name.Length + 1)..];
        var state = StateOf(start);

        // The callback's second unit is best-effort: a legal login limit of 1 must still let the
        // first login through to the provider, not answer every callback with 429.
        using var first = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, state));

        first.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(first)).ShouldBe(ApiErrorCode.TokenInvalid);

        // The first unit of the next callback is what decides: the window holds one, so this one is refused.
        using var second = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, state));

        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await LoginTestHost.ErrorCodeAsync(second)).ShouldBe(ApiErrorCode.RateLimited);
        second.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_Refused_Callback_Still_Deletes_The_Cookie_And_Sets_No_Store()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();
        var callbackBudget = Burst(host) / 2;

        for (var attempt = 1; attempt <= callbackBudget; attempt++)
        {
            using var allowed = await LoginTestHost.SendAsync(
                client,
                LoginTestHost.CallbackRequest("fake", cookieValue: null, state: "state-1"));

            allowed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var refused = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue: null, state: "state-1"));

        // The throttle answers after the deletion, so "the cookie is deleted on every outcome" holds for
        // the 429 too, and the refusal is never cacheable.
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        LoginTestHost.StateCookieDeletion(refused).ShouldNotBeNull();
        refused.Headers.CacheControl.ShouldNotBeNull();
        refused.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    /// <summary>The same login host with a different login budget bound at boot.</summary>
    private static WebApplicationFactory<Program> HostWithBudget(WebApplicationFactory<Program> host, int burst) =>
        host.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Identity:RateLimits:LoginLimit"] = burst.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })));

    /// <summary>The <c>state</c> the start redirect carries (base64url, so it needs no decoding).</summary>
    private static string StateOf(HttpResponseMessage start)
    {
        var location = start.Headers.Location
            ?? throw new InvalidOperationException("the start response carried no Location header");

        return location.Query
            .TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == "state")
            .Select(pair => pair[1])
            .First();
    }

    /// <summary>The request header the address-setting filter reads the client address from.</summary>
    private const string ClientIpHeader = ForwardedHeadersTestHost.SocketAddressHeader;

    /// <summary>The burst the login routes actually spend: the login surface's own budget, bound at boot.</summary>
    private static int Burst(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.LoginLimit;

    /// <summary>A <c>start</c> request that reaches the app from <paramref name="address"/>.</summary>
    private static HttpRequestMessage Start(string address)
    {
        var request = LoginTestHost.StartRequest("fake");
        request.Headers.Add(ClientIpHeader, address);

        return request;
    }

    /// <summary>
    /// A configured provider whose own settings cannot build an authorization URL (a deployment
    /// misconfiguration the start route turns into 503 <c>unavailable</c>).
    /// </summary>
    private sealed class UnbuildableProvider : ILoginProvider
    {
        /// <inheritdoc />
        public string Name => "fake";

        /// <inheritdoc />
        public bool IsConfigured => true;

        /// <inheritdoc />
        public Uri BuildAuthorizationUrl(LoginAuthorizationRequest request) =>
            throw new ArgumentException("this provider's configuration builds no authorization URL", nameof(request));

        /// <inheritdoc />
        public Task<LoginProviderResult> CompleteAsync(LoginCallbackRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("a start that cannot build its URL never reaches the completion");
    }
}
