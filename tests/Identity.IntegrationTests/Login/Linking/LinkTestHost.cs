using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Audit;
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
using Npgsql;
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
/// Two substitutions only. The providers (the real ones need live credentials and outbound calls; the
/// provider half is proven by the login suites' own fakes and mocked endpoints), and — in the cases that need
/// it — one service the case replaces through <c>overrides</c> (the identity audit writer or the link store),
/// so a fault or an outcome the database would rarely produce is injected at its seam instead of being
/// simulated by editing rows mid-request. Everything else is the deployment's: the routes, the envelope
/// filter, the session store, the cookie protection and the database.
/// </para>
/// <para>
/// The connection string is the harness's APP role (00 §5.4.3), so the linking path runs with the privileges
/// it has in production; assertions about stored rows read them back through the owner connection, because
/// the app role holds no privilege on the platform identity tables at all (0041).
/// </para>
/// </remarks>
internal sealed class LinkTestHost : WebApplicationFactory<Program>
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
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

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
    /// <param name="sessionCookie">The <c>__Host-evidgate_session</c> value of the signed-in user.</param>
    /// <param name="returnUrl">The optional body <c>return_url</c>, or null for no body at all.</param>
    internal async Task<(HttpResponseMessage Response, LoginStatePayload Payload, string StateCookie)> StartLinkAsync(
        HttpClient client,
        string provider,
        string sessionCookie,
        string? returnUrl = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(sessionCookie);

        using var request = Request(HttpMethod.Post, "/v1/auth/link/" + provider);
        request.Headers.Add("Cookie", SessionCookieName + "=" + sessionCookie);

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
    /// <param name="sessionCookie">The caller's session cookie, or null to send none.</param>
    internal static async Task<HttpResponseMessage> LinkCallbackAsync(
        HttpClient client,
        string provider,
        string stateCookie,
        string state,
        string? sessionCookie)
    {
        var request = CallbackRequest(provider, stateCookie, state);

        if (sessionCookie is not null)
        {
            request.Headers.Add("Cookie", SessionCookieName + "=" + sessionCookie);
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
        string? origin = null)
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

        return await client.SendAsync(request, Token).ConfigureAwait(false);
    }

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

    /// <summary>The raw body text, for the assertions that nothing sensitive reaches the wire.</summary>
    internal static Task<string> BodyAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Content.ReadAsStringAsync(Token);
    }

    /// <summary>A JSON member of the response body, or null when it is absent.</summary>
    internal static async Task<string?> MemberAsync(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync(Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);

        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(name, out var member)
            ? member.ToString()
            : null;
    }

    // ------------------------------------------------------------------ rows (owner seam)

    /// <summary>The <c>user.id</c> of <c>(provider, subject)</c>, read as the owner (the app role cannot).</summary>
    internal async Task<Guid?> UserIdAsync(string provider, string subject) =>
        await OwnerScalarAsync(
            "select user_id from public.user_identity where provider = @provider and subject = @subject",
            [("provider", provider), ("subject", subject)]).ConfigureAwait(false) is Guid id
            ? id
            : null;

    /// <summary>The <c>email_at_provider</c> of <c>(provider, subject)</c>, read as the owner.</summary>
    internal async Task<string?> IdentityEmailAsync(string provider, string subject) =>
        await OwnerScalarAsync(
            "select email_at_provider from public.user_identity where provider = @provider and subject = @subject",
            [("provider", provider), ("subject", subject)]).ConfigureAwait(false) as string;

    /// <summary>How many rows <paramref name="sql"/> counts, read as the owner.</summary>
    /// <param name="sql">The counting query.</param>
    /// <param name="values">Its parameters.</param>
    internal async Task<long> CountAsync(string sql, params (string Name, object Value)[] values) =>
        (long)(await OwnerScalarAsync(sql, values).ConfigureAwait(false))!;

    /// <summary>
    /// The org's audit rows oldest first, read through a real read-only tenant scope, so the RLS policy of
    /// <c>audit_log</c> applies exactly as it does for the deployment.
    /// </summary>
    /// <param name="orgId">The org whose chain is read.</param>
    internal async Task<IReadOnlyList<AuditRow>> AuditRowsAsync(Guid orgId)
    {
        await using var scope = await Fixture
            .BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token)
            .ConfigureAwait(false);

        return await scope.Db.QueryAsync<AuditRow>(
            """
            select action as "Action"
                 , object_id::text as "ObjectId"
                 , coalesce(metadata, '{}'::jsonb)::text as "MetadataJson"
              from public.audit_log
             order by seq
            """,
            cancellationToken: Token).ConfigureAwait(false);
    }

    /// <summary>Runs one statement as the owner (arranging the boundary of a case).</summary>
    internal Task ArrangeAsync(string sql, params (string Name, object Value)[] values) =>
        Fixture.SeedAsync(sql, values, Token);

    /// <summary>
    /// Seeds one active membership through the owner seam, which is how a case gives its signed-in user the
    /// membership a session requires (00 §5.4.8: no session without one).
    /// </summary>
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

    /// <summary>One <c>audit_log</c> row as these suites assert on it.</summary>
    /// <param name="Action">The action, e.g. <c>auth.link</c>.</param>
    /// <param name="ObjectId">The audited object id as text.</param>
    /// <param name="MetadataJson">The allowlisted metadata, as JSON text.</param>
    internal sealed record AuditRow(string Action, string? ObjectId, string? MetadataJson);

    // ------------------------------------------------------------------ wiring

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
                ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
                ["EVIDGATE:Session:Pepper"] = Convert.ToBase64String(Pepper),
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

            if (_logProvider is { } logProvider)
            {
                services.AddSingleton(logProvider);
            }

            _overrides?.Invoke(services);
        });
    }

    /// <summary>The harness the host runs against (needed by the row helpers).</summary>
    internal required DatabaseFixture Fixture { get; init; }

    /// <summary>One scalar read as the owner connection (the only role that may read the identity tables).</summary>
    /// <param name="sql">The query.</param>
    /// <param name="values">Its parameters.</param>
    private async Task<object?> OwnerScalarAsync(string sql, IReadOnlyList<(string Name, object Value)> values)
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

    private static LinkTestHost Start(
        DatabaseFixture fixture,
        ILoggerProvider? logProvider,
        Action<IServiceCollection>? overrides,
        params FakeLoginProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new LinkTestHost(fixture.AppConnectionString, providers, logProvider, overrides) { Fixture = fixture };
    }

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("User-Agent", UserAgent);

        return request;
    }

    private static HttpRequestMessage CallbackRequest(string provider, string? cookieValue, string? state)
    {
        var query = new List<string>();
        if (state is not null)
        {
            query.Add("state=" + Uri.EscapeDataString(state));
        }

        query.Add("code=authorization-code");

        var request = Request(
            HttpMethod.Get,
            "/v1/auth/oidc/" + provider + "/callback?" + string.Join("&", query));

        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", LoginStateCookie.Name + "=" + cookieValue);
        }

        return request;
    }

    private static StringContent JsonContent(object body) =>
        new StringContent(
            JsonSerializer.Serialize(body),
            System.Text.Encoding.UTF8,
            "application/json");

    /// <summary>
    /// An <see cref="ILoggerProvider"/> that keeps every entry the host's own filters let through, so a suite
    /// can assert on what the DEPLOYMENT would log rather than on what a hand-built logger would.
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
}

