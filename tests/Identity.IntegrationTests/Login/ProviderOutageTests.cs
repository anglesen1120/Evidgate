using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1-T1.1 / AC8 / 02.3.1 §9 (<c>ProviderOutageTests</c>): a provider that is down, times out or is not
/// configured answers 503 <c>unavailable</c> with <c>Retry-After</c> and no provider text; a rejected code
/// answers 401 <c>token_invalid</c>. The verified-identity half — the user upsert, the membership check, the
/// session row and the cookie — is <c>Login/Completion/LoginFlowTests</c>', which runs on a real database.
/// </summary>
// The assembly Fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class ProviderOutageTests(DatabaseFixture fixture)
    : CatalogueTestBase(fixture, DatabaseIsolation.Dedicated)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unavailable_Provider_Answers_503_With_Retry_After_And_No_Provider_Text()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Fail(LoginFailure.ProviderUnavailable),
        };
        using var host = LoginTestHost.Start(Fixture, new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        callback.Headers.RetryAfter.ShouldNotBeNull();
        callback.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(30));

        var body = await LoginTestHost.BodyAsync(callback);
        body.ShouldContain(ApiErrorCode.Unavailable);
        body.ShouldNotContain(FakeLoginProvider.Canary);
    }

    [Fact]
    public async Task Provider_Timeout_Answers_503_Not_A_Server_Error()
    {
        var provider = new FakeLoginProvider("fake")
        {
            // What an HttpClient timeout looks like once it escapes the provider's own handling.
            OnComplete = _ => throw new TaskCanceledException(FakeLoginProvider.Canary),
        };
        using var host = LoginTestHost.Start(Fixture, new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        callback.Headers.RetryAfter.ShouldNotBeNull();

        var body = await LoginTestHost.BodyAsync(callback);
        body.ShouldContain(ApiErrorCode.Unavailable);
        body.ShouldNotContain(FakeLoginProvider.Canary);
    }

    [Fact]
    public async Task Provider_Transport_Failure_Answers_503()
    {
        var provider = new FakeLoginProvider("fake")
        {
            OnComplete = _ => throw new HttpRequestException(FakeLoginProvider.Canary),
        };
        using var host = LoginTestHost.Start(Fixture, new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.Unavailable);
        (await LoginTestHost.BodyAsync(callback)).ShouldNotContain(FakeLoginProvider.Canary);
    }

    [Fact]
    public async Task Unconfigured_Provider_Answers_503_On_Both_Routes()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(Fixture, new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        // start, with the provider's client credential missing (02.3.1 §5: 503 unavailable, retryable).
        provider.IsConfigured = false;
        using var unconfiguredStart = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        unconfiguredStart.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        unconfiguredStart.Headers.RetryAfter.ShouldNotBeNull();
        (await LoginTestHost.ErrorCodeAsync(unconfiguredStart)).ShouldBe(ApiErrorCode.Unavailable);

        // callback, with a cookie written while it was configured: it became unusable in between.
        provider.IsConfigured = true;
        using var configuredStart = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(configuredStart);

        provider.IsConfigured = false;
        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));
        callback.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        callback.Headers.RetryAfter.ShouldNotBeNull();
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.Unavailable);
    }

    [Fact]
    public async Task Rejected_Code_Answers_401_Token_Invalid()
    {
        var provider = new FakeLoginProvider("fake") { Result = LoginProviderResult.Fail(LoginFailure.TokenInvalid) };
        using var host = LoginTestHost.Start(Fixture, new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        (await LoginTestHost.BodyAsync(callback)).ShouldNotContain(FakeLoginProvider.Canary);
    }

    [Fact]
    public async Task The_Completion_Seams_Own_Failure_Is_Mapped_As_Itself()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        var completion = new FakeLoginCompletion { Failure = Error.Denied(ApiErrorCode.NoMembership, "no active membership") };
        using var host = LoginTestHost.Start(Fixture, new MutableClock(Now), completion, provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue, payload.State));

        // AC3's shape, proven through the seam that will own it: no membership, no session cookie.
        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.NoMembership);
    }
}
