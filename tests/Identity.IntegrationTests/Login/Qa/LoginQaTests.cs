using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Qa;

/// <summary>
/// QA pass over 02.3.1-T1.1/T1.2 (negative, boundary and exploratory probes through the real routes).
/// Not part of the author's suite: each probe proves a property the author's tests do not reach.
/// </summary>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class LoginQaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/%2f%2fevil.example")]
    [InlineData("/%252f%252fevil.example")]
    [InlineData("/%5cevil.example")]
    [InlineData("/%09evil")]
    [InlineData("/%0aevil")]
    [InlineData("/%0devil")]
    [InlineData("/%20evil")]
    [InlineData("/%c2%a0evil")]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    public async Task Qa_Off_App_Or_Smuggled_ReturnUrl_Resolves_To_The_Root(string returnUrl)
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake", returnUrl));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        host.PendingLogin(response).Payload.ReturnUrl.ShouldBe(ReturnUrlPolicy.Root);
    }

    [Fact]
    public async Task Qa_Encoded_Plain_App_Path_Is_Kept_After_One_Decode()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake", "/runs%2F42"));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        host.PendingLogin(response).Payload.ReturnUrl.ShouldBe("/runs%2F42");
    }

    [Fact]
    public async Task Qa_Returned_ReturnUrl_Reaches_The_Completion_Seam_And_The_Redirect()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion { Target = "/runs/42" };
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(clock, completion, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake", "/runs/42?tab=x"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        callback.Headers.Location?.ToString().ShouldBe("/runs/42");
        completion.Last.ShouldNotBeNull();
        completion.Last!.ReturnUrl.ShouldBe("/runs/42?tab=x");
    }

    [Fact]
    public async Task Qa_Callback_At_Exactly_The_Cookie_Lifetime_Is_Accepted()
    {
        var clock = new MutableClock(Now);
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(clock, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        clock.Advance(TimeSpan.FromSeconds(600));

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        provider.Completions.ShouldBe(1);
    }

    [Fact]
    public async Task Qa_Callback_One_Second_Past_The_Cookie_Lifetime_Is_Refused_Before_The_Provider()
    {
        var clock = new MutableClock(Now);
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(clock, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        clock.Advance(TimeSpan.FromSeconds(601));

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Qa_Cookie_59_Seconds_In_The_Future_Is_Accepted_Within_Skew()
    {
        var clock = new MutableClock(Now);
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(clock, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        clock.Advance(TimeSpan.FromSeconds(-59));

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        provider.Completions.ShouldBe(1);
    }

    [Fact]
    public async Task Qa_Cookie_61_Seconds_In_The_Future_Is_Refused_Beyond_Skew()
    {
        var clock = new MutableClock(Now);
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(clock, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        clock.Advance(TimeSpan.FromSeconds(-61));

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Qa_Code_Of_Exactly_2048_Characters_Is_Accepted()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State, code: new string('c', 2048)));

        callback.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        provider.Completions.ShouldBe(1);
    }

    [Fact]
    public async Task Qa_Empty_Code_Is_Token_Invalid_Without_An_Outbound_Call()
    {
        var provider = new FakeLoginProvider("fake");
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State, code: string.Empty));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Qa_Present_But_Empty_Error_Key_Is_Token_Invalid()
    {
        var provider = new FakeLoginProvider("fake");
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State, error: string.Empty));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Qa_Oversized_State_Is_Token_Invalid_Without_An_Outbound_Call()
    {
        var provider = new FakeLoginProvider("fake");
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (_, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, new string('s', 5000)));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Qa_A_State_Consumed_By_A_Provider_Outage_Stays_Consumed()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Fail(LoginFailure.ProviderUnavailable),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var outage = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));
        outage.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        provider.Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake"));

        using var retry = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        retry.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(retry)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(1);
    }

    [Fact]
    public async Task Qa_Concurrent_Callbacks_On_One_State_Reach_The_Provider_Exactly_Once()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        var inFlight = Enumerable.Range(0, 8)
            .Select(_ => LoginTestHost.SendAsync(
                client,
                LoginTestHost.CallbackRequest("fake", cookieValue, payload.State)))
            .ToArray();

        var responses = await Task.WhenAll(inFlight);
        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.NotImplemented).ShouldBe(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized).ShouldBe(7);
            provider.Completions.ShouldBe(1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Qa_Provider_Failure_Text_Never_Reaches_Any_Header_Or_Body()
    {
        var failures = new (string Label, Func<Exception> Make)[]
        {
            ("task cancelled", () => new TaskCanceledException(FakeLoginProvider.Canary)),
            ("transport", () => new HttpRequestException(FakeLoginProvider.Canary)),
            ("timeout", () => new TimeoutException(FakeLoginProvider.Canary)),
            ("unexpected", () => new InvalidOperationException(FakeLoginProvider.Canary)),
        };

        foreach (var (label, make) in failures)
        {
            var provider = new FakeLoginProvider("fake") { OnComplete = _ => throw make() };
            using var host = LoginTestHost.Start(new MutableClock(Now), provider);
            using var client = host.CreateLoginClient();

            using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
            var (payload, cookieValue) = host.PendingLogin(start);

            using var callback = await LoginTestHost.SendAsync(
                client,
                LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

            var body = await LoginTestHost.BodyAsync(callback);
            var headers = string.Join(
                "\n",
                callback.Headers.Concat(callback.Content.Headers)
                    .Select(header => header.Key + ": " + string.Join(",", header.Value)));

            body.Contains(FakeLoginProvider.Canary, StringComparison.Ordinal).ShouldBeFalse(label);
            headers.Contains(FakeLoginProvider.Canary, StringComparison.Ordinal).ShouldBeFalse(label);
            ((int)callback.StatusCode).ShouldBeGreaterThanOrEqualTo(500, label);
        }
    }

    [Fact]
    public async Task Qa_No_Store_On_Every_Outcome_Of_Both_Routes()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Fail(LoginFailure.ProviderUnavailable),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var startOk = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        using var startUnknown = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("saml"));
        var (payload, cookieValue) = host.PendingLogin(startOk);

        using var callbackUnknown = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("saml", cookieValue, payload.State));
        using var callbackWrongState = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, "another-state"));
        using var callbackOutage = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        var responses = new[] { startOk, startUnknown, callbackUnknown, callbackWrongState, callbackOutage };
        foreach (var response in responses)
        {
            response.Headers.CacheControl.ShouldNotBeNull(response.RequestMessage?.RequestUri?.ToString());
            response.Headers.CacheControl!.NoStore.ShouldBeTrue(response.RequestMessage?.RequestUri?.ToString());
        }
    }

    [Fact]
    public async Task Qa_Unknown_Provider_On_Callback_Is_404_And_Calls_No_Provider()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("saml", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        provider.Completions.ShouldBe(0);
    }
}
