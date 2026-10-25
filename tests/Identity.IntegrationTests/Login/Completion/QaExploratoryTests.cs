using System.Net;
using System.Text;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// QA exploratory cases for 02.3.1-T1.3/T1.4 (PR #164), beyond the plan's §9 matrix: a first-login race, the
/// cookie's storage form, an oversized user agent, and per-session HMACs.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class QaExploratoryTests : CatalogueTestBase
{
    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container, migrated database and reset seam.</param>
    public QaExploratoryTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Eight concurrent first sign-ins of one unknown subject all answer 403 <c>no_membership</c> and leave
    /// exactly one <c>user</c> row and one <c>user_identity</c> row (the upsert is race-free).
    /// </summary>
    [Fact]
    public async Task Concurrent_First_Logins_Of_One_Subject_Create_One_User_And_One_Identity()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
            {
                using var client = host.CreateLoginClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(LoginCompletionTestHost.UserAgent);
                return await host.SignInAsync(client);
            }));

        try
        {
            foreach (var response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "no membership yet, so every first login is refused");
                (await LoginCompletionTestHost.ErrorCodeAsync(response)).ShouldBe("no_membership");
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.\"user\""))
            .ShouldBe(1, "one subject, one user row, however many first logins race");
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.user_identity"))
            .ShouldBe(1, "one (provider, subject) binding, however many first logins race");
    }

    /// <summary>
    /// The session cookie value appears only in <c>Set-Cookie</c>: not in the 302 body, not in <c>Location</c>,
    /// and the stored <c>session.cookie_hmac</c> is the HMAC of that value, never the value itself.
    /// </summary>
    [Fact]
    public async Task Session_Cookie_Value_Is_Never_Echoed_And_Is_Stored_Only_As_An_Hmac()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(LoginCompletionTestHost.UserAgent);

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        using var completed = await host.SignInAsync(client, returnUrl: "/runs/42");

        completed.StatusCode.ShouldBe(HttpStatusCode.Found);
        var cookieValue = LoginCompletionTestHost.SessionCookieValue(completed);
        cookieValue.ShouldNotBeNullOrEmpty();

        (await LoginCompletionTestHost.BodyAsync(completed)).ShouldNotContain(cookieValue!);
        completed.Headers.Location!.OriginalString.ShouldNotContain(cookieValue!);

        (await LoginCompletionTestHost.OwnerScalarAsync<int>(
                Fixture,
                "select count(*)::int from public.session where cookie_hmac = @h",
                [("h", host.CookieHmac(cookieValue!))]))
            .ShouldBe(1, "the stored value is the HMAC of the cookie value under the configured pepper");

        (await LoginCompletionTestHost.OwnerScalarAsync<int>(
                Fixture,
                "select count(*)::int from public.session where cookie_hmac = convert_to(@v, 'UTF8')",
                [("v", cookieValue!)]))
            .ShouldBe(0, "the raw cookie value must never be stored");
    }

    /// <summary>
    /// A 2000-character <c>User-Agent</c> does not reject the login: the callback still answers 302 and a
    /// session cookie is issued.
    /// </summary>
    [Fact]
    public async Task Oversized_User_Agent_Does_Not_Reject_The_Login()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(new string('a', 2000));

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        using var completed = await host.SignInAsync(client);

        completed.StatusCode.ShouldBe(HttpStatusCode.Found, "an oversized user agent is not a reason to refuse");
        LoginCompletionTestHost.SessionCookieValue(completed).ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// Two logins of one member store two distinct HMACs, one per cookie value, so a cookie cannot be replayed
    /// against another session's row.
    /// </summary>
    [Fact]
    public async Task Each_Login_Session_Stores_The_Hmac_Of_Its_Own_Cookie()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(LoginCompletionTestHost.UserAgent);

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        using var first = await host.SignInAsync(client);
        using var second = await host.SignInAsync(client);
        var firstCookie = LoginCompletionTestHost.SessionCookieValue(first);
        var secondCookie = LoginCompletionTestHost.SessionCookieValue(second);
        firstCookie.ShouldNotBeNullOrEmpty();
        secondCookie.ShouldNotBeNullOrEmpty();
        firstCookie.ShouldNotBe(secondCookie);

        var firstHmac = host.CookieHmac(firstCookie!);
        var secondHmac = host.CookieHmac(secondCookie!);
        firstHmac.ShouldNotBe(secondHmac);

        (await LoginCompletionTestHost.OwnerScalarAsync<int>(
                Fixture,
                "select count(*)::int from public.session where cookie_hmac = @h",
                [("h", firstHmac)]))
            .ShouldBe(1, "the first login's row carries the HMAC of its own cookie");
        (await LoginCompletionTestHost.OwnerScalarAsync<int>(
                Fixture,
                "select count(*)::int from public.session where cookie_hmac = @h",
                [("h", secondHmac)]))
            .ShouldBe(1, "the second login's row carries the HMAC of its own cookie");
    }
}
