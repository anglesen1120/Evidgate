using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login;
using Evidgate.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// A clock the test moves: the pending-login cookie's lifetime and the one-time <c>state</c> record are
/// both driven by <see cref="TimeProvider"/>, so an expiry is proven without waiting.
/// </summary>
internal sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    /// <summary>The instant every component reads until <see cref="Advance"/> moves it.</summary>
    public DateTimeOffset UtcNow { get; private set; } = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => UtcNow;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// A login-completion seam that signs in instead of handing the identity to the real completion: it records what the
/// protocol handed over and answers the redirect target (or a scripted failure), which is what
/// 02.3.1-T1.4's real implementation will do once it lands.
/// </summary>
internal sealed class FakeLoginCompletion : ILoginCompletion
{
    /// <summary>Where a successful completion sends the browser.</summary>
    public string Target { get; set; } = "/runs/42";

    /// <summary>A scripted failure (e.g. 403 <c>no_membership</c>), or null for success.</summary>
    public Error? Failure { get; set; }

    /// <summary>How many verified identities reached the seam.</summary>
    public int Calls { get; private set; }

    /// <summary>The last request, so a test can read the identity and the <c>returnUrl</c> it was given.</summary>
    public LoginCompletionRequest? Last { get; private set; }

    /// <inheritdoc />
    public ValueTask<Result<string>> CompleteAsync(LoginCompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Calls++;
        Last = request;

        return ValueTask.FromResult(Failure is { } failure ? Result.Failure<string>(failure) : Result.Success(Target));
    }
}

/// <summary>
/// A link seam for the callback-plumbing cases (02.3.1-T2 Q4): it records what the protocol handed over —
/// including the account the state cookie named — and answers a redirect target or a scripted failure, standing
/// in for the linking slice's implementation.
/// </summary>
internal sealed class FakeLinkCompletion : ILinkCompletion
{
    /// <summary>Where a successful link sends the browser.</summary>
    public string Target { get; set; } = "/settings/security";

    /// <summary>A scripted failure (e.g. the 409 of an identity that still belongs to a live account), or null.</summary>
    public Error? Failure { get; set; }

    /// <summary>How many verified identities reached this seam.</summary>
    public int Calls { get; private set; }

    /// <summary>The last request, so a case can read the identity, the link account and the staged token.</summary>
    public LoginCompletionRequest? Last { get; private set; }

    /// <inheritdoc />
    public ValueTask<Result<string>> CompleteAsync(LoginCompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Calls++;
        Last = request;

        return ValueTask.FromResult(Failure is { } failure ? Result.Failure<string>(failure) : Result.Success(Target));
    }
}

/// <summary>
/// The host the login protocol tests drive: the real composition (<c>Program</c> → <c>ApiHost</c>) with the
/// login options of a configured deployment, a clock the test moves, the fake providers it registers and —
/// where the case is about the completion — a completion seam that signs in.
/// </summary>
/// <remarks>
/// The host runs on the test's own migrated database: the rate limiter of the pre-tenant routes keeps its
/// windows there, so a 429 is the deployment's own decision. The replay set and the key ring stay in the test
/// process (see <see cref="InMemoryLoginSeams"/>), so replay expiry follows the clock the case moves.
/// </remarks>
internal sealed class LoginTestHost : WebApplicationFactory<Program>
{
    /// <summary>The <c>EVIDGATE:Login:PublicBaseUrl</c> the redirect URIs are built from.</summary>
    internal const string PublicBaseUrl = "https://app.evidgate.test";

    // One hour: a case's attempts must not cross into a fresh window while it runs.
    private const int RateLimitWindowSeconds = 3600;

    // Twenty: every protocol case stays well inside it, while the throttle cases can spend it and observe 429.
    private const int RateLimitAttempts = 20;

    private const int StateLifetimeSeconds = 600;

    private const int RetryAfterSeconds = 30;

    private readonly MutableClock _clock;
    private readonly ILoginProvider[] _providers;
    private readonly ILoginCompletion? _completion;
    private readonly ILinkCompletion? _linkCompletion;
    private readonly ILoggerProvider? _logs;
    private readonly string _connectionString;

    private LoginTestHost(
        DatabaseFixture fixture,
        MutableClock clock,
        ILoginProvider[] providers,
        ILoginCompletion? completion,
        ILinkCompletion? linkCompletion = null,
        ILoggerProvider? logs = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _connectionString = fixture.AppConnectionString;
        _clock = clock;
        _providers = providers;
        _completion = completion;
        _linkCompletion = linkCompletion;
        _logs = logs;
    }

