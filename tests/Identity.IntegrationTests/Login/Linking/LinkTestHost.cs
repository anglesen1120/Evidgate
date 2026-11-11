using System.Net;
using System.Text.Json;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>
/// The host the linking suites drive (02.3.1-T2.2, Q4): the real Api composition
/// (<c>Program</c> → <c>ApiHost</c>) over the harness's migrated database, the login options of a configured
/// deployment, the session pepper of <c>EVIDGATE:SESSION</c>, and exactly the login providers a case passes.
/// </summary>
/// <remarks>
/// <para>
/// One substitution only. The providers (the real ones need live credentials and outbound calls; the provider
/// half is proven by the login suites' own fakes and mocked endpoints), and — in the cases that need it — one
/// service the case replaces through <c>overrides</c> (the link store), so an outcome the database would rarely
/// produce is injected at its seam instead of being simulated by editing rows mid-request. Everything else is
/// the deployment's: the routes, the envelope filter, the session store, the cookie protection and the database.
/// </para>
/// <para>
/// The connection string is the harness's APP role (00 §5.4.3), so the linking path runs with the privileges
/// it has in production; assertions about stored rows read them back through the owner connection, because
/// the app role holds no privilege on the platform identity tables at all (0041).
/// </para>
/// </remarks>
internal sealed partial class LinkTestHost : WebApplicationFactory<Program>
{
    /// <summary>The <c>EVIDGATE:Login:PublicBaseUrl</c> the redirect URIs and the origin check are built from.</summary>
    internal const string PublicBaseUrl = "https://app.evidgate.test";

    /// <summary>The provider name the first sign-in cases use.</summary>
    internal const string Google = "google";

    /// <summary>The provider name the second-provider cases use.</summary>
    internal const string GitHub = "github";

    /// <summary>A user agent the session row carries, so a case can prove it was stored.</summary>
    internal const string UserAgent = "evidgate-link-tests/1.0";

    private const int StateLifetimeSeconds = 600;

    private const int RetryAfterSeconds = 30;

    private const int SessionPepperBytes = 32;

    private readonly string _connectionString;
    private readonly FakeLoginProvider[] _providers;
    private readonly ILoggerProvider? _logProvider;
    private readonly Action<IServiceCollection>? _overrides;

