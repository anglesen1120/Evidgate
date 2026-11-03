using System.Globalization;
using System.Net;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// AC1 of 02.3.2 over the wire: the cookie the real sign-in writes (02.3.1's
/// <c>oidc/{provider}/callback</c> → <c>LoginCommandCompletion</c> → <c>SessionCookieWriter</c>) is a
/// <c>__Host-</c> cookie with exactly the attribute set a browser enforces for that prefix
/// (<c>Secure</c>, <c>Path=/</c>, no <c>Domain</c>), plus <c>HttpOnly</c> and <c>SameSite=Lax</c>; its value is
/// ≥128 bits of opaque randomness that exists in exactly one place — the browser — because the row keeps only
/// its peppered HMAC (T5).
/// </summary>
/// <remarks>
/// <para>
/// Nothing is read off the code that writes the cookie: every assertion parses the response's
/// <c>Set-Cookie</c> header, which is the only thing a browser sees. A writer that forgot <c>Secure</c> or added a
/// <c>Domain</c> would still pass a unit test of its own options object; it cannot pass this one.
/// </para>
/// <para>
/// The antiforgery pair is asserted here too (its cookies are written by the same issuance path, D6): the pair
/// must outlive the request that accepted the session and must be readable by a page script only in its
/// <c>XSRF-TOKEN</c> half.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class CookieAttributeTests : TenantTest
{
    /// <summary>Throttle of this file: none, the lifetimes are the host's defaults; its own database, because
    /// one case counts every session row.</summary>
    public CookieAttributeTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// AC1: name, <c>__Host-</c> attributes, <c>HttpOnly</c>, <c>SameSite=Lax</c>, a <c>Path=/</c>, no
    /// <c>Domain</c>, a <c>Max-Age</c>/<c>Expires</c> pair at the ABSOLUTE lifetime, and a value of 256 opaque bits.
    /// </summary>
    [Fact]
    public async Task Sign_In_Writes_A_Host_Prefixed_Session_Cookie_With_The_Exact_Attribute_Set()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        var cookie = await SignInCookieAsync(host, client);

        cookie.Name.ShouldBe(SessionTestHost.SessionCookieName);
        cookie.Name.ShouldStartWith("__Host-", Case.Sensitive, "the prefix is what makes the browser enforce the rest");

        // The three rules of the __Host- prefix: Secure, Path=/, and no Domain.
        cookie.Has("secure").ShouldBeTrue("__Host- refuses a cookie that is not Secure");
        cookie.Attribute("path").ShouldBe("/", "a __Host- cookie is served from the whole origin and nothing else");
        cookie.Has("domain").ShouldBeFalse("__Host- refuses ANY Domain attribute");

        cookie.Has("httponly").ShouldBeTrue("no script may read the session credential");
        cookie.Attribute("samesite").ShouldBe("lax", StringCompareShould.IgnoreCase);

        // D1: the browser deadline is the session's ABSOLUTE lifetime — the row, not the cookie, is what slides.
        cookie.Attribute("max-age")
            .ShouldBe(host.AbsoluteSeconds.ToString(CultureInfo.InvariantCulture));
        var expires = DateTimeOffset.Parse(
            cookie.Attribute("expires").ShouldNotBeNull(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        expires.ShouldBe(host.Clock.UtcNow.AddSeconds(host.AbsoluteSeconds), TimeSpan.FromSeconds(1));

        // AC1: at least 128 bits, opaque — the value is 32 random bytes in base64url, nothing derived from a row.
        cookie.Value.Length.ShouldBe(43, "32 bytes in unpadded base64url");
        cookie.Value.ShouldNotContain("=");
        Convert.FromBase64String(Pad(cookie.Value)).Length.ShouldBeGreaterThanOrEqualTo(16);
    }

    /// <summary>
    /// AC1 (second clause): the row keeps <c>HMAC-SHA256(pepper, value)</c> and the pepper's version — never the
    /// value. Read through the owner, the only role allowed to look at <c>public.session</c> (0041).
    /// </summary>
    [Fact]
    public async Task The_Cookie_Value_Is_Stored_Only_As_A_Peppered_Hmac()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        var cookie = await SignInCookieAsync(host, client);
        var sessionId = await host.SessionIdOfAsync(cookie.Value);

        var row = await host.SessionRowAsync(sessionId);
        row.RevokedAt.ShouldBeNull("a sign-in mints a live session");

        var stored = await host.ScalarAsync(
            "select row_to_json(s)::text from public.session s where id = @id",
            [("id", sessionId)]);

        ((string)stored!).ShouldNotContain(
            cookie.Value,
            Case.Sensitive,
            "the raw cookie value must never reach the database");

        var hmac = (byte[])(await host.ScalarAsync(
            "select cookie_hmac from public.session where id = @id",
            [("id", sessionId)]))!;
        hmac.ShouldBe(host.HmacOf(cookie.Value), "the row stores exactly the HMAC the pepper produces");

        (await host.ScalarAsync(
            "select pepper_version from public.session where id = @id",
            [("id", sessionId)]))
            .ShouldBe(SessionTestHost.PepperVersion, "the row records the pepper that minted its HMAC");
    }

    /// <summary>
    /// AC1 boundary: two sign-ins never mint the same value — the credential is random per session, so a leaked
    /// value names exactly one row and a replayed one cannot land on another account.
    /// </summary>
    [Fact]
    public async Task Two_Sign_Ins_Mint_Different_Opaque_Values()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        var first = await SignInCookieAsync(host, client);
        var second = await SignInCookieAsync(host, client);

        second.Value.ShouldNotBe(first.Value);

        // Both decode to 32 bytes: the length is the contract's, not a coincidence of one value.
        Convert.FromBase64String(Pad(first.Value)).Length.ShouldBe(32);
        Convert.FromBase64String(Pad(second.Value)).Length.ShouldBe(32);

        (await host.CountAsync("select count(*) from public.session")).ShouldBe(2L);
    }

    /// <summary>
    /// D6: the antiforgery pair issued with the session cookie is the pair the contract names — the framework's
    /// <c>HttpOnly</c> cookie-token under a <c>__Host-</c> name, and the script-readable <c>XSRF-TOKEN</c> request
    /// token, both <c>Secure</c> and both lasting as long as the session they belong to.
    /// </summary>
    [Fact]
    public async Task The_Xsrf_Pair_Is_Issued_With_The_Session_Cookie_And_Carries_Its_Own_Attribute_Set()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        using var response = await SignInAsync(host, client);

        var xsrf = ParsedCookie.Parse(
            SessionTestHost.Cookies(response, SessionTestHost.XsrfCookieName).ShouldHaveSingleItem());
        xsrf.Name.ShouldBe(SessionTestHost.XsrfCookieName);
        xsrf.Has("httponly").ShouldBeFalse("a page script must read this half and echo it in the header");
        xsrf.Has("secure").ShouldBeTrue();
        xsrf.Attribute("path").ShouldBe("/");
        xsrf.Attribute("samesite").ShouldBe("lax", StringCompareShould.IgnoreCase);
        xsrf.Value.ShouldNotBeNullOrEmpty();

        var antiforgery = ParsedCookie.Parse(
            SessionTestHost.Cookies(response, SessionTestHost.AntiforgeryCookieName).ShouldHaveSingleItem());
        antiforgery.Name.ShouldBe(SessionTestHost.AntiforgeryCookieName);
        antiforgery.Name.ShouldStartWith("__Host-", Case.Sensitive);
        antiforgery.Has("httponly").ShouldBeTrue("nothing in the page reads the cookie-token");
        antiforgery.Has("secure").ShouldBeTrue();
        antiforgery.Has("domain").ShouldBeFalse();
        antiforgery.Attribute("path").ShouldBe("/");
        antiforgery.Attribute("samesite").ShouldBe("strict", StringCompareShould.IgnoreCase);

        // The pair dies with the session, not with the request: both halves carry the absolute lifetime (D6), so
        // a browser reopened inside the window is never left authenticating by cookie and failing every mutation.
        var maxAge = host.AbsoluteSeconds.ToString(CultureInfo.InvariantCulture);
        xsrf.Attribute("max-age").ShouldBe(maxAge);
        antiforgery.Attribute("max-age").ShouldBe(maxAge);
    }

    /// <summary>
    /// D1 boundary: the browser deadline is the ABSOLUTE lifetime, never the (shorter) idle one — a cookie that
    /// expired with the idle window would sign an active user out mid-session, while a cookie that outlived the
    /// row only costs one 401 <c>session_expired</c>.
    /// </summary>
    [Fact]
    public async Task Boundary_Max_Age_Is_The_Absolute_Lifetime_And_Not_The_Idle_One()
    {
        using var host = SessionTestHost.Start(Fixture, idleSeconds: 300, absoluteSeconds: 1800);
        using var client = host.CreateSessionClient();

        var cookie = await SignInCookieAsync(host, client);

        cookie.Attribute("max-age").ShouldBe("1800");
        cookie.Attribute("max-age").ShouldNotBe("300", "the idle lifetime is the ROW's deadline, not the browser's");
    }

    /// <summary>
    /// The request throws when it answers anything but the completion redirect: an arrangement that silently
    /// became a 403 <c>no_membership</c> would make every later assertion about a cookie meaningless.
    /// </summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    private static async Task<HttpResponseMessage> SignInAsync(SessionTestHost host, HttpClient client)
    {
        await host.SeedMemberAsync();

        var response = await host.SignInAsync(client);

        response.StatusCode.ShouldBe(HttpStatusCode.Found, "the sign-in completes with the 302");
        SessionTestHost.SessionCookieValue(response).ShouldNotBeNull("the sign-in issues the session cookie");

        return response;
    }

    /// <summary>The session cookie of one sign-in, parsed from the response's own <c>Set-Cookie</c>.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    private static async Task<ParsedCookie> SignInCookieAsync(SessionTestHost host, HttpClient client)
    {
        using var response = await SignInAsync(host, client);

        return ParsedCookie.Parse(
            SessionTestHost.Cookies(response, SessionTestHost.SessionCookieName).ShouldHaveSingleItem());
    }

    /// <summary>Restores the base64 padding a base64url cookie value drops.</summary>
    /// <param name="value">The unpadded value.</param>
    private static string Pad(string value) =>
        (value.Replace('-', '+').Replace('_', '/') + "===")[..(value.Length + ((4 - (value.Length % 4)) % 4))];

    /// <summary>
    /// One <c>Set-Cookie</c> header split into the shape a browser reads it in: the name, the value and the
    /// attributes (lower-cased keys, so <c>SameSite</c> and <c>samesite</c> are the same attribute).
    /// </summary>
    /// <param name="Name">The cookie name.</param>
    /// <param name="Value">The cookie value.</param>
    /// <param name="Attributes">Its attributes; a valueless attribute maps to null.</param>
    private sealed record ParsedCookie(string Name, string Value, IReadOnlyDictionary<string, string?> Attributes)
    {
        /// <summary>Parses one <c>Set-Cookie</c> header value.</summary>
        /// <param name="header">The raw header.</param>
        internal static ParsedCookie Parse(string header)
        {
            ArgumentException.ThrowIfNullOrEmpty(header);

            var segments = header.Split(';', StringSplitOptions.TrimEntries);
            var pair = segments[0].Split('=', 2);

            var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in segments.Skip(1))
            {
                var separator = segment.IndexOf('=', StringComparison.Ordinal);
                if (separator < 0)
                {
                    attributes[segment] = null;
                }
                else
                {
                    attributes[segment[..separator]] = segment[(separator + 1)..];
                }
            }

            return new ParsedCookie(pair[0], pair[1], attributes);
        }

        /// <summary>Whether the cookie carries <paramref name="attribute"/> at all.</summary>
        /// <param name="attribute">The attribute name.</param>
        internal bool Has(string attribute) => Attributes.ContainsKey(attribute);

        /// <summary>The attribute's value (null for a valueless attribute OR an absent one — check <see cref="Has"/> first).</summary>
        /// <param name="attribute">The attribute name.</param>
        internal string? Attribute(string attribute) =>
            Attributes.TryGetValue(attribute, out var value) ? value : null;
    }
}
