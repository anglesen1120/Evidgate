using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Audit;
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
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// What one <see cref="LoginCompletionTestHost"/> composes over the deployment's own registrations
/// (02.3.1-T1.4, 02.3.1-T2.1): the login providers a case scripts, and the doubles of the two seams the login
/// CALLS — the invite binder of 02.1.2-T2 and the <c>auth.login</c> audit writer of 02.3.1-T2.2 — plus the
/// invite key material a seeded invite row must be hashed with.
/// </summary>
internal sealed record LoginHostSettings
{
    /// <summary>The client address every request of the host reports, or null for the socket's own.</summary>
    internal IPAddress? RemoteIpAddress { get; init; }

    /// <summary>A logger provider every entry is also handed to, or null.</summary>
    internal ILoggerProvider? LogProvider { get; init; }

    /// <summary>A double of the invite binder, or null to keep the deployment's own.</summary>
    internal IMembershipBinder? Binder { get; init; }

    /// <summary>A double of the identity audit writer, or null to keep the deployment's own.</summary>
    internal IIdentityAuditWriter? AuditWriter { get; init; }

    /// <summary>The invite pepper to configure, or null for a per-host random one.</summary>
    internal byte[]? InvitePepper { get; init; }

    /// <summary>The version stored beside every invite hash (<c>EVIDGATE:Invite:PepperVersion</c>).</summary>
    internal int InvitePepperVersion { get; init; } = 1;

    /// <summary>Exactly the providers the case scripted (the host's real ones would shadow a fake name).</summary>
    internal ILoginProvider[] Providers { get; init; } = [];

    /// <summary>Extra service registrations for a host with no database (the in-memory ring and replay set).</summary>
    internal Action<IServiceCollection>? Seams { get; init; }
}

/// <summary>
/// The host the login-COMPLETION suites drive (02.3.1-T1.4): the real Api composition
/// (<c>Program</c> → <c>ApiHost</c>) over the harness's migrated database, the login options of a configured
/// deployment, the session pepper of <c>EVIDGATE:SESSION</c> and exactly the login providers a case passes.
/// </summary>
/// <remarks>
/// <para>
/// Two substitutions only: the providers (the real ones need live provider credentials and outbound calls;
/// the provider half of AC1 is <c>GoogleProviderFlowTests</c>' and <c>TokenValidationTests</c>', proven
/// against mocked provider endpoints) and, in the cases that need it, the client address the socket would
/// report. Everything else — routes, envelope filter, C2 pipeline, the session store, the database — is the
/// deployment's.
/// </para>
/// <para>
/// The connection string is the harness's APP role (00 §5.4.3), so the login path runs with the privileges it
/// has in production; the assertions about what was written read the same rows through the owner connection,
/// because the app role holds no privilege on the platform identity tables at all (0041).
/// </para>
/// </remarks>
internal sealed partial class LoginCompletionTestHost : WebApplicationFactory<Program>
{
    /// <summary>The <c>EVIDGATE:Login:PublicBaseUrl</c> the redirect URIs are built from.</summary>
    internal const string PublicBaseUrl = "https://app.evidgate.test";

    /// <summary>The provider name the Google cases sign in under.</summary>
    internal const string Google = "google";

    /// <summary>The provider-verified subject the Google cases use.</summary>
    internal const string GoogleSubject = "google-subject-1";

    /// <summary>The provider name the GitHub cases sign in under (<c>GitHubLoginProvider.ProviderName</c>).</summary>
    internal const string GitHub = "github";

    /// <summary>The provider-verified subject the GitHub cases use (GitHub's numeric account id).</summary>
    internal const string GitHubSubject = "149272102";

    /// <summary>The address the GitHub cases' account is created with.</summary>
    internal const string GitHubEmail = "github-member@example.test";

    /// <summary>A user agent the session row carries, so the case can prove it was stored.</summary>
    internal const string UserAgent = "evidgate-login-completion-tests/1.0";