    /// <summary>The running test's cancellation token, which every wait of a case passes on.</summary>
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A deployment with the given providers registered and the REAL completion. The protocol cases never reach
    /// it (they stop at a refusal or at their own fake seam), and the cases that do complete run on the
    /// <c>Login/Completion</c> host instead.
    /// </summary>
    /// <param name="fixture">The test's database, which the rate limiter spends its windows in.</param>
    internal static LoginTestHost Start(DatabaseFixture fixture, MutableClock clock, params ILoginProvider[] providers) =>
        new(fixture, clock, providers, completion: null);

    /// <summary>A deployment with the given providers and a completion seam that signs in.</summary>
    internal static LoginTestHost Start(
        DatabaseFixture fixture, MutableClock clock, ILoginCompletion completion, params ILoginProvider[] providers) =>
        new(fixture, clock, providers, completion);

    /// <summary>
    /// A deployment with a completion seam that signs in AND a link seam, so both halves of the callback's
    /// purpose dispatch (02.3.1-T2 Q4) can be observed on one host. The linking slice registers the real
    /// <see cref="ILinkCompletion"/>; until then a case substitutes it exactly like the completion.
    /// </summary>
    internal static LoginTestHost Start(
        DatabaseFixture fixture,
        MutableClock clock,
        ILoginCompletion completion,
        ILinkCompletion linkCompletion,
        params ILoginProvider[] providers) =>
        new(fixture, clock, providers, completion, linkCompletion);

    /// <summary>A deployment with no link seam at all: a link attempt must then fail closed, never sign in.</summary>
    internal static LoginTestHost Start(
        DatabaseFixture fixture, MutableClock clock, ILinkCompletion linkCompletion, params ILoginProvider[] providers) =>
        new(fixture, clock, providers, completion: null, linkCompletion);

    /// <summary>
    /// The same deployment plus an extra logger provider in its container, for the redaction cases
    /// (<c>RequestLogRedactionTests</c>' mechanism: the host's own filters apply to it as to the deployment's
    /// sinks).
    /// </summary>
    /// <param name="fixture">The test's migrated database, isolated from other clients' global windows.</param>
    /// <param name="clock">The clock the test moves.</param>
    /// <param name="logs">The provider to add.</param>
    /// <param name="completion">A completion seam that signs in.</param>
    /// <param name="providers">The providers to register.</param>
    internal static LoginTestHost StartWithLogs(
        DatabaseFixture fixture,
        MutableClock clock,
        ILoggerProvider logs,
        ILoginCompletion completion,
        params ILoginProvider[] providers) =>
        new(fixture, clock, providers, completion, linkCompletion: null, logs);

    /// <summary>An <c>start</c> request, optionally naming a <c>returnUrl</c>.</summary>
    internal static HttpRequestMessage StartRequest(string provider, string? returnUrl = null) =>
        new(
            HttpMethod.Get,
            "/v1/auth/oidc/" + provider + "/start"
            + (returnUrl is null ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));

