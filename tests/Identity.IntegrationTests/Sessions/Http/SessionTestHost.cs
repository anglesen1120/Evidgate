using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login;
using Evidgate.Testing;
using Identity.IntegrationTests.Login;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// The host the 02.3.2-T1 HTTP suites drive (AC1–AC5): the real Api composition
/// (<c>Program</c> → <c>ApiHost</c>) over the harness's migrated database, with the real session
/// authenticator, the real antiforgery pair and its session binding, the deployment's session pepper —
/// and a clock, a statement log and a log/metric capture the case owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> Everything on the request path: <c>SessionXsrfMiddleware</c>, the memoized
/// <c>ISessionRequestAuthenticator</c>, <c>SessionCallerAuthenticator</c> as the host's
/// <c>ICallerAuthenticator</c> and <c>ICallerRefusalAuthenticator</c>, <c>TenantResolutionFilter</c>, the
/// <c>evidgate_resolve</c> functions of migration 0045, <c>SessionCookieWriter</c>,
/// <c>SessionAntiforgeryDataProvider</c> and the <c>/v1/orgs</c> route the authenticated cases drive. The
/// connection string is the harness's APP role, so nothing here runs with a privilege production lacks.
/// </para>
/// <para>
/// <b>What a case substitutes.</b> Only three things, each because it is a clock or an observation:
/// <see cref="Clock"/> (a <see cref="MutableClock"/>, so an idle or absolute expiry is proven without
/// waiting), <see cref="Statements"/> (the app-role <see cref="NpgsqlDataSource"/> rebuilt with a logger, which
/// is how "exactly one <c>session_authenticate</c> statement per authenticated request" is counted) and —
/// when a case passes one — a capturing <see cref="ILoggerProvider"/>. The login providers are
/// <see cref="FakeLoginProvider"/>s, exactly like the linking suites: the real ones need live credentials and
/// the network, and the provider half of 02.3.1 is proven by its own suites.
/// </para>
/// <para>
/// <b>Cookies are sent by hand.</b> <see cref="CreateSessionClient"/> neither stores cookies nor follows redirects, so
/// every request states exactly which cookie values it carries — which is what makes "the OLD cookie is dead"
/// (AC7) and "a replay after logout stays 401" (AC8) assertable, and what lets a case present a rotated
/// cookie's predecessor on purpose.
/// </para>
/// </remarks>
internal sealed class SessionTestHost : WebApplicationFactory<Program>
{
    /// <summary>The session cookie (02.3.2 D1); pinned here so a rename fails this suite instead of passing.</summary>
    internal const string SessionCookieName = "__Host-evidgate_session";

    /// <summary>The script-readable request-token cookie of the antiforgery pair (D6).</summary>
    internal const string XsrfCookieName = "XSRF-TOKEN";

    /// <summary>ASP.NET Core's antiforgery cookie, the pair's other half (D6).</summary>
    internal const string AntiforgeryCookieName = "__Host-evidgate_af";

    /// <summary>The header a cookie-authenticated mutation echoes the request token in (D6, 00 §9.1).</summary>
    internal const string XsrfHeaderName = "X-XSRF-TOKEN";

    /// <summary>The provider route value of the first sign-in cases.</summary>
    internal const string Google = "google";

    /// <summary>The provider route value of the second-provider cases.</summary>
    internal const string GitHub = "github";

    /// <summary>The user agent every request sends; a canary for the "never logged" rule (D9).</summary>
    internal const string UserAgent = "evidgate-session-tests/1.0";

    /// <summary>Idle lifetime this host binds unless a case asks for another (a short one keeps arithmetic exact).</summary>
    internal const int DefaultIdleSeconds = 600;

    /// <summary>Absolute lifetime this host binds unless a case asks for another.</summary>
    internal const int DefaultAbsoluteSeconds = 3600;

    /// <summary>The pepper version stored beside every hash this host writes.</summary>
    internal const int PepperVersion = 3;

    private const int PepperBytes = 32;

    private const int StateLifetimeSeconds = 600;

    private const int RetryAfterSeconds = 30;

    private const string PublicBaseUrl = "https://app.evidgate.test";

    private readonly string _connectionString;
    private readonly FakeLoginProvider[] _providers;
    private readonly ILoggerProvider? _logProvider;

