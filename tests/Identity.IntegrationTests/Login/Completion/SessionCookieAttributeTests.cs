using System.Net;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// 02.3.1-T1.4 / 00 §8.9 / 00 §9.1 (<c>SessionCookieAttributeTests</c>): the exact attributes of the
/// <c>__Host-evidgate_session</c> cookie a completed login sets.
/// </summary>
/// <remarks>
/// The <c>__Host-</c> prefix is a browser-enforced contract, not a naming style: the browser refuses the
/// cookie unless it carries <c>Secure</c>, <c>Path=/</c> and no <c>Domain</c> — so a regression in any of the
/// three turns the login into a loop that only a real browser would reveal. The assertions therefore read the
/// <c>Set-Cookie</c> header the deployment wrote, attribute by attribute, rather than the
/// <c>CookieOptions</c> a unit test could have invented.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionCookieAttributeTests : CatalogueTestBase
{
    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container and migrated database.</param>
    public SessionCookieAttributeTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>The completed login's cookie carries exactly the <c>__Host-</c> attribute set.</summary>
    [Fact]
    public async Task The_Session_Cookie_Carries_The_Host_Prefix_Attribute_Set()
    {
        var subject = Unique.Text("google-subject");
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider(subject));
        using var client = host.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            subject);

        using var completed = await host.SignInAsync(client);
        completed.StatusCode.ShouldBe(HttpStatusCode.Found);

        var cookie = LoginCompletionTestHost.SessionCookies(completed).ShouldHaveSingleItem();

        cookie.ShouldStartWith("__Host-evidgate_session=");
        cookie.ShouldContain("; secure", Case.Insensitive);
        cookie.ShouldContain("; httponly", Case.Insensitive);
        cookie.ShouldContain("; samesite=lax", Case.Insensitive);
        cookie.ShouldContain("; path=/", Case.Insensitive);
        cookie.ShouldContain("; expires=", Case.Insensitive);
        cookie.ShouldNotContain("domain=", Case.Insensitive);

        var value = LoginCompletionTestHost.SessionCookieValue(completed)!;
        value.ShouldNotBeNullOrWhiteSpace();
        value.ShouldNotContain("=", Case.Sensitive, "base64url without padding, so no '=' survives");
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .ShouldBeTrue("the value is base64url of the 32 random bytes the store generated");
    }
}