/// <summary>
/// The production <see cref="IdentityAuditWriter"/> behind a fault switch (02.3.1-T2.2): the first
/// <see cref="FailingWrites"/> calls raise a database exception and nothing else changes, so a case can prove
/// the confirm route's fail-closed answer AND its convergence — the retry of the same staged link reaches the
/// real writer and re-audits.
/// </summary>
internal sealed class FaultingAuditWriter : IIdentityAuditWriter
{
    private readonly IIdentityAuditWriter _inner;
    private int _remaining;

    /// <summary>Wraps <paramref name="inner"/>, failing its first <paramref name="failingWrites"/> calls.</summary>
    /// <param name="inner">The writer that answers once the fault budget is spent.</param>
    /// <param name="failingWrites">How many calls raise before the real writer is reached; 0 starts disarmed.</param>
    public FaultingAuditWriter(IIdentityAuditWriter inner, int failingWrites)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(failingWrites);

        _inner = inner;
        _remaining = failingWrites;
    }

    /// <summary>
    /// Rearms the fault for the next <paramref name="writes"/> calls. A case must arm AFTER it has arranged its
    /// session, because the deployment's sign-in path writes its own <c>auth.login</c> rows through this same
    /// registration — a host-armed fault would turn the member's sign-in into the 503 instead of the confirm.
    /// </summary>
    /// <param name="writes">How many calls raise from now on.</param>
    internal void Arm(int writes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(writes);

        Interlocked.Exchange(ref _remaining, writes);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(
        IdentityAuditEvent auditEvent,
        IReadOnlyCollection<Guid> orgIds,
        CancellationToken cancellationToken)
    {
        if (_remaining > 0)
        {
            _remaining--;

            // The type the route's fail-closed path catches: an audit chain that cannot commit looks exactly
            // like this to the caller.
            throw new NpgsqlException("injected audit fault (02.3.1-T2.2 test)");
        }

        return _inner.WriteAsync(auditEvent, orgIds, cancellationToken);
    }
}

/// <summary>
/// The real identity audit writer over the host's own collaborators, so a case that replaces
/// <see cref="IIdentityAuditWriter"/> still writes through the production path.
/// </summary>
internal static class AuditWriterFactory
{
    /// <summary>Builds the production writer from the container's registrations.</summary>
    /// <param name="provider">The host's service provider.</param>
    internal static IIdentityAuditWriter Create(IServiceProvider provider) =>
        new IdentityAuditWriter(
            provider.GetRequiredService<ITenantScopeFactory>(),
            provider.GetRequiredService<ITenantDb>(),
            provider.GetRequiredService<IAuditTrail>());
}