    private SessionTestHost(
        string connectionString,
        MutableClock clock,
        byte[] pepper,
        int idleSeconds,
        int absoluteSeconds,
        ILoggerProvider? logProvider,
        FakeLoginProvider[] providers)
    {
        _connectionString = connectionString;
        _providers = providers;
        _logProvider = logProvider;
        Clock = clock;
        Pepper = pepper;
        IdleSeconds = idleSeconds;
        AbsoluteSeconds = absoluteSeconds;
    }

    /// <summary>Non-failing assertion helper: the fixed instant every case starts from.</summary>
    internal static DateTimeOffset StartInstant { get; } = new(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>The running test's cancellation token (xUnit1051).</summary>
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The clock every session instant of this host comes from.</summary>
    internal MutableClock Clock { get; }

    /// <summary>Every statement the app role executed, so "one statement per request" is countable.</summary>
    internal StatementLog Statements { get; } = new();

    /// <summary>The path→route-pattern record of every request this host served.</summary>
    internal RequestRecorder Recorder { get; } = new();

    /// <summary>The session pepper this host configured, so a case can reproduce the stored HMAC.</summary>
    internal byte[] Pepper { get; }

    /// <summary>The idle lifetime this host bound, in seconds.</summary>
    internal int IdleSeconds { get; }

    /// <summary>The absolute lifetime this host bound, in seconds.</summary>
    internal int AbsoluteSeconds { get; }

    /// <summary>The harness the host runs against (its owner connection arranges and reads rows).</summary>
    internal required DatabaseFixture Fixture { get; init; }

    /// <summary>
    /// A host over the harness database: a real Api, a mutable clock, a fresh session pepper, and the
    /// lifetimes <paramref name="idleSeconds"/>/<paramref name="absoluteSeconds"/> (or this suite's short
    /// defaults, so an expiry is a clock move rather than a wait).
    /// </summary>
    /// <param name="fixture">The harness (its app-role connection string is what the host connects with).</param>
    /// <param name="idleSeconds">The sliding lifetime to bind, or null for <see cref="DefaultIdleSeconds"/>.</param>
    /// <param name="absoluteSeconds">The hard lifetime to bind, or null for <see cref="DefaultAbsoluteSeconds"/>.</param>
    /// <param name="logProvider">A capturing provider when the case asserts on what the deployment logs.</param>
    internal static SessionTestHost Start(
        DatabaseFixture fixture,
        int? idleSeconds = null,
        int? absoluteSeconds = null,
        ILoggerProvider? logProvider = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var fresh = RandomNumberGenerator.GetBytes(PepperBytes);

        return new SessionTestHost(
            fixture.AppConnectionString,
            new MutableClock(StartInstant),
            fresh,
            idleSeconds ?? DefaultIdleSeconds,
            absoluteSeconds ?? DefaultAbsoluteSeconds,
            logProvider,
            [new FakeLoginProvider(Google), new FakeLoginProvider(GitHub)])
        {
            Fixture = fixture,
        };
    }

    // ------------------------------------------------------------------ clients

    /// <summary>
    /// A client that neither keeps cookies nor follows redirects: every request states the cookies it sends
    /// (see the class remarks).
    /// </summary>
    internal HttpClient CreateSessionClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            // HTTPS, as the deployment is: the antiforgery cookie of 02.3.2-T1 is `Secure=Always`, and ASP.NET
            // Core refuses to mint the pair on a request that did not arrive over TLS. A plain-http client
            // would prove a login route no browser can reach.
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    /// <summary>The registered fake that answers to <paramref name="provider"/>.</summary>
    /// <param name="provider">The route value.</param>
    internal FakeLoginProvider Provider(string provider) =>
        _providers.First(candidate => string.Equals(candidate.Name, provider, StringComparison.Ordinal));

    /// <summary>Points a provider's next completion at one verified identity (the instance the container holds).</summary>
    /// <param name="provider">The route value.</param>
    /// <param name="subject">The provider's subject.</param>
    /// <param name="email">The verified address.</param>
    internal void VerifyIdentity(string provider, string subject, string email)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);

