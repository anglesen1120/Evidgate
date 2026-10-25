using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// A login-completion seam that signs in instead of answering the V1 default 501: it records what the
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
/// The host the login protocol tests drive: the real composition (<c>Program</c> → <c>ApiHost</c>) with the
/// login options of a configured deployment, a clock the test moves, the fake providers it registers and —
/// where the case is about the completion — a completion seam that signs in.
/// </summary>
/// <remarks>
/// Every path these suites exercise is answered before a handler could reach PostgreSQL, so the connection
/// string points at a closed port and the boot-time isolation self-check is told so — the same two keys
/// <c>tests/Contract.Tests</c> sets (02.6.1-T1.4), never a skipped check.
/// </remarks>
internal sealed class LoginTestHost : WebApplicationFactory<Program>
{
    /// <summary>The <c>EVIDGATE:Login:PublicBaseUrl</c> the redirect URIs are built from.</summary>
    internal const string PublicBaseUrl = "https://app.evidgate.test";

    private const string NoDatabase =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    private const int StateLifetimeSeconds = 600;

    private const int RetryAfterSeconds = 30;

    private readonly MutableClock _clock;
    private readonly ILoginProvider[] _providers;
    private readonly ILoginCompletion? _completion;

    private LoginTestHost(MutableClock clock, ILoginProvider[] providers, ILoginCompletion? completion)
    {
        _clock = clock;
        _providers = providers;
        _completion = completion;
    }

    /// <summary>The running test's cancellation token, which every wait of a case passes on.</summary>
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A deployment with the given providers registered and the V1 default (501) completion.</summary>
    internal static LoginTestHost Start(MutableClock clock, params ILoginProvider[] providers) =>
        new(clock, providers, completion: null);

    /// <summary>A deployment with the given providers and a completion seam that signs in.</summary>
    internal static LoginTestHost Start(MutableClock clock, ILoginCompletion completion, params ILoginProvider[] providers) =>
        new(clock, providers, completion);

    /// <summary>An <c>start</c> request, optionally naming a <c>returnUrl</c>.</summary>
    internal static HttpRequestMessage StartRequest(string provider, string? returnUrl = null) =>
        new(
            HttpMethod.Get,
            "/v1/auth/oidc/" + provider + "/start"
            + (returnUrl is null ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));

    /// <summary>A <c>callback</c> request carrying whatever cookie value the case presents.</summary>
    internal static HttpRequestMessage CallbackRequest(
        string provider,
        string? cookieValue,
        string? state,
        string? code = "authorization-code",
        string? error = null)
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

        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", LoginStateCookie.Name + "=" + cookieValue);
        }

        return request;
    }

    /// <summary>A client that neither follows the 302 nor keeps cookies, so each request is explicit.</summary>
    internal HttpClient CreateLoginClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

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
                ["EVIDGATE:Database:ConnectionString"] = NoDatabase,
                ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
            }));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);

            foreach (var provider in _providers)
            {
                services.AddSingleton<ILoginProvider>(provider);
            }

            if (_completion is not null)
            {
                services.AddSingleton<ILoginCompletion>(_completion);
            }
        });
    }
}
