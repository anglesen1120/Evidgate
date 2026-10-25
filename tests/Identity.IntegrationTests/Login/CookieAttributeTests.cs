using System.Net;
using Evidgate.Modules.Identity.Login;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1 §8 / T38 / 02.3.1 §9 (<c>CookieAttributeTests</c>): the pending-login cookie is a
/// <c>__Host-</c> cookie — <c>Secure</c>, <c>Path=/</c>, no <c>Domain</c> — plus <c>HttpOnly</c>,
/// <c>SameSite=Lax</c> and a bounded <c>Max-Age</c>, its value is unreadable, both routes are
/// <c>no-store</c>, and every callback outcome deletes it.
/// </summary>
public sealed class CookieAttributeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_Writes_Exactly_One_Host_Cookie_With_The_T38_Attributes()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var cookies = LoginTestHost.StateCookies(response);
        cookies.Count.ShouldBe(1);

        var header = cookies[0];
        header.ShouldStartWith(LoginStateCookie.Name + "=");
        header.ShouldContain("secure", Case.Insensitive);
        header.ShouldContain("httponly", Case.Insensitive);
        header.ShouldContain("samesite=lax", Case.Insensitive);
        header.ShouldContain("path=/", Case.Insensitive);
        header.ShouldContain("max-age=600", Case.Insensitive);
        header.ShouldNotContain("domain=", Case.Insensitive);
    }

    [Fact]
    public async Task The_Cookie_Value_Carries_No_Field_In_The_Clear()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake", "/runs/42"));

        var (payload, value) = host.PendingLogin(response);

        // The value is ciphertext: no field of the payload survives in it, and no field name either.
        value.ShouldNotContain(payload.State);
        value.ShouldNotContain(payload.Nonce);
        value.ShouldNotContain(payload.Verifier);
        value.ShouldNotContain("ReturnUrl");
        value.ShouldNotContain("Verifier");
    }

    [Fact]
    public async Task Both_Routes_Answer_No_Store()
    {
        using var host = LoginTestHost.Start(new MutableClock(Now), new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        start.Headers.CacheControl.ShouldNotBeNull();
        start.Headers.CacheControl!.NoStore.ShouldBeTrue();

        using var callback = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue: null, state: "any-state"));
        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        callback.Headers.CacheControl.ShouldNotBeNull();
        callback.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Every_Callback_Outcome_Deletes_The_Cookie_And_Sets_No_Other()
    {
        var provider = new FakeLoginProvider("fake")
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity("fake")),
        };
        using var host = LoginTestHost.Start(new MutableClock(Now), provider);
        using var client = host.CreateLoginClient();

        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest("fake"));
        var (payload, cookieValue) = host.PendingLogin(start);

        var outcomes = new List<(string Label, HttpRequestMessage Request)>
        {
            ("unknown provider", LoginTestHost.CallbackRequest("saml", cookieValue, payload.State)),
            ("missing cookie", LoginTestHost.CallbackRequest("fake", cookieValue: null, payload.State)),
            ("wrong state", LoginTestHost.CallbackRequest("fake", cookieValue, "another-state")),
            ("uncompleted login", LoginTestHost.CallbackRequest("fake", cookieValue, payload.State)),
        };

        foreach (var (label, request) in outcomes)
        {
            using (request)
            {
                using var response = await LoginTestHost.SendAsync(client, request);

                LoginTestHost.StateCookieDeletion(response).ShouldNotBeNull(
                    label + ": the callback must always emit the cookie deletion");
                LoginTestHost.StateCookies(response).Count.ShouldBe(
                    1,
                    label + ": only the deletion may be set");
            }
        }
    }
}