    private LinkTestHost(
        string connectionString,
        FakeLoginProvider[] providers,
        ILoggerProvider? logProvider,
        Action<IServiceCollection>? overrides)
    {
        _connectionString = connectionString;
        _providers = providers;
        _logProvider = logProvider;
        _overrides = overrides;

        // Generated per host, never a literal in the tree (scripts/check-no-secret-literals.sh).
        Pepper = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SessionPepperBytes);
    }

    /// <summary>The session pepper this host configured, so a case can compute the HMAC the store writes.</summary>
    internal byte[] Pepper { get; }

    /// <summary>The running test's cancellation token.</summary>
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A configured deployment over the harness database with exactly the given providers.</summary>
    /// <param name="fixture">The harness (its app connection string is what the host is configured with).</param>
    /// <param name="providers">The fakes every route resolves as <see cref="ILoginProvider"/>.</param>
    internal static LinkTestHost Start(DatabaseFixture fixture, params FakeLoginProvider[] providers) =>
        Start(fixture, logProvider: null, overrides: null, providers);

    /// <summary>A configured deployment whose container also applies <paramref name="overrides"/>.</summary>
    /// <param name="fixture">The harness.</param>
    /// <param name="overrides">Replacements applied after the host's own composition (a seam a case substitutes).</param>
    /// <param name="providers">The fakes every route resolves as <see cref="ILoginProvider"/>.</param>
    internal static LinkTestHost Start(
        DatabaseFixture fixture,
        Action<IServiceCollection> overrides,
        params FakeLoginProvider[] providers) =>
        Start(fixture, logProvider: null, overrides, providers);

    /// <summary>A configured deployment that hands every log entry to <paramref name="logProvider"/>.</summary>
    /// <param name="fixture">The harness.</param>
    /// <param name="logProvider">The capturing provider.</param>
    /// <param name="providers">The fakes every route resolves as <see cref="ILoginProvider"/>.</param>
    internal static LinkTestHost StartWithLogs(
        DatabaseFixture fixture,
        ILoggerProvider logProvider,
        params FakeLoginProvider[] providers) =>
        Start(fixture, logProvider, overrides: null, providers);

    /// <summary>A fake provider that verifies one identity of the given shape.</summary>
    /// <param name="name">The route value it answers to.</param>
    /// <param name="subject">The provider-verified subject.</param>
    /// <param name="email">The verified address.</param>
    internal static FakeLoginProvider Provider(string name, string subject, string email) =>
        new(name) { Result = LoginProviderResult.Ok(Identity(name, subject, email)) };

    /// <summary>A provider-verified identity of the shape a successful flow yields.</summary>
    /// <param name="provider">The provider's registered name.</param>
    /// <param name="subject">The provider's subject.</param>
    /// <param name="email">The verified address.</param>
    internal static VerifiedIdentity Identity(string provider, string subject, string email) =>
        new(provider, subject, email, EmailVerified: true, DisplayName: "Test Member");

    /// <summary>Points an already-registered fake at another identity (the instance the container holds).</summary>
    /// <param name="provider">The route value.</param>
    /// <param name="identity">The identity its next completion verifies.</param>
    internal void Use(string provider, VerifiedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        Provider(provider).Result = LoginProviderResult.Ok(identity);
    }

    /// <summary>The registered fake that answers to <paramref name="provider"/>.</summary>
    /// <param name="provider">The route value.</param>
    internal FakeLoginProvider Provider(string provider) =>
        _providers.First(candidate => string.Equals(candidate.Name, provider, StringComparison.Ordinal));

    /// <summary>A client that neither follows the 302 nor keeps cookies, so each request is explicit.</summary>
    internal HttpClient CreateLinkClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            // HTTPS, as the deployment is: the antiforgery cookie of 02.3.2-T1 is `Secure=Always`, and ASP.NET
            // Core refuses to mint the pair on a request that did not arrive over TLS. A plain-http client
            // would prove a login route no browser can reach.
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    // ------------------------------------------------------------------ the three-step flow

    /// <summary>
    /// One whole sign-in: <c>oidc/{provider}/start</c>, the state cookie it writes, then the <c>callback</c>.
    /// </summary>
    /// <param name="client">A client of this host (never following redirects).</param>
    /// <param name="provider">The provider route value.</param>
    /// <returns>The callback's response, which the caller disposes.</returns>
    internal async Task<HttpResponseMessage> SignInAsync(HttpClient client, string provider)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var start = await client
            .SendAsync(Request(HttpMethod.Get, "/v1/auth/oidc/" + provider + "/start"), Token)
            .ConfigureAwait(false);
        start.StatusCode.ShouldBe(HttpStatusCode.Found, "the start route answers the 302 to the provider");

        var (payload, cookieValue) = PendingLogin(start);

        return await client
            .SendAsync(CallbackRequest(provider, cookieValue, payload.State), Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One whole <c>POST /v1/auth/link/{provider}</c> start: the answer the route gave, the pending-login
    /// cookie it wrote and the state it carries, so the callback can be driven next. The caller disposes the
    /// response.
    /// </summary>
    /// <param name="client">A client of this host.</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="session">The signed-in browser's session and its antiforgery pair.</param>
    /// <param name="returnUrl">The optional body <c>return_url</c>, or null for no body at all.</param>
    internal async Task<(HttpResponseMessage Response, LoginStatePayload Payload, string StateCookie)> StartLinkAsync(
        HttpClient client,
        string provider,
        LinkSession session,
        string? returnUrl = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(session);

        using var request = Request(HttpMethod.Post, "/v1/auth/link/" + provider);
        request.Headers.Add("Cookie", string.Join("; ", session.Cookies().Select(cookie => cookie.Name + "=" + cookie.Value)));
        request.Headers.Add(RequestTokenHeaderName, session.RequestToken);

        if (returnUrl is not null)
        {
            request.Content = JsonContent(new { return_url = returnUrl });
        }

        var response = await client.SendAsync(request, Token).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "a signed-in start answers the authorization URL");

        var (payload, cookieValue) = PendingLogin(response);

        return (response, payload, cookieValue);
    }

    /// <summary>
    /// The provider's callback for a link attempt: it verifies the fake's identity, stages the confirm cookie
    /// and answers the 302 the browser follows. The caller disposes the response.
    /// </summary>
    /// <param name="client">A client of this host.</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="stateCookie">The pending-login cookie <see cref="StartLinkAsync"/> wrote.</param>
    /// <param name="state">The one-time state inside that cookie.</param>
    /// <param name="session">The caller's session, or null to send none.</param>
    internal static async Task<HttpResponseMessage> LinkCallbackAsync(
        HttpClient client,
        string provider,
        string stateCookie,
        string state,
        LinkSession? session)
    {
        var request = CallbackRequest(provider, stateCookie, state);

        if (session is not null)
        {
            request.Headers.Add("Cookie", SessionCookieName + "=" + session.Session);
        }

        return await client.SendAsync(request, Token).ConfigureAwait(false);
    }

    /// <summary>One request on a linking route, carrying the cookies a case names.</summary>
    /// <param name="client">A client of this host.</param>
    /// <param name="method">The verb.</param>
    /// <param name="path">The path below <c>/v1/auth</c>.</param>
    /// <param name="cookies">The cookies to send, in order.</param>
    /// <param name="origin">An <c>Origin</c> header to send, or null for none (the browser's own requests omit it).</param>
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        (string Name, string Value)[] cookies,
        string? origin = null,
        string? requestToken = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = Request(method, "/v1/auth" + path);

        if (cookies.Length > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value)));
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (requestToken is not null)
        {
            request.Headers.Add(RequestTokenHeaderName, requestToken);
        }

        return await client.SendAsync(request, Token).ConfigureAwait(false);
    }

    /// <summary>
    /// One request on a linking route from a signed-in browser: its session, antiforgery cookie, the staged link cookie
    /// when given, and the request token a cookie-authenticated mutation echoes in its header.
    /// </summary>
    /// <param name="client">A client of this host.</param>
    /// <param name="method">The verb.</param>
    /// <param name="path">The path below <c>/v1/auth</c>.</param>
    /// <param name="session">The browser state the sign-in left.</param>
    /// <param name="linkCookie">The staged link cookie value, or null for none.</param>
    /// <param name="origin">An <c>Origin</c> header to send, or null for none.</param>
    internal static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        LinkSession session,
        string? linkCookie = null,
        string? origin = null) =>
        SendAsync(client, method, path, session.Cookies(linkCookie), origin, session.RequestToken);

    // ------------------------------------------------------------------ reading what the server wrote

    /// <summary>Unprotects the pending-login cookie with this host's own protector and serializer.</summary>
    /// <param name="response">A response that set <see cref="LoginStateCookie.Name"/>.</param>
    internal (LoginStatePayload Payload, string CookieValue) PendingLogin(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var value = CookieValue(response, LoginStateCookie.Name)
            ?? throw new InvalidOperationException("the response set no " + LoginStateCookie.Name + " cookie");

        var protector = Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LoginStateCookie.ProtectorPurpose);

        var payload = JsonSerializer.Deserialize(
            protector.Unprotect(value),
            LoginStateJsonContext.Default.LoginStatePayload)
            ?? throw new InvalidOperationException("the pending-login cookie did not deserialize");

        return (payload, value);
    }

    /// <summary>Unprotects the staged-link cookie with this host's own protector and serializer, or null.</summary>
    /// <param name="response">A response that may have staged a link.</param>
    internal LinkConfirmPayload? StagedLink(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var value = CookieValue(response, LinkConfirmCookie.Name);

        return value is null ? null : ReadStagedLink(value);
    }

    /// <summary>Unprotects one staged-link cookie value with this host's own protector and serializer.</summary>
    /// <param name="cookieValue">The <c>__Host-evidgate_link</c> value a browser would send back.</param>
    internal LinkConfirmPayload ReadStagedLink(string cookieValue)
    {
        ArgumentNullException.ThrowIfNull(cookieValue);

        var protector = Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LinkConfirmCookie.ProtectorPurpose);

        return JsonSerializer.Deserialize(
            protector.Unprotect(cookieValue),
            LinkConfirmJsonContext.Default.LinkConfirmPayload)
            ?? throw new InvalidOperationException("the staged-link cookie did not deserialize");
    }

    /// <summary>A staged-link cookie value this host's protector accepts, for a case that forges one.</summary>
    /// <param name="payload">The payload to protect.</param>
    internal string Protect(LinkConfirmPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LinkConfirmCookie.ProtectorPurpose)
            .Protect(JsonSerializer.Serialize(payload, LinkConfirmJsonContext.Default.LinkConfirmPayload));
    }

    /// <summary>The session cookie's value as the browser would store it, or null when the response sets none.</summary>
    internal static string? SessionCookieValue(HttpResponseMessage response) => CookieValue(response, SessionCookieName);

    /// <summary>The name of the session cookie, pinned here so a rename fails this suite instead of passing silently.</summary>
    internal const string SessionCookieName = "__Host-evidgate_session";

    /// <summary>The antiforgery cookie a sign-in issues beside the session (D6), pinned as <see cref="SessionCookieName"/> is.</summary>
    internal const string AntiforgeryCookieName = "__Host-evidgate_af";

    /// <summary>The request-token cookie a page script reads and echoes in <see cref="RequestTokenHeaderName"/>.</summary>
    internal const string RequestTokenCookieName = "XSRF-TOKEN";

    /// <summary>The header a cookie-authenticated link mutation carries (00 §9.1, T38).</summary>
    internal const string RequestTokenHeaderName = "X-XSRF-TOKEN";

    /// <summary>
    /// The browser state one sign-in leaves behind (D6): the session cookie and the antiforgery pair the same response
    /// issued. A cookie-authenticated link mutation carries all of it, as the page does.
    /// </summary>
    internal sealed record LinkSession(string Session, string Antiforgery, string RequestToken)
    {
        /// <summary>Reads the session and its pair from a sign-in response; throws when the response issued one out.</summary>
        /// <param name="response">The sign-in callback response.</param>
        internal static LinkSession FromSignIn(HttpResponseMessage response) => new(
            CookieValue(response, SessionCookieName) ?? throw new InvalidOperationException("the sign-in issued no session cookie"),
            CookieValue(response, AntiforgeryCookieName) ?? throw new InvalidOperationException("the sign-in issued no antiforgery cookie"),
            CookieValue(response, RequestTokenCookieName) ?? throw new InvalidOperationException("the sign-in issued no request token"));

        /// <summary>The cookies a link request sends: the session, its antiforgery cookie and the staged link cookie when given.</summary>
        /// <param name="linkCookie">The staged link cookie value, or null for none.</param>
        internal (string Name, string Value)[] Cookies(string? linkCookie = null)
        {
            (string Name, string Value)[] cookies = linkCookie is null
                ? [(SessionCookieName, Session), (AntiforgeryCookieName, Antiforgery)]
                : [(SessionCookieName, Session), (AntiforgeryCookieName, Antiforgery), (LinkConfirmCookie.Name, linkCookie)];

            return cookies;
        }
    }

    /// <summary>Every <c>Set-Cookie</c> value of <paramref name="name"/> the response carries.</summary>
    internal static IReadOnlyList<string> Cookies(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith(name + "=", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>The value of the first <c>Set-Cookie</c> of <paramref name="name"/>, or null.</summary>
    internal static string? CookieValue(HttpResponseMessage response, string name)
    {
        var cookies = Cookies(response, name);
        if (cookies.Count == 0)
        {
            return null;
        }

        var cookie = cookies[0];

        return cookie[(name.Length + 1)..cookie.IndexOf(';', StringComparison.Ordinal)];
    }

    /// <summary>The 00 §9.2 <c>error.code</c> of a response body, or null when it carries none.</summary>
    internal static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync(Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);

        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }
}
