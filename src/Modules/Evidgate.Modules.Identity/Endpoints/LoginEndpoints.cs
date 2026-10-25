using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The federated login protocol routes (02.3.1 §5, 02.3.1-T1.1):
/// <c>GET /v1/auth/oidc/{provider}/start</c> and <c>GET /v1/auth/oidc/{provider}/callback</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both are PRE-TENANT (00 §5.4.8, the same shape as <c>CiTokenEndpoints</c>): the browser has no session
/// yet, so neither route takes a tenant, a principal, a permission or the tenant-resolution filter — only
/// the 00 §9.2 envelope filter. Neither touches the database: the callback makes its provider calls before
/// anything could open a transaction (P5) and hands the verified identity to
/// <see cref="ILoginCompletion"/>, which owns the writes (02.3.1-T1.4).
/// </para>
/// <para>
/// <c>start</c> writes the pending-login cookie (<see cref="LoginStateCookie"/>) and redirects to the
/// provider with <c>state</c>, a nonce and the PKCE S256 challenge; <c>callback</c> always deletes that
/// cookie, verifies the one-time <c>state</c> against it in constant time, refuses a replay, and maps the
/// provider's typed outcome to 401 <c>token_invalid</c> or 503 <c>unavailable</c> + <c>Retry-After</c>
/// (AC8). No provider text, code, token, nonce, verifier, email or subject is ever logged or echoed.
/// </para>
/// </remarks>
public static class LoginEndpoints
{
    /// <summary>Both routes' response cache policy: a login answer is never reusable.</summary>
    private const string NoStore = "no-store";

    /// <summary>Largest authorization code accepted from a provider redirect (02.3.1 §8 bounds the input).</summary>
    private const int MaxCodeLength = 2048;

    /// <summary>
    /// Longest user agent stored on the session row: <c>session_user_agent_length_check</c> allows at most
    /// 512 characters (migrations/0041_identity_user.sql), so the value is bounded here, where client input
    /// is read, and the database check stays a backstop.
    /// </summary>
    private const int MaxUserAgentLength = 512;

    /// <summary>Entropy of <c>state</c>, the nonce and the PKCE verifier, in bytes (32 bytes → 43 base64url characters).</summary>
    private const int SecretBytes = 32;

    /// <summary>Telemetry outcome of a start that redirected to the provider.</summary>
    private const string OutcomeStarted = "started";

    /// <summary>Telemetry outcome of a callback whose completion answered a redirect.</summary>
    private const string OutcomeCompleted = "completed";

    /// <summary>
    /// Telemetry provider tag of a route value no provider is registered under. The raw route value is
    /// client-controlled, so it never becomes a metric label; the outcome code carries the cause.
    /// </summary>
    private const string UnknownProviderTag = "unknown";

    /// <summary>Maps both routes and returns the group (only the envelope filter applies: there is no tenant yet).</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapLoginEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/auth/oidc").WithTags("identity");

        group.MapGet("/{provider}/start", Start)
            .Produces(StatusCodes.Status302Found)
            .WithSummary("Start a federated login: set the state/nonce/PKCE cookie and redirect to the provider (02.3.1)");

        group.MapGet("/{provider}/callback", CallbackAsync)
            .Produces(StatusCodes.Status302Found)
            .WithSummary("Provider callback: verify the one-time state, exchange the code with PKCE and complete the login (02.3.1)");

