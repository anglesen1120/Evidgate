using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Login;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
/// the 00 §9.2 envelope filter. Neither route runs inside a tenant scope or a transaction of its own: the
/// callback makes its provider calls before anything touches the database (P5) and hands the verified identity to
/// <see cref="ILoginCompletion"/>, which owns the writes — the user/identity upsert, the active-membership
/// check, the <c>session</c> row and the <c>__Host-evidgate_session</c> cookie (02.3.1-T1.4).
/// </para>
/// <para>
/// <c>start</c> writes the pending-login cookie (<see cref="LoginStateCookie"/>) and redirects to the
/// provider with <c>state</c>, a nonce and the PKCE S256 challenge; <c>callback</c> always deletes that
/// cookie, verifies the one-time <c>state</c> against it in constant time, refuses a replay, and maps the
/// provider's typed outcome to 401 <c>token_invalid</c> or 503 <c>unavailable</c> + <c>Retry-After</c>
/// (AC8). A verified identity then ends in a 302 — the allowlisted <c>returnUrl</c> — or in the completion's
/// own refusal (403 <c>no_membership</c>, 403 <c>account_disabled</c>, 503 <c>unavailable</c> + <c>Retry-After</c>).
/// No provider text, code, token, nonce, verifier, email or subject is ever logged or echoed.
/// </para>
/// <para>
/// Both routes are throttled per client address before any other work, with the limiter
/// <c>POST /v1/ci/token</c> already uses (<see cref="ExchangeRateLimiter"/>, the same
/// <c>EVIDGATE__CI__EXCHANGEBURSTLIMIT</c>/<c>…EXCHANGEWINDOWSECONDS</c> budget) and its 429
/// <c>rate_limited</c> + <c>Retry-After</c> envelope: an anonymous flood is what fills the one-time
/// <c>state</c> set and drives outbound provider calls, so a single client cannot spend the platform's
/// login capacity for everyone else.
/// </para>
/// <para>
/// <b>Invite staging (02.3.1-T2, Q1).</b> The same group carries <c>POST /v1/auth/invite-token</c>
/// (<see cref="InviteTokenEndpoints"/>), which stages a validated invite token in the signed-in-browser's
/// <see cref="InviteTokenCookie"/>. The callback deletes that cookie on every outcome — next to the state
/// cookie, before any other work — reads the token once, and hands it to the completion as
/// <see cref="LoginCompletionRequest.InviteToken"/>. The value never reaches a URL, a <c>Location</c>, a log
/// line, a metric, an error body or the session row.
/// </para>
/// <para>
/// <b>Explicit linking (02.3.1-T2, Q4).</b> A state cookie whose purpose is
/// <see cref="LoginStatePayload.PurposeLink"/> names the account a second provider is being added to
/// (<see cref="LoginStatePayload.LinkUserId"/>) and is completed ONLY by <see cref="ILinkCompletion"/>: the
/// callback never falls back to <see cref="ILoginCompletion"/> for it, and answers 503 <c>unavailable</c> when
/// no link seam is composed. Both start routes build the authorization URL through the one
/// <see cref="BeginAuthorization"/> helper, so a link attempt and a sign-in cannot drift apart in cookie,
/// PKCE or failure handling.
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

    /// <summary>
    /// The client-address limiter key of <c>start</c>: the CI exchange limiter's key
    /// (<see cref="ExchangeRateLimiter.ClientKey"/>) under a route prefix, so a flood of one route cannot
    /// spend the other's budget.
    /// </summary>
    private const string StartLimiterPrefix = "login-start:";

    /// <summary>The client-address limiter key of <c>callback</c>, the second of the two route budgets.</summary>
    private const string CallbackLimiterPrefix = "login-cb:";

    /// <summary>Limiter units one <c>start</c> spends from its client's window.</summary>
    private const int StartCost = 1;

    /// <summary>
    /// Limiter units one <c>callback</c> spends from its client's window. It is the route that calls the
    /// provider, so it spends twice a <c>start</c> and is therefore twice as strict inside the one shared
    /// <c>EVIDGATE__CI__EXCHANGEBURSTLIMIT</c> budget the limiter applies to every key.
    /// </summary>
    private const int CallbackCost = 2;

    /// <summary>Maps the login routes and returns the group (only the envelope filter applies: there is no tenant yet).</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapLoginEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // One group for the whole /v1/auth surface: the invite staging route of 02.3.1-T2 must carry exactly
        // the same pre-tenant composition (envelope filter only, no tenant filter) as the two protocol routes,
        // and a sibling group would be one more place for the two to drift apart.
        var group = routes.MapGroup("/v1/auth").WithTags("identity");

        // ASP.NET derives a response for the default status (200) from the delegate's return type whenever
        // the declared status is not 200 — and that type is the C8 `Result` wrapper, which is never the wire
        // shape (the host's ResultEndpointFilter renders it). The bodiless `.Produces(200)` below replaces
        // that inferred response, so `Result`/`Error`/`ErrorDetail` never reach the served OpenAPI document
        // (`Contract.Tests.ServedDocumentTests` diffs the served schema names against
        // `contracts/openapi.yaml`; the same shape as `CiAdminEndpoints`/`MembershipEndpoints`). The 302
        // stays the documented answer: the browser flow has no 200 body.
        group.MapGet("/oidc/{provider}/start", Start)
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Start a federated login: set the state/nonce/PKCE cookie and redirect to the provider (02.3.1)");

        group.MapGet("/oidc/{provider}/callback", CallbackAsync)
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Provider callback: verify the one-time state, exchange the code with PKCE and complete the login (02.3.1)");

        InviteTokenEndpoints.Map(group);

        // 02.3.1-T2.2 (Q4): the explicit second-provider link. The same group, so the four linking routes carry
        // exactly the pre-tenant composition of the protocol routes (envelope filter only, no tenant filter).
        LinkEndpoints.Map(group);

        return group;
    }

    /// <summary>
    /// <c>start</c>: a 429 once the client's window is spent, a 404 for an unknown provider, a 503 when the
    /// provider or the deployment's public base URL is missing, otherwise the pending-login cookie plus a
    /// 302 to the provider's authorization URL. Synchronous by design: it makes no outbound call.
    /// </summary>
    private static Result Start(
        HttpContext context,
        string provider,
        IEnumerable<ILoginProvider> providers,
        LoginStateCookie cookie,
        ExchangeRateLimiter limiter,
        IOptions<LoginOptions> options,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        context.Response.Headers.CacheControl = NoStore;

        // First, before any other work: the per-client budget is what keeps one anonymous caller from
        // spending the platform's login capacity (see the class remarks).
        if (Throttle(context, limiter, StartLimiterPrefix + ExchangeRateLimiter.ClientKey(context.Connection.RemoteIpAddress), StartCost) is { } throttled)
        {
            return Result.Failure(throttled);
        }

        var login = options.Value;
        var selected = Find(providers, provider);

        if (selected is null)
        {
            EvidgateTelemetry.RecordLoginAttempt(UnknownProviderTag, ApiErrorCode.NotFound);

            return Result.Failure(LoginErrors.UnknownProvider);
        }

        var returnUrl = ReturnUrlPolicy.Resolve(context.Request.Query["returnUrl"]);
        var began = BeginAuthorization(context, selected, cookie, options, clock, returnUrl, linkUserId: null);
        if (!began.IsSuccess)
        {
            return Result.Failure(began.Error!);
        }

        EvidgateTelemetry.RecordLoginAttempt(selected.Name, OutcomeStarted);

        return Result.Success<IResult>(Results.Redirect(began.Value.AbsoluteUri), StatusCodes.Status302Found);
    }

    /// <summary>
    /// Builds a provider's authorization URL and writes the pending-attempt cookie, for a sign-in and for an
    /// explicit second-provider link alike (02.3.1-T2, Q4). One helper, so the two start routes cannot drift
    /// apart in PKCE, cookie or failure handling.
    /// </summary>
    /// <param name="context">The request the cookie is written on.</param>
    /// <param name="provider">A registered provider; the caller has already resolved the route value.</param>
    /// <param name="cookie">The pending-login cookie store.</param>
    /// <param name="options">The bound login options (public base URL, retry-after, lifetimes).</param>
    /// <param name="clock">The clock the attempt is stamped with.</param>
    /// <param name="returnUrl">The already-allowlisted app path to land on (<see cref="ReturnUrlPolicy"/>).</param>
    /// <param name="linkUserId">The account a link is being started for, or null for a sign-in. It sets the
    /// cookie's purpose to <see cref="LoginStatePayload.PurposeLink"/> and is the ONLY thing that decides what
    /// the callback is allowed to complete the attempt with.</param>
    /// <returns>
    /// Success with the absolute authorization URL the route redirects to, or the 503 <c>unavailable</c> the
    /// route answers when the provider or the deployment's public base URL is unusable. No cookie is written
    /// on a failure.
    /// </returns>
    internal static Result<Uri> BeginAuthorization(
        HttpContext context,
        ILoginProvider provider,
        LoginStateCookie cookie,
        IOptions<LoginOptions> options,
        TimeProvider clock,
        string returnUrl,
        Guid? linkUserId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(returnUrl);

        var login = options.Value;
        var callbackUri = login.CallbackUri(provider.Name);
        if (!provider.IsConfigured || callbackUri is null)
        {
            return Result.Failure<Uri>(LoginErrors.Unavailable(context, provider.Name, login).Error!);
        }

        // A link names an account, a sign-in names none: anything else is a caller bug, refused before the
        // cookie exists rather than written as a state the callback would have to guess about.
        if (linkUserId == Guid.Empty)
        {
            throw new ArgumentException("a link names an account, never the empty id", nameof(linkUserId));
        }

        var state = NewSecret();
        var nonce = NewSecret();
        var verifier = NewSecret();
        var issuedAt = clock.GetUtcNow();

        Uri authorization;
        try
        {
            authorization = provider.BuildAuthorizationUrl(
                new LoginAuthorizationRequest(state, nonce, Challenge(verifier), callbackUri));
        }
        catch (ArgumentException)
        {
            // A provider whose own configuration cannot build a URL is unusable, not a bad request. The
            // cookie is written only after this point, so the failed attempt leaves nothing in the browser.
            return Result.Failure<Uri>(LoginErrors.Unavailable(context, provider.Name, login).Error!);
        }

        cookie.Write(
            context,
            new LoginStatePayload(
                LoginStateCookie.PayloadVersion,
                provider.Name,
                state,
                nonce,
                verifier,
                returnUrl,
                issuedAt,
                linkUserId is null ? LoginStatePayload.PurposeLogin : LoginStatePayload.PurposeLink,
                linkUserId));

        return Result.Success(authorization);
    }

    /// <summary>
    /// <c>callback</c>: one use of one pending login. Both cookies are deleted on every outcome - the 429
    /// included - and a spent window is answered right after those deletions and before either cookie is
    /// unprotected, the <c>state</c> is compared in constant time and recorded as consumed before the
    /// provider is called, and the provider's typed failure becomes 401 or 503 without any provider text.
    /// </summary>
    private static async Task<Result> CallbackAsync(
        HttpContext context,
        string provider,
        IEnumerable<ILoginProvider> providers,
        LoginStateCookie cookie,
        InviteTokenCookie inviteCookie,
        LoginReplayCache replay,
        ILoginCompletion completion,
        ExchangeRateLimiter limiter,
        IOptions<LoginOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(inviteCookie);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);

        context.Response.Headers.CacheControl = NoStore;

        // Read the pending login and delete BOTH cookies next, whatever happens after: one attempt, one use.
        // The staged invite token is a bearer secret (02.3.1-T2 Q1), so it leaves the browser on the first
        // callback that sees it, whether that callback succeeds, is refused or never reads it.
        var presented = context.Request.Cookies[LoginStateCookie.Name];
        LoginStateCookie.Delete(context);
        var stagedInvite = context.Request.Cookies[InviteTokenCookie.Name];
        InviteTokenCookie.Delete(context);

        // Then, still ahead of every expensive step: this is the route an anonymous flood uses to fill the
        // replay set and to drive outbound token exchanges, so it spends the client's window before the
        // cookie is unprotected, before the one-time record and before the provider call - while the
        // deletion above keeps "the cookie is deleted on every outcome" literally true.
        if (Throttle(context, limiter, CallbackLimiterPrefix + ExchangeRateLimiter.ClientKey(context.Connection.RemoteIpAddress), CallbackCost) is { } throttled)
        {
            return Result.Failure(throttled);
        }

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

        var request = new LoginCompletionRequest(
            identity,
            pending.ReturnUrl,
            context.Connection.RemoteIpAddress?.ToString(),
            UserAgent(context),
            context,
            inviteCookie.Read(stagedInvite),
            pending.LinkUserId);

        // The purpose the SIGNED state cookie carries decides which seam may complete this attempt, and only
        // that seam: a link is never completed by the login completion (it would bind a provider to an account
        // without the explicit confirm of AC6), and a sign-in is never completed by the link one. Unprotect
        // already refused every purpose but these two, so "not link" is "login" here.
        var completed = string.Equals(pending.Purpose, LoginStatePayload.PurposeLink, StringComparison.Ordinal)
            ? await CompleteLinkAsync(context, request, selected.Name, login, cancellationToken).ConfigureAwait(false)
            : await completion.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        if (!completed.IsSuccess)
        {
            EvidgateTelemetry.RecordLoginAttempt(selected.Name, completed.Error!.Code);

            return completed;
        }

        EvidgateTelemetry.RecordLoginAttempt(selected.Name, OutcomeCompleted);

        // The seam promises an app path, and the browser is sent here right after a successful sign-in:
        // this is the sink where a completion bug (a computed target, a provider value) would become an
        // open redirect, so the allowlist is applied once more to what actually leaves the server. The value
        // is already percent-encoded by the completion (ReturnUrlEncoder), so a non-ASCII path is a valid
        // ASCII Location and this check sees the bytes Kestrel writes.
        var target = ReturnUrlPolicy.IsAppPath(completed.Value) ? completed.Value : ReturnUrlPolicy.Root;

        return Result.Success<IResult>(Results.Redirect(target), StatusCodes.Status302Found);
    }

    /// <summary>
    /// Completes an explicit second-provider link through <see cref="ILinkCompletion"/> (02.3.1-T2, Q4) and
    /// answers the routes' existing 503 when no link seam is composed.
    /// </summary>
    /// <param name="context">The request, whose container holds the seam.</param>
    /// <param name="request">The verified identity and the account the link was started for.</param>
    /// <param name="provider">The registered provider name, for the outcome count (never a route value).</param>
    /// <param name="login">The bound login options (<c>Retry-After</c> of the 503).</param>
    /// <param name="cancellationToken">Request abort.</param>
    private static ValueTask<Result<string>> CompleteLinkAsync(
        HttpContext context,
        LoginCompletionRequest request,
        string provider,
        LoginOptions login,
        CancellationToken cancellationToken)
    {
        // Resolved here, not declared as an endpoint parameter: only this branch ever needs the seam, and a
        // host that composes none must still serve the sign-in routes. An absent seam fails closed with the
        // 503 these routes already answer for a composition gap — never by falling back to the login
        // completion, which would bind a second provider without the confirm AC6 requires.
        if (context.RequestServices.GetService<ILinkCompletion>() is not { } link)
        {
            return ValueTask.FromResult(
                Result.Failure<string>(LoginErrors.Unavailable(context, provider, login).Error!));
        }

        return link.CompleteAsync(request, cancellationToken);
    }

    /// <summary>
    /// Spends up to <paramref name="cost"/> units of the client's window in the CI exchange limiter and
    /// returns the 00 §9.2 429 <c>rate_limited</c> failure (with <c>Retry-After</c> set on the response) when
    /// the budget is gone, or null to continue. Same envelope and same <see cref="Error"/> shape as
    /// <c>CiTokenEndpoints.Throttle</c>. Only the first unit decides: the extra units of a route that must be
    /// stricter are spent from the same window when they are available and never refuse by themselves, so a
    /// legal <c>ExchangeBurstLimit</c> of 1 (or an odd one) cannot make the route unusable.
    /// </summary>
    private static Error? Throttle(HttpContext context, ExchangeRateLimiter limiter, string key, int cost)
    {
        if (!limiter.TryAcquire(key, out var retryAfter))
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

            return new Error(
                ApiErrorCode.RateLimited,
                "too many sign-in attempts; retry later",
                StatusCodes.Status429TooManyRequests,
                Retryable: true);
        }

        for (var unit = 1; unit < cost; unit++)
        {
            _ = limiter.TryAcquire(key, out _);
        }

        return null;
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