    /// <summary>A <c>callback</c> request carrying whatever cookies the case presents.</summary>
    /// <param name="provider">The provider route value.</param>
    /// <param name="cookieValue">The raw <c>__Host-evidgate_login</c> value, or null for none.</param>
    /// <param name="state">The <c>state</c> query value, or null for none.</param>
    /// <param name="code">The <c>code</c> query value, or null for none.</param>
    /// <param name="error">The provider's <c>error</c> query value, or null for none.</param>
    /// <param name="inviteCookieValue">The raw <c>__Host-evidgate_invite</c> value a landing page staged, or
    /// null when the case staged none (02.3.1-T2 Q1).</param>
    internal static HttpRequestMessage CallbackRequest(
        string provider,
        string? cookieValue,
        string? state,
        string? code = "authorization-code",
        string? error = null,
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

        if (error is not null)
        {
            query.Add("error=" + Uri.EscapeDataString(error));
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
            cookies.Add(InviteTokenCookie.Name + "=" + inviteCookieValue);
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

    /// <summary>Sends one request with the case's token.</summary>
    internal static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        return client.SendAsync(request, Token);
    }

    /// <summary>Every <c>Set-Cookie</c> of the response that sets <c>__Host-evidgate_login</c>.</summary>
    internal static IReadOnlyList<string> StateCookies(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith(LoginStateCookie.Name + "=", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>The <c>name=value</c> the browser would send back, or null when the response set none.</summary>
    internal static string? StateCookieValue(HttpResponseMessage response)
    {
        var cookies = StateCookies(response);

        return cookies.Count == 0 ? null : cookies[0].Split(';', 2)[0];
    }

    /// <summary>The one <c>__Host-evidgate_login</c> deletion a callback must always emit.</summary>
    internal static string? StateCookieDeletion(HttpResponseMessage response) =>
        StateCookies(response).FirstOrDefault(cookie =>
            cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

    /// <summary>Unprotects the pending-login cookie with this host's own protector and serializer.</summary>
    internal (LoginStatePayload Payload, string CookieValue) PendingLogin(HttpResponseMessage start)
    {
        var pair = StateCookieValue(start)
            ?? throw new InvalidOperationException("the start response set no " + LoginStateCookie.Name + " cookie");
        var cookieValue = pair[(LoginStateCookie.Name.Length + 1)..];

        var protector = Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LoginStateCookie.ProtectorPurpose);

        var payload = JsonSerializer.Deserialize(
            protector.Unprotect(cookieValue),
            LoginStateJsonContext.Default.LoginStatePayload)
            ?? throw new InvalidOperationException("the pending-login cookie did not deserialize");

        return (payload, cookieValue);
    }

    /// <summary>Every <c>Set-Cookie</c> of the response that sets <c>__Host-evidgate_invite</c>.</summary>
    internal static IReadOnlyList<string> InviteCookies(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith(InviteTokenCookie.Name + "=", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>The one <c>__Host-evidgate_invite</c> deletion a callback must always emit.</summary>
    internal static string? InviteCookieDeletion(HttpResponseMessage response) =>
        InviteCookies(response).FirstOrDefault(cookie =>
            cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Protects an arbitrary pending-attempt payload with this host's own key ring, so a case can present a
    /// state cookie the current routes would never write: a link attempt (whose start route lands with a later
    /// slice) or a legacy payload of the previous shape.
    /// </summary>
    /// <param name="payload">The payload to protect.</param>
    internal string ProtectPending(LoginStatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return Protect(JsonSerializer.Serialize(payload, LoginStateJsonContext.Default.LoginStatePayload));
    }

    /// <summary>Protects a raw JSON body as a pending-attempt cookie value (the legacy-cookie cases).</summary>
    /// <param name="json">The payload JSON exactly as the case wrote it.</param>
    internal string ProtectRawPending(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return Protect(json);
    }

    /// <summary>
    /// Protects a raw JSON body as a staged-invite cookie value, so a case can present a token the route would
    /// have staged without posting it first.
    /// </summary>
    /// <param name="token">The invite token to stage.</param>
    /// <param name="issuedAt">When the staging happened, for the lifetime check.</param>
    internal string ProtectInvite(string token, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(token);

        var json = JsonSerializer.Serialize(
            new InviteTokenPayload(InviteTokenCookie.PayloadVersion, token, issuedAt),
            InviteTokenJsonContext.Default.InviteTokenPayload);

        return Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(InviteTokenCookie.ProtectorPurpose)
            .Protect(json);
    }

    /// <summary>Protects a payload JSON with this host's pending-login protector.</summary>
    /// <param name="json">The payload JSON.</param>
    private string Protect(string json) =>
        Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(LoginStateCookie.ProtectorPurpose)
            .Protect(json);

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

    /// <summary>The raw body text, for the assertions that a provider value never reaches the wire.</summary>
    internal static Task<string> BodyAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Content.ReadAsStringAsync(Token);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
                ["EVIDGATE:Identity:RateLimits:LoginLimit"] =
                    RateLimitAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Identity:RateLimits:LoginWindowSeconds"] =
                    RateLimitWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Identity:RateLimits:LinkLimit"] =
                    RateLimitAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Identity:RateLimits:LinkWindowSeconds"] =
                    RateLimitWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Identity:RateLimits:InviteLimit"] =
                    RateLimitAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Identity:RateLimits:InviteWindowSeconds"] =
                    RateLimitWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
            }));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            InMemoryLoginSeams.Apply(services, _clock);

            foreach (var provider in _providers)
            {
                services.AddSingleton<ILoginProvider>(provider);
            }

            if (_completion is not null)
            {
                services.AddSingleton<ILoginCompletion>(_completion);
            }

            // 02.3.1-T2 Q4: the link seam, which the callback resolves only for a link-purpose state. The module
            // registers the real one (02.3.2-T1 composed it, since its store now resolves the session itself), so
            // "this host has no link seam" — the composition gap the callback must fail closed on — is expressed
            // by REMOVING it rather than by never adding one.
            if (_linkCompletion is not null)
            {
                services.AddSingleton<ILinkCompletion>(_linkCompletion);
            }
            else
            {
                services.RemoveAll<ILinkCompletion>();
            }

            // Added to the container, so the host's own logging filters apply to it exactly as they do to the
            // deployment's sinks (the RequestLogRedactionTests mechanism).
            if (_logs is not null)
            {
                services.AddSingleton(_logs);
            }
        });
    }
}