        return group;
    }

    /// <summary>
    /// <c>start</c>: a 404 for an unknown provider, a 503 when the provider or the deployment's public base
    /// URL is missing, otherwise the pending-login cookie plus a 302 to the provider's authorization URL.
    /// Synchronous by design: it makes no outbound call.
    /// </summary>
    private static Result Start(
        HttpContext context,
        string provider,
        IEnumerable<ILoginProvider> providers,
        LoginStateCookie cookie,
        IOptions<LoginOptions> options,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        context.Response.Headers.CacheControl = NoStore;

        var login = options.Value;
        var selected = Find(providers, provider);

        if (selected is null)
        {
            EvidgateTelemetry.RecordLoginAttempt(UnknownProviderTag, ApiErrorCode.NotFound);

            return Result.Failure(LoginErrors.UnknownProvider);
        }

        var callbackUri = login.CallbackUri(selected.Name);
        if (!selected.IsConfigured || callbackUri is null)
        {
            return LoginErrors.Unavailable(context, selected.Name, login);
        }

        var state = NewSecret();
        var nonce = NewSecret();
        var verifier = NewSecret();

        cookie.Write(
            context,
            new LoginStatePayload(
                LoginStateCookie.PayloadVersion,
                selected.Name,
                state,
                nonce,
                verifier,
                ReturnUrlPolicy.Resolve(context.Request.Query["returnUrl"]),
                clock.GetUtcNow()));

        Uri authorization;
        try
        {
            authorization = selected.BuildAuthorizationUrl(
                new LoginAuthorizationRequest(state, nonce, Challenge(verifier), callbackUri));
        }
        catch (ArgumentException)
        {
            // A provider whose own configuration cannot build a URL is unusable, not a bad request: the
            // cookie is already written and will simply expire unless this attempt is retried.
            return LoginErrors.Unavailable(context, selected.Name, login);
        }

        EvidgateTelemetry.RecordLoginAttempt(selected.Name, OutcomeStarted);

        return Result.Success<IResult>(Results.Redirect(authorization.AbsoluteUri), StatusCodes.Status302Found);
    }

    /// <summary>
    /// <c>callback</c>: one use of one pending login. The cookie is deleted on every outcome, the
    /// <c>state</c> is compared in constant time and recorded as consumed before the provider is called,
    /// and the provider's typed failure becomes 401 or 503 without any provider text.
    /// </summary>
    private static async Task<Result> CallbackAsync(
        HttpContext context,
        string provider,
        IEnumerable<ILoginProvider> providers,
        LoginStateCookie cookie,
        LoginReplayCache replay,
        ILoginCompletion completion,
        IOptions<LoginOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(options);

        context.Response.Headers.CacheControl = NoStore;

        // Read the pending login and delete the cookie first, whatever happens next: one attempt, one use.
        var presented = context.Request.Cookies[LoginStateCookie.Name];
        LoginStateCookie.Delete(context);

        var login = options.Value;
        var selected = Find(providers, provider);

        if (selected is null)
        {
            EvidgateTelemetry.RecordLoginAttempt(UnknownProviderTag, ApiErrorCode.NotFound);

            return Result.Failure(LoginErrors.UnknownProvider);
        }

        var pending = cookie.Unprotect(presented);
        if (pending is null || !string.Equals(pending.Provider, selected.Name, StringComparison.Ordinal))
        {
            return LoginErrors.Invalid(selected.Name);
        }

        if (!FixedTimeEquals(pending.State, context.Request.Query["state"].ToString()))
        {
            return LoginErrors.Invalid(selected.Name);
        }

        // The provider's own `error` value is never echoed: it can carry the code or a description.
        if (context.Request.Query.ContainsKey("error"))
        {
            return LoginErrors.Invalid(selected.Name);
        }

        var code = context.Request.Query["code"].ToString();
        if (code.Length is 0 or > MaxCodeLength)
        {
            return LoginErrors.Invalid(selected.Name);
        }

        var callbackUri = login.CallbackUri(selected.Name);
        if (!selected.IsConfigured || callbackUri is null)
        {
            return LoginErrors.Unavailable(context, selected.Name, login);
        }

        // Recorded before the provider call: a replayed callback must be refused even if the first attempt
        // failed, and two concurrent replays must not both reach the provider.
        if (!replay.TryConsume(selected.Name + ":" + pending.State, login.StateLifetime))
        {
            return LoginErrors.Invalid(selected.Name);
        }

        LoginProviderResult verified;
        try
        {
            verified = await selected
                .CompleteAsync(new LoginCallbackRequest(code, pending.Verifier, pending.Nonce, callbackUri), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A provider timeout that escaped its own handling is an outage, not a bad callback (AC8).
            return LoginErrors.Unavailable(context, selected.Name, login);
        }
        catch (HttpRequestException)
        {
            return LoginErrors.Unavailable(context, selected.Name, login);
        }
        catch (TimeoutException)
        {
            return LoginErrors.Unavailable(context, selected.Name, login);
        }

        if (verified.Failure == LoginFailure.ProviderUnavailable)
        {
            return LoginErrors.Unavailable(context, selected.Name, login);
        }

        if (verified.Failure is not null
            || verified.Identity is not { } identity
            || !string.Equals(identity.Provider, selected.Name, StringComparison.Ordinal))
        {
            return LoginErrors.Invalid(selected.Name);
        }

        var completed = await completion
            .CompleteAsync(
                new LoginCompletionRequest(
                    identity,
                    pending.ReturnUrl,
                    context.Connection.RemoteIpAddress?.ToString(),
                    UserAgent(context),
                    context),
                cancellationToken)
            .ConfigureAwait(false);

        if (!completed.IsSuccess)
        {
            EvidgateTelemetry.RecordLoginAttempt(selected.Name, completed.Error!.Code);

            return completed;
        }

        EvidgateTelemetry.RecordLoginAttempt(selected.Name, OutcomeCompleted);

        return Result.Success<IResult>(Results.Redirect(completed.Value), StatusCodes.Status302Found);
    }

    /// <summary>The provider registered under exactly <paramref name="provider"/> (lower case, ordinal), or null.</summary>
    private static ILoginProvider? Find(IEnumerable<ILoginProvider> providers, string? provider)
    {
        if (providers is null || string.IsNullOrEmpty(provider))
        {
            return null;
        }

        foreach (var candidate in providers)
        {
            if (string.Equals(candidate.Name, provider, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The client's user agent, or null when it sent none (session metadata, never logged). Truncated to
    /// <see cref="MaxUserAgentLength"/> — and never mid-surrogate-pair — so what the completion hands the
    /// session row always satisfies the column's length check.
    /// </summary>
    private static string? UserAgent(HttpContext context)
    {
        if (context.Request.Headers.UserAgent.Count == 0)
        {
            return null;
        }

        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (userAgent.Length <= MaxUserAgentLength)
        {
            return userAgent;
        }

        var truncated = userAgent[..MaxUserAgentLength];

        return char.IsHighSurrogate(truncated[^1]) ? truncated[..^1] : truncated;
    }

    /// <summary>A fresh 256-bit secret as base64url (43 characters: a valid RFC 7636 verifier).</summary>
    private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The PKCE S256 challenge of <paramref name="verifier"/> (RFC 7636 §4.2).</summary>
    private static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>base64url without padding (RFC 7636 §A).</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Constant-time comparison of the stored and presented <c>state</c> (never a plain string equality).</summary>
    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
}