    /// <summary>The invite token's cookie, so a case can present what <c>stage</c> set (02.3.1-T2 Q1).</summary>
    internal const string InviteCookieName = "__Host-evidgate_invite";

    /// <summary>The <c>POST /v1/auth/invite-token</c> route that stages a token for the next login.</summary>
    internal const string InviteTokenRoute = "/v1/auth/invite-token";

    /// <summary>The invite pepper's length in bytes (<c>InviteSecrets.MinKeyBytes</c>).</summary>
    internal const int InvitePepperBytes = 32;

    private const int StateLifetimeSeconds = 600;

    private const int RetryAfterSeconds = 30;

    private const int SessionPepperBytes = 32;

    /// <summary>A database that cannot answer, for the outage case: nothing listens on port 1.</summary>
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2";

    private readonly string _connectionString;
    private readonly LoginHostSettings _settings;

    private LoginCompletionTestHost(string connectionString, LoginHostSettings settings)
    {
        _connectionString = connectionString;
        _settings = settings;

        // Generated per host, never a literal in the tree (scripts/check-no-secret-literals.sh).
        Pepper = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SessionPepperBytes);
        InvitePepper = settings.InvitePepper
            ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(InvitePepperBytes);
    }

    /// <summary>
    /// The session pepper this host configured, so a case can recompute the HMAC the store writes and prove
    /// the cookie value itself is never stored.
    /// </summary>
    internal byte[] Pepper { get; }

    /// <summary>
    /// The invite pepper this host configured (02.1.2-T2's HMAC key), so a case can seed an invite row whose
    /// hash the deployment's own binder verifies.
    /// </summary>
    internal byte[] InvitePepper { get; }

    /// <summary>The running test's cancellation token.</summary>
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A configured deployment over the harness database with exactly the given providers.</summary>
    internal static LoginCompletionTestHost Start(DatabaseFixture fixture, params ILoginProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new LoginCompletionTestHost(
            fixture.AppConnectionString,
            new LoginHostSettings { Providers = providers });
    }

    /// <summary>A configured deployment whose socket reports <paramref name="remoteIpAddress"/> for the client.</summary>
    internal static LoginCompletionTestHost Start(
        DatabaseFixture fixture,
        IPAddress remoteIpAddress,
        params ILoginProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(remoteIpAddress);

        return new LoginCompletionTestHost(
            fixture.AppConnectionString,
            new LoginHostSettings { RemoteIpAddress = remoteIpAddress, Providers = providers });
    }

    /// <summary>A configured deployment that also hands every log entry to <paramref name="logProvider"/>.</summary>
    internal static LoginCompletionTestHost StartWithLogs(
        DatabaseFixture fixture,
        ILoggerProvider logProvider,
        params ILoginProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(logProvider);

        return new LoginCompletionTestHost(
            fixture.AppConnectionString,
            new LoginHostSettings { LogProvider = logProvider, Providers = providers });
    }

    /// <summary>
    /// A configured deployment over the harness database with whatever the case substitutes: the providers,
    /// the invite key material of a seeded invite row, and — for the cases that prove how the login calls it —
    /// a double of the invite binder (02.1.2-T2) or of the <c>auth.login</c> audit writer (02.3.1-T2.2).
    /// </summary>
    /// <param name="fixture">The harness's migrated database.</param>
    /// <param name="settings">What this host composes over the deployment's own registrations.</param>
    internal static LoginCompletionTestHost Start(DatabaseFixture fixture, LoginHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(settings);

        return new LoginCompletionTestHost(fixture.AppConnectionString, settings);
    }

    /// <summary>
    /// A configured deployment whose database cannot answer: the same host, a connection string for a closed
    /// port, so the completion's own database fault is produced by the real Npgsql path.
    /// </summary>
    internal static LoginCompletionTestHost StartWithoutDatabase(params ILoginProvider[] providers) =>
        new(UnreachableDatabase, new LoginHostSettings { Providers = providers, Seams = s => InMemoryLoginSeams.Apply(s, TimeProvider.System) });

    /// <summary>A <c>google</c> provider that verifies <paramref name="subject"/>'s identity.</summary>
    internal static FakeLoginProvider GoogleProvider(string subject = GoogleSubject) =>
        new(Google) { Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity(Google, subject)) };

    /// <summary>
    /// A provider that verifies exactly the identity a case names, including the address it reports, whether
    /// it calls that address verified and the Workspace <c>hd</c> claim — the three inputs the email-verified
    /// rule and the domain allowlist of AC4/AC5 read (02.3.1-T2.1).
    /// </summary>
    /// <param name="provider">The provider name (<see cref="Google"/>, <see cref="GitHub"/>).</param>
    /// <param name="subject">The provider-verified subject.</param>
    /// <param name="email">The address the provider reports.</param>
    /// <param name="emailVerified">The provider's verdict on that address.</param>
    /// <param name="hostedDomain">The <c>hd</c> claim, or null (GitHub never asserts one).</param>
    internal static FakeLoginProvider IdentityProvider(
        string provider,
        string subject,
        string email,
        bool emailVerified = true,
        string? hostedDomain = null) =>
        new(provider)
        {
            Result = LoginProviderResult.Ok(
                new VerifiedIdentity(provider, subject, email, emailVerified, "Test Member", hostedDomain)),
        };

    /// <summary>A <c>start</c> request, optionally naming a <c>returnUrl</c>.</summary>
    internal static HttpRequestMessage StartRequest(string provider, string? returnUrl = null) =>
        new(
            HttpMethod.Get,
            "/v1/auth/oidc/" + provider + "/start"
            + (returnUrl is null ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));

    /// <summary>A <c>callback</c> request carrying whatever pending-login cookie value the case presents.</summary>
    /// <param name="provider">The provider route value.</param>
    /// <param name="cookieValue">The pending-login cookie's value.</param>
    /// <param name="state">The <c>state</c> query value.</param>
    /// <param name="code">The authorization code, or null to omit it.</param>
    /// <param name="inviteCookieValue">
    /// The staged-invite cookie's value to present with the pending login (02.3.1-T2 Q1), or null for a login
    /// that staged no token. Both cookies travel in one header, as a browser sends them.
    /// </param>
    internal static HttpRequestMessage CallbackRequest(
        string provider,
        string? cookieValue,
        string? state,
        string? code = "authorization-code",
        string? inviteCookieValue = null)
    {
        var query = new List<string>();
        if (state is not null)
        {
            query.Add("state=" + Uri.EscapeDataString(state));
        }

        if (code is not null)
        {
            query.Add("code=" + Uri.EscapeDataString(code));
        }

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/v1/auth/oidc/" + provider + "/callback"
            + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query)));

        var cookies = new List<string>();
        if (cookieValue is not null)
        {
            cookies.Add(LoginStateCookie.Name + "=" + cookieValue);
        }

        if (inviteCookieValue is not null)
        {
            cookies.Add(InviteCookieName + "=" + inviteCookieValue);
        }

        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies));
        }

        return request;
    }

    /// <summary>A client that neither follows the 302 nor keeps cookies, so each request is explicit.</summary>
    internal HttpClient CreateLoginClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            // HTTPS, as the deployment is: the antiforgery cookie of 02.3.2-T1 is `Secure=Always`, and ASP.NET
            // Core refuses to mint the pair on a request that did not arrive over TLS. A plain-http client
            // would prove a login route no browser can reach.
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    /// <summary>One whole sign-in: <c>start</c>, read the pending-login cookie, then the <c>callback</c>.</summary>
    /// <param name="client">A client of that host (never following redirects).</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="returnUrl">The post-login destination to ask for, or null.</param>
    /// <param name="inviteCookieValue">
    /// The staged-invite cookie's value the callback presents (02.3.1-T2 Q1), or null when the case staged no
    /// token.
    /// </param>
    /// <returns>The callback's response, which the case asserts on and disposes.</returns>
    internal async Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string provider = Google,
        string? returnUrl = null,
        string? inviteCookieValue = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var start = await client
            .SendAsync(StartRequest(provider, returnUrl), Token)
            .ConfigureAwait(false);
        start.StatusCode.ShouldBe(HttpStatusCode.Found, "the start route answers the 302 to the provider");

        var (payload, cookieValue) = PendingLogin(start);

        return await client
            .SendAsync(CallbackRequest(provider, cookieValue, payload.State, inviteCookieValue: inviteCookieValue), Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One whole invited sign-in (02.3.1-T2 Q1/AC4): the token is staged on <c>POST /v1/auth/invite-token</c>,
    /// then the usual <c>start</c> + <c>callback</c> presents the staged cookie.
    /// </summary>
    /// <param name="client">A client of that host (never following redirects, never keeping cookies).</param>
    /// <param name="inviteToken">The <c>evgi_…</c> token a seeded invite row was minted for.</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="returnUrl">The post-login destination to ask for, or null.</param>
    /// <returns>The callback's response, which the case asserts on and disposes.</returns>
    internal async Task<HttpResponseMessage> SignInWithInviteAsync(
        HttpClient client,
        string inviteToken,
        string provider = Google,
        string? returnUrl = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var stage = await client
            .SendAsync(StageInviteRequest(inviteToken), Token)
            .ConfigureAwait(false);
        stage.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "the invite-token route stages a well-formed token and answers 204");

        return await SignInAsync(client, provider, returnUrl, InviteCookieValue(stage)).ConfigureAwait(false);
    }

    /// <summary>The <c>POST /v1/auth/invite-token</c> request that stages <paramref name="inviteToken"/>.</summary>
    /// <param name="inviteToken">The token, as the landing page received it.</param>
    internal static HttpRequestMessage StageInviteRequest(string inviteToken)
    {
        ArgumentNullException.ThrowIfNull(inviteToken);

        // The body the route reads: the web JSON defaults with snake_case names (`invite_token`).
        return new HttpRequestMessage(HttpMethod.Post, InviteTokenRoute)
        {
            Content = new StringContent(
                "{\"invite_token\":" + JsonSerializer.Serialize(inviteToken) + "}",
                Encoding.UTF8,
                "application/json"),
        };
    }

    /// <summary>Every <c>Set-Cookie</c> of the response that sets the staged-invite cookie.</summary>
    internal static IReadOnlyList<string> InviteCookies(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith(InviteCookieName + "=", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>The staged-invite cookie's value as a browser would store it, or null when none is set.</summary>
    internal static string? InviteCookieValue(HttpResponseMessage response)
    {
        var cookies = InviteCookies(response);
        if (cookies.Count == 0)
        {
            return null;
        }

        var cookie = cookies[0];

        return cookie[(InviteCookieName.Length + 1)..cookie.IndexOf(';', StringComparison.Ordinal)];
    }

    /// <summary>Unprotects the pending-login cookie with this host's own protector and serializer.</summary>
    internal (LoginStatePayload Payload, string CookieValue) PendingLogin(HttpResponseMessage start)
    {
        ArgumentNullException.ThrowIfNull(start);

        var cookies = StateCookies(start);
        if (cookies.Count == 0)
        {
            throw new InvalidOperationException("the start response set no " + LoginStateCookie.Name + " cookie");
        }

        var cookieValue = cookies[0].Split(';', 2)[0][(LoginStateCookie.Name.Length + 1)..];

        var protector = Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LoginStateCookie.ProtectorPurpose);

        var payload = JsonSerializer.Deserialize(
            protector.Unprotect(cookieValue),
            LoginStateJsonContext.Default.LoginStatePayload)
            ?? throw new InvalidOperationException("the pending-login cookie did not deserialize");

        return (payload, cookieValue);
    }

    /// <summary>Every <c>Set-Cookie</c> the response carries for the session cookie.</summary>
    internal static IReadOnlyList<string> SessionCookies(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith(LoginSessionCookieName + "=", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>
    /// The session cookie's name. Declared here because <c>SessionCookie</c> is internal to the module and the
    /// suites assert on the wire value: a rename has to fail here, not silently pass.
    /// </summary>
    internal const string LoginSessionCookieName = "__Host-evidgate_session";

    /// <summary>Every <c>Set-Cookie</c> of the response that sets the pending-login cookie.</summary>
    internal static IReadOnlyList<string> StateCookies(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith(LoginStateCookie.Name + "=", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>
    /// Brings <c>(provider, subject)</c> to an active membership, the way a real deployment does: a first sign-in
    /// creates the user row and answers 403 <c>no_membership</c>, the harness then seeds an organization and an
    /// active membership for that user id.
    /// </summary>
    /// <param name="fixture">The harness whose owner connection arranges the rows.</param>
    /// <param name="host">The running host the first sign-in runs on.</param>
    /// <param name="client">A client of that host.</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="subject">The provider-verified subject.</param>
    /// <returns>The seeded organization's id.</returns>
    internal static async Task<Guid> EstablishMemberAsync(
        DatabaseFixture fixture,
        LoginCompletionTestHost host,
        HttpClient client,
        string provider,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(client);

        using var first = await host.SignInAsync(client, provider).ConfigureAwait(false);
        first.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "AC3: a verified identity without a membership is refused, and that is what creates its user row");

        var userId = await UserIdAsync(fixture, provider, subject).ConfigureAwait(false);
        var orgId = await fixture.SeedOrgAsync().ConfigureAwait(false);
        await SeedMembershipAsync(fixture, orgId, userId).ConfigureAwait(false);

        return orgId;
    }

    /// <summary>The session cookie's value as the browser would store it, or null when the response sets none.</summary>
    internal static string? SessionCookieValue(HttpResponseMessage response)
    {
        var cookies = SessionCookies(response);
        if (cookies.Count == 0)
        {
            return null;
        }

        var cookie = cookies[0];

        return cookie[(LoginSessionCookieName.Length + 1)..cookie.IndexOf(';', StringComparison.Ordinal)];
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

    /// <summary>The raw body text, for the assertions that no database or provider text reaches the wire.</summary>
    internal static Task<string> BodyAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Content.ReadAsStringAsync(Token);
    }

    /// <summary>Seeds one membership row for <paramref name="userId"/>, as the owner.</summary>
    internal static Task SeedMembershipAsync(
        DatabaseFixture fixture,
        Guid orgId,
        Guid userId,
        string status = "active") =>
        fixture.SeedAsync(
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org_id, @user_id, @email, @status, now(), now(), 1)
            """,
            [
                ("id", Guid.CreateVersion7()),
                ("org_id", orgId),
                ("user_id", userId),
                ("email", $"member-{userId:N}@example.test"),
                ("status", status),
            ],
            Token);

    /// <summary>The <c>user.id</c> of <c>(provider, subject)</c>, read as the owner (the app role cannot).</summary>
    internal static Task<Guid> UserIdAsync(DatabaseFixture fixture, string provider, string subject) =>
        OwnerScalarAsync<Guid>(
            fixture,
            """
            select user_id from public.user_identity where provider = @provider and subject = @subject
            """,
            [("provider", provider), ("subject", subject)]);

    /// <summary>How many rows <paramref name="sql"/> counts, read as the owner.</summary>
    internal static async Task<int> CountAsync(DatabaseFixture fixture, string sql) =>
        (int)await OwnerScalarAsync<long>(fixture, sql, []).ConfigureAwait(false);

    /// <summary>
    /// The status of one <c>membership</c> row, read as the owner: the assertion that a login bound an invite
    /// (<c>active</c>) or left it alone (<c>pending</c>), 02.3.1-T2 AC4.
    /// </summary>
    /// <param name="fixture">The harness whose owner connection reads the row.</param>
    /// <param name="membershipId">The row.</param>
    internal static Task<string> MembershipStatusAsync(DatabaseFixture fixture, Guid membershipId) =>
        OwnerScalarAsync<string>(
            fixture,
            "select status from public.membership where id = @id",
            [("id", membershipId)]);

    /// <summary>The <c>user_id</c> a membership row is bound to, or null, read as the owner.</summary>
    /// <param name="fixture">The harness whose owner connection reads the row.</param>
    /// <param name="membershipId">The row.</param>
    internal static Task<Guid?> MembershipUserIdAsync(DatabaseFixture fixture, Guid membershipId) =>
        OwnerScalarAsync<Guid?>(
            fixture,
            "select user_id from public.membership where id = @id",
            [("id", membershipId)]);

    /// <summary>How many <c>auth.login</c> rows the chain of <paramref name="orgId"/> holds (Q5).</summary>
    /// <param name="fixture">The harness whose owner connection reads the chain.</param>
    /// <param name="orgId">The org whose <c>audit_log</c> rows are counted.</param>
    internal static async Task<int> LoginAuditRowsAsync(DatabaseFixture fixture, Guid orgId) =>
        (int)await OwnerScalarAsync<long>(
            fixture,
            "select count(*) from public.audit_log where org_id = @org_id and action = 'auth.login'",
            [("org_id", orgId)]).ConfigureAwait(false);

    /// <summary>Sets <c>user.status</c>, the owner's administrative act of 02.3.1 §6.</summary>
    /// <param name="fixture">The harness whose owner connection writes the row.</param>
    /// <param name="userId">The user.</param>
    /// <param name="status"><c>active</c> or <c>disabled</c>.</param>
    internal static Task<int> SetUserStatusAsync(DatabaseFixture fixture, Guid userId, string status) =>
        fixture.SeedAsync(
            "update public.\"user\" set status = @status, updated_at = now() where id = @id",
            [("id", userId), ("status", status)],
            Token);

    /// <summary>The session row <paramref name="sessionId"/> names, read as the owner.</summary>
    internal static async Task<SessionRow> SessionAsync(DatabaseFixture fixture, Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, user_id, cookie_hmac, host(ip), user_agent, expires_at,
                   absolute_expires_at, revoked_at
              from public.session
             where id = @id
            """;
        command.Parameters.AddWithValue("id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(Token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("the session row does not exist");
        }

        var id = await reader.GetFieldValueAsync<Guid>(0, Token).ConfigureAwait(false);
        var userId = await reader.GetFieldValueAsync<Guid>(1, Token).ConfigureAwait(false);
        var cookieHmac = await reader.GetFieldValueAsync<byte[]>(2, Token).ConfigureAwait(false);
        var ip = await reader.IsDBNullAsync(3, Token).ConfigureAwait(false)
            ? null
            : await reader.GetFieldValueAsync<string>(3, Token).ConfigureAwait(false);
        var userAgent = await reader.IsDBNullAsync(4, Token).ConfigureAwait(false)
            ? null
            : await reader.GetFieldValueAsync<string>(4, Token).ConfigureAwait(false);
        var expiresAt = await reader.GetFieldValueAsync<DateTimeOffset>(5, Token).ConfigureAwait(false);
        var absoluteExpiresAt = await reader.GetFieldValueAsync<DateTimeOffset>(6, Token).ConfigureAwait(false);
        var revokedAt = await reader.IsDBNullAsync(7, Token).ConfigureAwait(false)
            ? (DateTimeOffset?)null
            : await reader.GetFieldValueAsync<DateTimeOffset>(7, Token).ConfigureAwait(false);

        return new SessionRow(id, userId, cookieHmac, ip, userAgent, expiresAt, absoluteExpiresAt, revokedAt);
    }

    /// <summary>
    /// The HMAC the session store writes for <paramref name="cookieValue"/> under this host's pepper — the
    /// value <c>session.cookie_hmac</c> must hold, never the cookie itself.
    /// </summary>
    /// <param name="cookieValue">The <c>__Host-evidgate_session</c> value the response set.</param>
    /// <remarks>
    /// Synchronous on purpose: computing it from a test method would be a blocking call in async context
    /// (VSTHRD103), and .NET's async HMAC overloads only take streams, so the memory form is the honest one.
    /// </remarks>
    internal byte[] CookieHmac(string cookieValue)
    {
        ArgumentNullException.ThrowIfNull(cookieValue);

        return System.Security.Cryptography.HMACSHA256.HashData(
            Pepper,
            System.Text.Encoding.ASCII.GetBytes(cookieValue));
    }

    /// <summary>One <c>public.session</c> row as the owner connection sees it.</summary>
    /// <param name="Id">The row's id.</param>
    /// <param name="UserId">The user it belongs to.</param>
    /// <param name="CookieHmac">The stored HMAC of the cookie value, which must be 32 bytes.</param>
    /// <param name="Ip">The stored <c>inet</c> literal, or null.</param>
    /// <param name="UserAgent">The stored user agent, or null.</param>
    /// <param name="ExpiresAt">The sliding deadline.</param>
    /// <param name="AbsoluteExpiresAt">The hard deadline.</param>
    /// <param name="RevokedAt">Null for a fresh login.</param>
    internal sealed record SessionRow(
        Guid Id,
        Guid UserId,
        byte[] CookieHmac,
        string? Ip,
        string? UserAgent,
        DateTimeOffset ExpiresAt,
        DateTimeOffset AbsoluteExpiresAt,
        DateTimeOffset? RevokedAt);

    /// <summary>One scalar read as the owner connection (the only role that may read the identity tables).</summary>
    internal static async Task<T> OwnerScalarAsync<T>(
        DatabaseFixture fixture,
        string sql,
        IReadOnlyList<(string Name, object Value)> values)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalar = await command.ExecuteScalarAsync(Token).ConfigureAwait(false);

        return scalar is null or DBNull ? default! : (T)scalar;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Login:PublicBaseUrl"] = PublicBaseUrl,
                ["EVIDGATE:Login:StateLifetimeSeconds"] =
                    StateLifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Login:RetryAfterSeconds"] =
                    RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
                // The session pepper is a secret (EVIDGATE__SESSION__PEPPER): the environment secret provider
                // reads it from configuration by its own spelling, so an in-memory key resolves exactly as the
                // deployed environment variable does.
                ["EVIDGATE:Session:Pepper"] = Convert.ToBase64String(Pepper),
                // 02.3.1-T2.1: the same for the invite pepper (EVIDGATE__INVITE__PEPPER), so the deployment's
                // own binder verifies the hash a case seeded with this host's InvitePepper.
                ["EVIDGATE:Invite:Pepper"] = Convert.ToBase64String(InvitePepper),
                ["EVIDGATE:Invite:PepperVersion"] = _settings.InvitePepperVersion
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));

        builder.ConfigureTestServices(services =>
        {
            ArgumentNullException.ThrowIfNull(services);

            // Exactly the case's providers: the host's real ones are unconfigured anyway, and `Find` takes the
            // first provider registered under a name, so leaving them in would shadow a fake named `google`.
            services.RemoveAll<ILoginProvider>();
            foreach (var provider in _settings.Providers)
            {
                services.AddSingleton(provider);
            }

            // The two seams the login calls: a case substitutes one only to prove HOW it is called (the
            // ambient tenant of the bind, the fail-closed audit of 02.3.1-T2.2); the deployment's own stay
            // registered otherwise, so every other case asserts on the real path.
            _settings.Seams?.Invoke(services);

            if (_settings.Binder is { } binder)
            {
                services.RemoveAll<IMembershipBinder>();
                services.AddSingleton(binder);
            }

            if (_settings.AuditWriter is { } auditWriter)
            {
                services.RemoveAll<IIdentityAuditWriter>();
                services.AddSingleton(auditWriter);
            }

            if (_settings.RemoteIpAddress is { } remoteIpAddress)
            {
                services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(remoteIpAddress));
            }

            if (_settings.LogProvider is { } logProvider)
            {
                services.AddSingleton(logProvider);
            }
        });
    }

    /// <summary>Makes every request report <paramref name="remoteIpAddress"/> as its client address.</summary>
    private sealed class RemoteAddressStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteIpAddress;

                return nextMiddleware(context);
            });

            next(app);
        };
    }
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps every entry the host's own filters let through, so a suite can
/// assert on what the DEPLOYMENT would log rather than on what a hand-built logger would.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
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

    /// <summary>The logger a <see cref="CapturingLoggerProvider"/> hands out; every level is enabled.</summary>
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

