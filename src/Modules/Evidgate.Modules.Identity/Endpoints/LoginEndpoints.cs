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
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The federated login protocol routes (02.3.1 §5):
/// <c>GET /v1/auth/oidc/{provider}/start</c> and <c>GET /v1/auth/oidc/{provider}/callback</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both are PRE-TENANT (00 §5.4.8, the same shape as <c>CiTokenEndpoints</c>): the browser has no session
/// yet, so neither route takes a tenant, a principal, a permission or the tenant-resolution filter — only
/// the 00 §9.2 envelope filter. Neither route runs inside a tenant scope or a transaction of its own: the
/// callback makes its provider calls before anything touches the database (P5) and hands the verified identity to
/// <see cref="ILoginCompletion"/>, which owns the writes — the user/identity upsert, the active-membership
/// check, the <c>session</c> row and the <c>__Host-evidgate_session</c> cookie.
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
/// Both routes are throttled per client address before any other work, from the login budget the shared
/// limiter keeps (<see cref="ExchangeRateLimiter"/>, <c>EVIDGATE__IDENTITY__RATELIMITS__LOGIN__*</c>) with its 429
/// <c>rate_limited</c> + <c>Retry-After</c> envelope: an anonymous flood is what fills the one-time
/// <c>state</c> set and drives outbound provider calls, so a single client cannot spend the platform's
/// login capacity for everyone else.
/// </para>
/// <para>
/// <b>Invite staging (Q1).</b> The same group carries <c>POST /v1/auth/invite-token</c>
/// (<see cref="InviteTokenEndpoints"/>), which stages a validated invite token in the signed-in-browser's
/// <see cref="InviteTokenCookie"/>. The callback deletes that cookie on every outcome — next to the state
/// cookie, before any other work — reads the token once, and hands it to the completion as
/// <see cref="LoginCompletionRequest.InviteToken"/>. The value never reaches a URL, a <c>Location</c>, a log
/// line, a metric, an error body or the session row.
/// </para>
/// <para>
/// <b>Explicit linking (Q4).</b> A state cookie whose purpose is
/// <see cref="LoginStatePayload.PurposeLink"/> names the account a second provider is being added to
/// (<see cref="LoginStatePayload.LinkUserId"/>) and is completed ONLY by <see cref="ILinkCompletion"/>: the
/// callback never falls back to <see cref="ILoginCompletion"/> for it, and answers 503 <c>unavailable</c> when
/// no link seam is composed. Both start routes build the authorization URL through the one
/// <see cref="BeginAuthorization"/> helper, so a link attempt and a sign-in cannot drift apart in cookie,
/// PKCE or failure handling.
/// </para>
/// </remarks>
public static partial class LoginEndpoints
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
    /// The client-address limiter key of <c>start</c>: the shared limiter's address key
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
    /// provider, so it spends twice a <c>start</c> and is therefore twice as strict inside the one
    /// <c>login</c> budget the limiter applies to every key.
    /// </summary>
    private const int CallbackCost = 2;

    /// <summary>Maps the login routes and returns the group (only the envelope filter applies: there is no tenant yet).</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapLoginEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // One group for the whole /v1/auth surface: the invite staging route must carry exactly
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

        // The explicit second-provider link (Q4). The same group, so the four linking routes carry
        // exactly the pre-tenant composition of the protocol routes (envelope filter only, no tenant filter).
        LinkEndpoints.Map(group);

        // Session management and logout, plus the administrative bulk revoke. The same group, so these
        // routes carry exactly the pre-tenant composition of the protocol routes (envelope filter only, no tenant
        // filter): the acting user is the session cookie, resolved by the authenticator, and never a route value -
        // the bulk revoke's user segment names the member whose sessions end, not the caller.
        SessionEndpoints.Map(group);

        return group;
    }

    /// <summary>
    /// <c>start</c>: a 429 once the client's window is spent, a 404 for an unknown provider, a 503 when the
    /// provider or the deployment's public base URL is missing, otherwise the pending-login cookie plus a
    /// 302 to the provider's authorization URL. No outbound call: the only I/O is the shared attempt budget.
    /// </summary>
    private static async Task<Result> Start(
        HttpContext context,
        string provider,
        IEnumerable<ILoginProvider> providers,
        LoginStateCookie cookie,
        ExchangeRateLimiter limiter,
        IOptions<LoginOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        context.Response.Headers.CacheControl = NoStore;

        // First, before any other work: the per-client budget is what keeps one anonymous caller from
        // spending the platform's login capacity (see the class remarks).
        if (await ThrottleAsync(context, limiter, StartLimiterPrefix + ClientIp.LimiterKey(context), StartCost, cancellationToken)
                .ConfigureAwait(false) is { } throttled)
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
    /// explicit second-provider link alike (Q4). One helper, so the two start routes cannot drift
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

        cookie.Write(context, NewPending(provider.Name, state, nonce, verifier, returnUrl, issuedAt, linkUserId));

        return Result.Success(authorization);
    }
    /// <summary>The pending-attempt payload; a link names its account and so changes the purpose (see <see cref="BeginAuthorization"/>).</summary>
    private static LoginStatePayload NewPending(
        string provider, string state, string nonce, string verifier, string returnUrl, DateTimeOffset issuedAt, Guid? linkUserId) =>
        new(
            LoginStateCookie.PayloadVersion,
            provider,
            state,
            nonce,
            verifier,
            returnUrl,
            issuedAt,
            linkUserId is null ? LoginStatePayload.PurposeLogin : LoginStatePayload.PurposeLink,
            linkUserId);

    /// <summary>
    /// Spends up to <paramref name="cost"/> units of the client's window in the login budget and returns the
    /// 00 §9.2 429 <c>rate_limited</c> failure (with <c>Retry-After</c> set on the response) when the budget is
    /// gone, the 503 when the shared store cannot answer, or null to continue.
    /// </summary>
    /// <param name="context">The request whose response carries the refusal headers.</param>
    /// <param name="limiter">The shared attempt-budget store.</param>
    /// <param name="key">The limiter key, already prefixed by the route.</param>
    /// <param name="cost">Limiter units this attempt is worth.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    internal static ValueTask<Error?> ThrottleAsync(
        HttpContext context,
        ExchangeRateLimiter limiter,
        string key,
        int cost,
        CancellationToken cancellationToken) =>
        RateLimitGate.AcquireAsync(
            context, limiter, RateSurface.Login, key, cost, "too many sign-in attempts; retry later", cancellationToken);

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

    /// <summary>A fresh 256-bit secret as base64url (43 characters: a valid RFC 7636 verifier).</summary>
    private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The PKCE S256 challenge of <paramref name="verifier"/> (RFC 7636 §4.2).</summary>
    private static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>base64url without padding (RFC 7636 §A).</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
