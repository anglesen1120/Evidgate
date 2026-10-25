using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, RemoteIpStartupFilter>()));
        using var client = remote.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
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

    /// <summary>The request header the address-setting filter reads the client address from.</summary>
    private const string ClientIpHeader = "X-Test-Client-Ip";

    /// <summary>The burst the routes actually spend: the CI exchange limiter's own budget, bound at boot.</summary>
    private static int Burst(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IOptions<CiAuthOptions>>().Value.ExchangeBurstLimit;

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

    /// <summary>Sets <c>RemoteIpAddress</c> from <see cref="ClientIpHeader"/> before the app's pipeline.</summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            return app =>
            {
                // Use(Func<RequestDelegate, RequestDelegate>): the client address is in place before
                // routing, so the login routes' limiter reads the address under test.
                app.Use(nextDelegate => context =>
                {
                    if (context.Request.Headers.TryGetValue(ClientIpHeader, out var header)
                        && IPAddress.TryParse(header.ToString(), out var address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    return nextDelegate(context);
                });

                next(app);
            };
        }
    }
}