/// <summary>
/// A double of the invite binder (02.1.2-T2) that records HOW the login calls it: the request handed over and
/// whether a tenant scope was already open at that moment (02.3.1-T2: the login must bind OUTSIDE one, since
/// the binder opens and commits one transaction per org itself).
/// </summary>
internal sealed class RecordingMembershipBinder : IMembershipBinder
{
    private readonly List<BinderCall> _calls = [];

    /// <summary>
    /// The ambient scope accessor, set by the case once the host's services exist; when it is null the call is
    /// recorded without an ambient observation.
    /// </summary>
    internal ITenantScopeAccessor? Scopes { get; set; }

    /// <summary>What to answer instead of an empty success, or null for "nothing bound".</summary>
    internal Error? Failure { get; set; }

    /// <summary>Every call, in the order the login made them.</summary>
    internal IReadOnlyList<BinderCall> Calls => _calls;

    /// <inheritdoc />
    public ValueTask<Result<BindPendingMembershipsResult>> BindAsync(
        BindPendingMembershipsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _calls.Add(new BinderCall(request, Scopes?.Current is not null));

        return ValueTask.FromResult(Failure is { } failure
            ? Result.Failure<BindPendingMembershipsResult>(failure)
            : Result.Success(new BindPendingMembershipsResult([])));
    }