        Provider(provider).Result = LoginProviderResult.Ok(
            new VerifiedIdentity(provider, subject, email, EmailVerified: true, DisplayName: "Session Test Member"));
    }

    // ------------------------------------------------------------------ the sign-in flow (real routes)

    /// <summary>
    /// One whole sign-in on the real routes: <c>oidc/{provider}/start</c>, the pending-login cookie it writes,
    /// then <c>oidc/{provider}/callback</c>. The callback's response is what carries the session cookie and the
    /// antiforgery pair (D6), so the caller disposes it.
    /// </summary>
    /// <param name="client">A client of this host (never following redirects).</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="presentedSessionCookie">
    /// The session cookie value the browser still holds when it signs in again, or null: D5a revokes that row in
    /// the same statement as the insert and chains <c>rotated_from</c>, which is how AC7's login rotation is
    /// driven.
    /// </param>
    internal async Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string provider = Google,
        string? presentedSessionCookie = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(provider);

        using var start = await client
            .SendAsync(Request(HttpMethod.Get, "/v1/auth/oidc/" + provider + "/start"), Token)
            .ConfigureAwait(false);

        start.StatusCode.ShouldBe(HttpStatusCode.Found, "the start route answers the 302 to the provider");

        var stateCookie = CookieValue(start, LoginStateCookie.Name)
            ?? throw new InvalidOperationException("the response set no " + LoginStateCookie.Name + " cookie");

        // The callback is driven by hand, so the pending-login cookie the start route just wrote must travel
        // with it: without it the completion has no one-time state to consume and answers 401 token_invalid. A
        // sign-in that follows a previous one carries BOTH cookies, the pending one and the session the browser
        // still holds (D5a reads the second one to revoke the row it names).
        var cookies = LoginStateCookie.Name + "=" + stateCookie;
        if (presentedSessionCookie is not null)
        {
            cookies += "; " + SessionCookieName + "=" + presentedSessionCookie;
        }

        var request = Request(
            HttpMethod.Get,
            "/v1/auth/oidc/" + provider + "/callback?state="
                + Uri.EscapeDataString(PendingLoginState(stateCookie))
                + "&code=authorization-code");

        request.Headers.Add("Cookie", cookies);

        return await client.SendAsync(request, Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds an account that can sign in and signs it in: an organization, the <c>user</c> row, its linkable
    /// identity and an active membership — then the real sign-in route, whose 302 carries the session cookie
    /// and the antiforgery pair.
    /// </summary>
    /// <param name="client">A client of this host.</param>
    /// <param name="provider">The provider route value to sign in with.</param>
    internal async Task<SignedInMember> SignInMemberAsync(HttpClient client, string provider = Google)
    {
        ArgumentNullException.ThrowIfNull(client);

        var member = await SeedMemberAsync(provider).ConfigureAwait(false);

        using var response = await SignInAsync(client, provider).ConfigureAwait(false);

        response.StatusCode.ShouldBe(HttpStatusCode.Found, "a member's sign-in completes with the 302");

        var session = SessionCookieValue(response)
            ?? throw new InvalidOperationException("the sign-in set no " + SessionCookieName + " cookie");

        return new SignedInMember(member.OrgId, member.UserId, member.Email, session, XsrfPair.From(response));
    }

    // ------------------------------------------------------------------ arrangement (owner seam)

    /// <summary>Seeds one organization through the harness's owner seam.</summary>
    internal Task<Guid> SeedOrganizationAsync() => Fixture.SeedOrgAsync(cancellationToken: Token);

    /// <summary>Seeds one active <c>public."user"</c> row and returns its id.</summary>
    /// <param name="email">The address, or null for a unique one.</param>
    /// <param name="status">The account status (<c>active</c> unless a case disables it).</param>
    internal async Task<Guid> SeedUserAsync(string? email = null, string status = "active")
    {
        var id = Guid.CreateVersion7();

        await ArrangeAsync(
            """
            insert into public."user" (id, email, display_name, status)
            values (@id, @email, @display_name, @status)
            """,
            ("id", id),
            ("email", email ?? "session-" + id.ToString("N") + "@example.test"),
            ("display_name", "Session Test User"),
            ("status", status)).ConfigureAwait(false);

        return id;
    }

    /// <summary>Seeds an active membership, which is what gives a session an org to act in (00 §5.4.8).</summary>
    /// <param name="orgId">The org.</param>
    /// <param name="userId">The member.</param>
    /// <param name="status">The membership status.</param>
    internal Task SeedMembershipAsync(Guid orgId, Guid userId, string status = "active") =>
        ArrangeAsync(
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org_id, @user_id, @email, @status, now(), now(), 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("user_id", userId),
            ("email", "member-" + userId.ToString("N") + "@example.test"),
            ("status", status));

    /// <summary>
    /// Seeds everything a sign-in needs for one account and points the provider's fake at it: the org, the
    /// <c>user</c> row, the <c>(provider, subject)</c> identity that resolves to that row, and the active
    /// membership the completion requires (a verified sign-in that joins no org answers 403 <c>no_membership</c>).
    /// </summary>
    /// <param name="provider">The provider route value.</param>
    /// <param name="subject">The subject, or null for a unique one.</param>
    /// <param name="email">The verified address, or null for a unique one.</param>
    internal async Task<SeededMember> SeedMemberAsync(
        string provider = Google,
        string? subject = null,
        string? email = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);

        var resolvedSubject = subject ?? "subject-" + Guid.CreateVersion7().ToString("N");
        var resolvedEmail = email ?? resolvedSubject + "@example.test";

        // First the account, then the identity that must resolve to it: the completion upserts on
        // (provider, subject), so a seeded identity is what makes it land on the seeded user.
        var userId = await SeedUserAsync(resolvedEmail).ConfigureAwait(false);
        var orgId = await SeedOrganizationAsync().ConfigureAwait(false);

        await ArrangeAsync(
            """
            insert into public.user_identity (id, user_id, provider, subject, email_at_provider)
            values (@id, @user_id, @provider, @subject, @email)
            """,
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("provider", provider),
            ("subject", resolvedSubject),
            ("email", resolvedEmail)).ConfigureAwait(false);

        await SeedMembershipAsync(orgId, userId).ConfigureAwait(false);
        VerifyIdentity(provider, resolvedSubject, resolvedEmail);

        return new SeededMember(orgId, userId, provider, resolvedSubject, resolvedEmail);
    }

    /// <summary>
    /// Creates a session directly through the real <see cref="ISessions"/> writer — the boundary cases a route
    /// cannot arrange (a session minted at a chosen instant) start here.
    /// </summary>
    /// <param name="userId">The account the session belongs to.</param>
    internal async Task<SessionTicket> CreateSessionAsync(Guid userId)
    {
        var created = await Services.GetRequiredService<ISessions>()
            .CreateAsync(new SessionCreation(userId, "203.0.113.7", UserAgent), Token)
            .ConfigureAwait(false);

        created.IsSuccess.ShouldBeTrue(created.Error?.Code ?? "the session store must create a session");

        return created.Value;
    }

    /// <summary>Revokes one session row through the owner seam (AC4, AC8: a logout or an admin revoke).</summary>
    /// <param name="sessionId">The row to revoke.</param>
    internal Task RevokeSessionAsync(Guid sessionId) =>
        ArrangeAsync(
            "update public.session set revoked_at = @now where id = @id and revoked_at is null",
            ("now", Clock.GetUtcNow().ToUniversalTime()),
            ("id", sessionId));

    /// <summary>Marks the account <c>disabled</c> (D7: the authenticator answers 403 <c>account_disabled</c>).</summary>
    /// <param name="userId">The account.</param>
    internal Task DisableUserAsync(Guid userId) =>
        ArrangeAsync("update public.\"user\" set status = 'disabled' where id = @id", ("id", userId));

    /// <summary>
    /// Stamps <c>rotation_required_at</c> on every live session of an account, which is what a privilege change
    /// does (D5b, AC7). Arranged through the owner seam so a case can create the state without driving a role
    /// change; the request path's rotation is the real one.
    /// </summary>
    /// <param name="userId">The account whose sessions must rotate.</param>
    internal Task MarkRotationRequiredAsync(Guid userId) =>
        ArrangeAsync(
            "update public.session set rotation_required_at = @now where user_id = @user_id and revoked_at is null",
            ("now", Clock.GetUtcNow().ToUniversalTime()),
            ("user_id", userId));

    /// <summary>Runs one statement as the owner (arranging the boundary of a case).</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="values">Its parameters.</param>
    internal Task ArrangeAsync(string sql, params (string Name, object Value)[] values) =>
        Fixture.SeedAsync(sql, values, Token);

    // ------------------------------------------------------------------ reading rows (owner seam)

    /// <summary>How many rows <paramref name="sql"/> counts, read as the owner.</summary>
    /// <param name="sql">The counting query.</param>
    /// <param name="values">Its parameters.</param>
    internal async Task<long> CountAsync(string sql, params (string Name, object Value)[] values) =>
        (long)(await ScalarAsync(sql, values).ConfigureAwait(false))!;

    /// <summary>One scalar on the owner connection, or null.</summary>
    /// <param name="sql">The query.</param>
    /// <param name="values">Its parameters.</param>
    internal async Task<object?> ScalarAsync(string sql, IReadOnlyList<(string Name, object Value)> values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalar = await command.ExecuteScalarAsync(Token).ConfigureAwait(false);

        return scalar is null or DBNull ? null : scalar;
    }

    /// <summary>The HMAC the <c>session</c> row of <paramref name="cookieValue"/> must carry.</summary>
    /// <param name="cookieValue">The cookie value the host minted.</param>
    internal byte[] HmacOf(string cookieValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(cookieValue);

        return HMACSHA256.HashData(Pepper, Encoding.ASCII.GetBytes(cookieValue));
    }

    /// <summary>The <c>session</c> row id of a cookie value, read as the owner (the only role that may).</summary>
    /// <param name="cookieValue">The cookie value the host minted.</param>
    internal async Task<Guid> SessionIdOfAsync(string cookieValue) =>
        (Guid)(await ScalarAsync(
            "select id from public.session where cookie_hmac = @hmac",
            [("hmac", HmacOf(cookieValue))]).ConfigureAwait(false))!;

    /// <summary>The lifecycle columns of one <c>session</c> row, as these suites assert on them.</summary>
    /// <param name="sessionId">The row.</param>
    internal async Task<SessionRow> SessionRowAsync(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select expires_at, absolute_expires_at, last_seen_at, revoked_at, rotation_required_at
              from public.session
             where id = @id
            """;
        command.Parameters.AddWithValue("id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(Token).ConfigureAwait(false);
        (await reader.ReadAsync(Token).ConfigureAwait(false)).ShouldBeTrue("the session row must exist");

        return new SessionRow(
            sessionId,
            await reader.GetFieldValueAsync<DateTimeOffset>(0, Token).ConfigureAwait(false),
            await reader.GetFieldValueAsync<DateTimeOffset>(1, Token).ConfigureAwait(false),
            await reader.GetFieldValueAsync<DateTimeOffset>(2, Token).ConfigureAwait(false),
            await reader.IsDBNullAsync(3, Token).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(3, Token).ConfigureAwait(false),
            await reader.IsDBNullAsync(4, Token).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(4, Token).ConfigureAwait(false));
    }

    // ------------------------------------------------------------------ requests and readers

    /// <summary>One request on this host: the cookies, the XSRF header and the bearer a case names.</summary>
    /// <param name="client">A client of this host.</param>
    /// <param name="method">The verb.</param>
    /// <param name="path">The path with its query.</param>
    /// <param name="cookieHeader">The <c>Cookie</c> header to send, or null.</param>
    /// <param name="xsrfToken">The <c>X-XSRF-TOKEN</c> value to send, or null for none.</param>
    /// <param name="authorization">The <c>Authorization</c> header to send, or null.</param>
    /// <param name="jsonBody">A JSON body to send as <c>application/json</c>, or null for none.</param>
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? cookieHeader = null,
        string? xsrfToken = null,
        string? authorization = null,
        string? jsonBody = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = Request(method, path);

        if (cookieHeader is not null)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        if (xsrfToken is not null)
        {
            request.Headers.Add(XsrfHeaderName, xsrfToken);
        }

        if (authorization is not null)
        {
            request.Headers.Add("Authorization", authorization);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        // Awaited inside the using scope: the request (and its content) must outlive the send, or a bodied request
        // reaches the server truncated.
        return await client.SendAsync(request, Token).ConfigureAwait(false);
    }

    /// <summary>A <c>Cookie</c> header value for one session cookie, optionally with its antiforgery pair.</summary>
    /// <param name="sessionCookie">The session value.</param>
    /// <param name="xsrf">The pair the login or rotation issued, or null.</param>
    internal static string CookieHeaderFor(string sessionCookie, XsrfPair? xsrf = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionCookie);

        var header = SessionCookieName + "=" + sessionCookie;

        return xsrf is null ? header : header + "; " + xsrf.CookieHeader;
    }

    /// <summary>Every <c>Set-Cookie</c> value of <paramref name="name"/> the response carries.</summary>
    /// <param name="response">The response.</param>
    /// <param name="name">The cookie name.</param>
    internal static IReadOnlyList<string> Cookies(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith(name + "=", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>The value of the first <c>Set-Cookie</c> of <paramref name="name"/>, or null.</summary>
    /// <param name="response">The response.</param>
    /// <param name="name">The cookie name.</param>
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

    /// <summary>The session cookie's value as the browser would store it, or null when the response sets none.</summary>
    /// <param name="response">The response.</param>
    internal static string? SessionCookieValue(HttpResponseMessage response) =>
        CookieValue(response, SessionCookieName);

    /// <summary>The 00 §9.2 <c>error.code</c> of a response body, or null when it carries none.</summary>
    /// <param name="response">The response.</param>
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

    /// <summary>The raw body text, for the assertions that nothing sensitive reaches the wire.</summary>
    /// <param name="response">The response.</param>
    internal static Task<string> BodyAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Content.ReadAsStringAsync(Token);
    }

    // ------------------------------------------------------------------ wiring

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                // 02.6.1-T1.4: the boot self-check is a deployment guard, not a behaviour under test here.
                ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
                // The pepper is resolved through ISecretProvider, which reads the configuration key the
                // environment-variables source uses (SecretNames.ToConfigurationKey). Generated per host, never
                // a literal (scripts/check-no-secret-literals.sh).
                ["EVIDGATE:SESSION:PEPPER"] = Convert.ToBase64String(Pepper),
                ["EVIDGATE:SESSION:PEPPERVERSION"] = PepperVersion.ToString(CultureInfo.InvariantCulture),
                ["EVIDGATE:SESSION:IDLESECONDS"] = IdleSeconds.ToString(CultureInfo.InvariantCulture),
                ["EVIDGATE:SESSION:ABSOLUTESECONDS"] = AbsoluteSeconds.ToString(CultureInfo.InvariantCulture),
                // 02.3.1: the login routes refuse to start without a public base URL and their two timings.
                ["EVIDGATE:Login:PublicBaseUrl"] = PublicBaseUrl,
                ["EVIDGATE:Login:StateLifetimeSeconds"] = StateLifetimeSeconds.ToString(CultureInfo.InvariantCulture),
                ["EVIDGATE:Login:RetryAfterSeconds"] = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture),
            }));

        builder.ConfigureTestServices(services =>
        {
            ArgumentNullException.ThrowIfNull(services);

            // Exactly the case's providers: the host's real ones are unconfigured anyway, and a fake named
            // `google` would shadow the real registration for the same route value.
            services.RemoveAll<ILoginProvider>();
            foreach (var provider in _providers)
            {
                services.AddSingleton<ILoginProvider>(provider);
            }

            // The clock: registered last, so it wins over every TryAddSingleton(TimeProvider.System) the
            // composition made (sessions, CI, login) — the whole request path then runs on the case's time.
            services.AddSingleton<TimeProvider>(Clock);

            if (_logProvider is { } logProvider)
            {
                // As the abstract type, because the logger factory resolves IEnumerable<ILoggerProvider> from DI:
                // registering the concrete type would leave the capture unconnected.
                services.AddSingleton<ILoggerProvider>(logProvider);
            }

            // The same app-role data source, with a logger that records each command: that is how "exactly one
            // session_authenticate statement per authenticated request" (P1/P3, AC2) is counted.
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(_ => new NpgsqlDataSourceBuilder(_connectionString)
                .UseLoggerFactory(Statements)
                .Build());

            services.AddSingleton<IStartupFilter>(new RecorderStartupFilter(Recorder));
        });
    }

    /// <summary>A request with the suite's user agent (the canary of the "never logged" rule).</summary>
    /// <param name="method">The verb.</param>
    /// <param name="path">The path.</param>
    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("User-Agent", UserAgent);

        return request;
    }

    /// <summary>Reads the one-time <c>state</c> out of the pending-login cookie with this host's own protector.</summary>
    /// <param name="cookieValue">The <c>__Host-evidgate_login</c> value the start route wrote.</param>
    private string PendingLoginState(string cookieValue)
    {
        var protector = Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LoginStateCookie.ProtectorPurpose);

        var payload = JsonSerializer.Deserialize(
            protector.Unprotect(cookieValue),
            LoginStateJsonContext.Default.LoginStatePayload)
            ?? throw new InvalidOperationException("the pending-login cookie did not deserialize");

        return payload.State;
    }

    /// <summary>
    /// Records the route pattern of every request AFTER it was served: the endpoint feature routing stamped on
    /// the request is still readable at that point, whatever the answer was (an answer the session refusal
    /// produced included).
    /// </summary>
    private sealed class RecorderStartupFilter(RequestRecorder recorder) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    try
                    {
                        await nextMiddleware().ConfigureAwait(false);
                    }
                    finally
                    {
                        recorder.Record(
                            context.Request.Method,
                            context.Request.Path.Value ?? string.Empty,
                            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText);
                    }
                });

                next(app);
            };
        }
    }
}

/// <summary>An account the harness seeded so it can sign in: the org it belongs to and the identity that resolves to it.</summary>
/// <param name="OrgId">The organization its active membership names.</param>
/// <param name="UserId">The <c>user</c> row the identity resolves to.</param>
/// <param name="Provider">The provider route value its identity is registered under.</param>
/// <param name="Subject">The provider's subject.</param>
/// <param name="Email">The verified address.</param>
internal sealed record SeededMember(Guid OrgId, Guid UserId, string Provider, string Subject, string Email);

/// <summary>
/// What one sign-in produced: the session cookie, the antiforgery pair and the account behind them. The
/// session's row id is deliberately NOT here — it is resolved through the owner seam
/// (<see cref="SessionTestHost.SessionIdOfAsync"/>) so a case reads the row that the value really minted.
/// </summary>
/// <param name="OrgId">The organization the member acts in.</param>
/// <param name="UserId">The account the session belongs to.</param>
/// <param name="Email">The verified address.</param>
/// <param name="SessionCookie">The <c>__Host-evidgate_session</c> value the browser now holds.</param>
/// <param name="Xsrf">The pair issued with the cookie, bound to this session.</param>
internal sealed record SignedInMember(
    Guid OrgId,
    Guid UserId,
    string Email,
    string SessionCookie,
    XsrfPair Xsrf);

/// <summary>
/// The antiforgery pair the browser holds after a response (D6): the two cookie VALUES and the request token a
/// mutation must echo in <c>X-XSRF-TOKEN</c>.
/// </summary>
/// <param name="AntiforgeryCookie">The <c>__Host-evidgate_af</c> value (the framework's cookie token).</param>
/// <param name="RequestToken">The <c>XSRF-TOKEN</c> value, i.e. the header's value.</param>
internal sealed record XsrfPair(string AntiforgeryCookie, string RequestToken)
{
    /// <summary>Both cookies, ready to append to a request's <c>Cookie</c> header.</summary>
    internal string CookieHeader =>
        SessionTestHost.XsrfCookieName + "=" + RequestToken + "; "
        + SessionTestHost.AntiforgeryCookieName + "=" + AntiforgeryCookie;

    /// <summary>The pair a response issued, or a failure when it issued no request token.</summary>
    /// <param name="response">The response that should carry <c>XSRF-TOKEN</c>.</param>
    /// <param name="presented">
    /// The pair the request already carried, when the response may leave the cookie token alone. ASP.NET Core
    /// reuses a valid antiforgery COOKIE token from the request instead of storing a new one; only the request
    /// token is minted afresh, and that is the half bound to the session (D6). A rotation therefore refreshes
    /// <c>XSRF-TOKEN</c> and leaves <c>__Host-evidgate_af</c> as the browser has it, so the pair the case
    /// assembles is the response's new request token over the cookie token the browser still holds.
    /// </param>
    internal static XsrfPair From(HttpResponseMessage response, XsrfPair? presented = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        var xsrf = SessionTestHost.CookieValue(response, SessionTestHost.XsrfCookieName)
            ?? throw new InvalidOperationException("the response issued no " + SessionTestHost.XsrfCookieName);
        var antiforgery = SessionTestHost.CookieValue(response, SessionTestHost.AntiforgeryCookieName)
            ?? presented?.AntiforgeryCookie
            ?? throw new InvalidOperationException("the response issued no " + SessionTestHost.AntiforgeryCookieName);

        return new XsrfPair(antiforgery, xsrf);
    }
}

/// <summary>One <c>session</c> row's lifecycle columns, as the HTTP suites assert on them.</summary>
/// <param name="SessionId">The row.</param>
/// <param name="ExpiresAt">The sliding deadline.</param>
/// <param name="AbsoluteExpiresAt">The hard deadline.</param>
/// <param name="LastSeenAt">The instant of the last accepted request (the slide throttle's input).</param>
/// <param name="RevokedAt">When the row was revoked, or null.</param>
/// <param name="RotationRequiredAt">When a privilege change demanded rotation, or null.</param>
internal sealed record SessionRow(
    Guid SessionId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? RotationRequiredAt);

/// <summary>One served request: its method, path and the route pattern routing matched (null for no match).</summary>
/// <param name="Method">The request method.</param>
/// <param name="Path">The request path.</param>
/// <param name="Pattern">The matched route's raw pattern, or null when no route matched.</param>
internal sealed record RecordedRequest(string Method, string Path, string? Pattern);

/// <summary>
/// The path→route-pattern record of one host, so a suite can prove that the path it called really matched a
/// route: an unmatched path answers 404 through the status-code pages, and a check that only looked at the
/// response would be satisfied by a refusal of the wrong thing.
/// </summary>
internal sealed class RequestRecorder
{
    private readonly ConcurrentQueue<RecordedRequest> _entries = new();

    /// <summary>Every request served so far, in order.</summary>
    internal IReadOnlyList<RecordedRequest> Entries => [.. _entries];

    /// <summary>Forgets what was recorded, so a case can assert on one request only.</summary>
    internal void Clear() => _entries.Clear();

    /// <summary>The pattern routing matched for <paramref name="method"/> <paramref name="path"/>, or null.</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path.</param>
    internal string? PatternFor(string method, string path) =>
        _entries
            .LastOrDefault(entry => string.Equals(entry.Method, method, StringComparison.Ordinal)
                && string.Equals(entry.Path, path, StringComparison.Ordinal))
            ?.Pattern;

    /// <summary>Appends one served request.</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path.</param>
    /// <param name="pattern">The matched route pattern, or null.</param>
    internal void Record(string method, string path, string? pattern) =>
        _entries.Enqueue(new RecordedRequest(method, path, pattern));
}

/// <summary>
/// Npgsql's <c>Executing command: {CommandText}</c> log line, captured once per command before it is sent
/// (02.3.2 §8 P1/P3): transaction control is dropped, so the capture is the work itself and "one statement per
/// authenticated request" is a count, not a guess.
/// </summary>
internal sealed class StatementLog : ILoggerFactory, ILogger
{
    private const string ExecutingCommandPrefix = "Executing command: ";

    private readonly ConcurrentQueue<string> _statements = new();

    /// <summary>Everything captured since the last <see cref="Clear"/>.</summary>
    internal IReadOnlyList<string> Snapshot() => [.. _statements];

    /// <summary>The captured statements that mention <paramref name="fragment"/>, case-insensitively.</summary>
    /// <param name="fragment">A table, function or keyword the statements are counted by.</param>
    internal IReadOnlyList<string> Containing(string fragment) =>
        [.. _statements.Where(statement => statement.Contains(fragment, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Forgets every captured statement.</summary>
    internal void Clear() => _statements.Clear();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => this;

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // One capture serves every category.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: the queue is managed memory owned by the test.
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        if (message is null || !message.StartsWith(ExecutingCommandPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var text = string.Join(' ', message[ExecutingCommandPrefix.Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (!IsTransactionControl(text))
        {
            _statements.Enqueue(text);
        }
    }

    /// <summary>Whether the statement only moves the transaction state (BEGIN/COMMIT/ROLLBACK/SAVEPOINT/RELEASE).</summary>
    /// <param name="statement">The normalised statement.</param>
    private static bool IsTransactionControl(string statement) =>
        statement.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
        || statement.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
        || statement.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase)
        || statement.StartsWith("SAVEPOINT", StringComparison.OrdinalIgnoreCase)
        || statement.StartsWith("RELEASE", StringComparison.OrdinalIgnoreCase)
        || statement.StartsWith("START TRANSACTION", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps every entry the host's own filters let through, so a suite can
/// assert on what the DEPLOYMENT would log rather than on what a hand-built logger would (02.3.2 §8 S3, D9).
/// </summary>
internal sealed class CapturingLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Every entry logged so far, in order.</summary>
    internal IReadOnlyList<LogEntry> Entries => [.. _entries];

    /// <summary>Drops what was captured so far, so a case can assert on one request only.</summary>
    internal void Clear() => _entries.Clear();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: the queue is managed memory owned by the test.
    }

    /// <summary>One log line: its category, level and formatted text.</summary>
    /// <param name="Category">The category name the logger was created under.</param>
    /// <param name="Level">The level the line was written at.</param>
    /// <param name="Message">The formatted message.</param>
    internal sealed record LogEntry(string Category, LogLevel Level, string Message);

    /// <summary>The logger a <see cref="CapturingLogProvider"/> hands out; every level is enabled.</summary>
    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}