    /// <summary>One recorded call and the ambient tenant scope the login had open at that moment.</summary>
    /// <param name="Request">Exactly what the login asked the binder to do.</param>
    /// <param name="AmbientTenantScope">True when a tenant scope was open, which the caller must never do.</param>
    internal sealed record BinderCall(BindPendingMembershipsRequest Request, bool AmbientTenantScope);
}

/// <summary>
/// A double of the identity audit writer (02.3.1-T2.2) that records every event instead of writing a chain,
/// so a case can prove which orgs a login audited — or that it audited nothing at all.
/// </summary>
internal sealed class RecordingAuditWriter : IIdentityAuditWriter
{
    private readonly List<RecordedWrite> _writes = [];

    /// <summary>Every write, in order.</summary>
    internal IReadOnlyList<RecordedWrite> Writes => _writes;

    /// <inheritdoc />
    public ValueTask WriteAsync(
        IdentityAuditEvent auditEvent,
        IReadOnlyCollection<Guid> orgIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(orgIds);

        _writes.Add(new RecordedWrite(auditEvent, [.. orgIds]));

        return ValueTask.CompletedTask;
    }

    /// <summary>One recorded write: the event and the orgs it was addressed to.</summary>
    /// <param name="AuditEvent">The action, user and provider.</param>
    /// <param name="OrgIds">The orgs the login resolved for the row.</param>
    internal sealed record RecordedWrite(IdentityAuditEvent AuditEvent, IReadOnlyList<Guid> OrgIds);
}

/// <summary>
/// A double of the identity audit writer that fails the way the producer does on a database fault or a busy
/// chain, so a case can prove the login is fail-closed: 503 <c>unavailable</c>, no session row, no cookie.
/// </summary>
internal sealed class FaultingAuditWriter : IIdentityAuditWriter
{
    private readonly Exception _fault;

    /// <summary>Creates the double over the fault it raises.</summary>
    /// <param name="fault">What the writer throws when the login reaches it.</param>
    internal FaultingAuditWriter(Exception fault) => _fault = fault;

    /// <summary>How many times the login reached the writer.</summary>
    internal int Calls { get; private set; }

    /// <inheritdoc />
    public ValueTask WriteAsync(
        IdentityAuditEvent auditEvent,
        IReadOnlyCollection<Guid> orgIds,
        CancellationToken cancellationToken)
    {
        Calls++;

        throw _fault;
    }
}
