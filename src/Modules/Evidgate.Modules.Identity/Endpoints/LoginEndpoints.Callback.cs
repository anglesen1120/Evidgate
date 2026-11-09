using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Login;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Endpoints;

// The callback half of the login routes: the one-use pending-login check, the provider exchange and the
// completion. Route registration and the start half stay in LoginEndpoints.cs.
public static partial class LoginEndpoints
{
    /// <summary>What a callback that passed every pre-provider check carries into the provider exchange.</summary>
    private readonly record struct CallbackInput(LoginStatePayload Pending, string Code, Uri CallbackUri);

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
        // The staged invite token is a bearer secret (Q1), so it leaves the browser on the first
        // callback that sees it, whether that callback succeeds, is refused or never reads it.
        var presented = context.Request.Cookies[LoginStateCookie.Name];
        LoginStateCookie.Delete(context);
        var stagedInvite = context.Request.Cookies[InviteTokenCookie.Name];
        InviteTokenCookie.Delete(context);

        // Ahead of every expensive step: an anonymous flood fills the replay set and drives provider calls here.
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

        if (CheckPending(context, selected, cookie, presented, login, replay, out var input) is { } refused)
        {
            return refused;
        }

        var verified = await VerifyAsync(context, selected, input, login, cancellationToken).ConfigureAwait(false);
        if (verified.Refusal is not null)
        {
            return verified.Refusal;
        }

        return await CompleteAsync(
            context, selected, input.Pending, verified.Identity!, inviteCookie.Read(stagedInvite), completion, login, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The pre-provider checks of a callback, in order: the pending login names this provider, the one-time
    /// <c>state</c> matches, no provider <c>error</c>, a bounded <c>code</c>, a configured provider, and the
    /// replay record. Returns the refusal, or null with <paramref name="input"/> set.
    /// </summary>
    private static Result? CheckPending(
        HttpContext context,
        ILoginProvider selected,
        LoginStateCookie cookie,
        string? presented,
        LoginOptions login,
        LoginReplayCache replay,
        out CallbackInput input)
    {
        input = default;

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

        input = new CallbackInput(pending, code, callbackUri);

        return null;
    }

    /// <summary>
    /// The code exchange: the verified identity, or the refusal (503 for an outage, 401 for a typed failure
    /// or an identity of another provider).
    /// </summary>
    private static async Task<(VerifiedIdentity? Identity, Result? Refusal)> VerifyAsync(
        HttpContext context,
        ILoginProvider selected,
        CallbackInput input,
        LoginOptions login,
        CancellationToken cancellationToken)
    {
        LoginProviderResult verified;
        try
        {
            verified = await selected
                .CompleteAsync(new LoginCallbackRequest(input.Code, input.Pending.Verifier, input.Pending.Nonce, input.CallbackUri), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A provider timeout that escaped its own handling is an outage, not a bad callback (AC8).
            return (null, LoginErrors.Unavailable(context, selected.Name, login));
        }
        catch (HttpRequestException)
        {
            return (null, LoginErrors.Unavailable(context, selected.Name, login));
        }
        catch (TimeoutException)
        {
            return (null, LoginErrors.Unavailable(context, selected.Name, login));
        }

        if (verified.Failure == LoginFailure.ProviderUnavailable)
        {
            return (null, LoginErrors.Unavailable(context, selected.Name, login));
        }

        if (verified.Failure is not null
            || verified.Identity is not { } identity
            || !string.Equals(identity.Provider, selected.Name, StringComparison.Ordinal))
        {
            return (null, LoginErrors.Invalid(selected.Name));
        }

        return (identity, null);
    }

    /// <summary>
    /// Hands the verified identity to the seam the signed pending login names, counts a sign-in outcome and
    /// maps success to the allowlisted 302.
    /// </summary>
    private static async Task<Result> CompleteAsync(
        HttpContext context,
        ILoginProvider selected,
        LoginStatePayload pending,
        VerifiedIdentity identity,
        string? inviteToken,
        ILoginCompletion completion,
        LoginOptions login,
        CancellationToken cancellationToken)
    {
        var request = new LoginCompletionRequest(
            identity,
            pending.ReturnUrl,
            context.Connection.RemoteIpAddress?.ToString(),
            UserAgent(context),
            context,
            inviteToken,
            pending.LinkUserId);

        // The purpose the SIGNED state cookie carries decides which seam may complete this attempt, and only
        // that seam: a link is never completed by the login completion (it would bind a provider to an account
        // without the explicit confirm of AC6), and a sign-in is never completed by the link one. Unprotect
        // already refused every purpose but these two, so "not link" is "login" here.
        var isLink = string.Equals(pending.Purpose, LoginStatePayload.PurposeLink, StringComparison.Ordinal);
        var completed = isLink
            ? await CompleteLinkAsync(context, request, selected.Name, login, cancellationToken).ConfigureAwait(false)
            : await completion.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        // The attempt counter is the SIGN-IN success rate and nothing else: a link stages an
        // identity for the explicit confirm of AC6 and signs nobody in, so counting its outcome here — as
        // `completed` or as its seam's refusal code — would inflate that rate. The linking slice counts no
        // counter of its own (there is none in EvidgateTelemetry), so what a link decides is recorded
        // nowhere, exactly as LinkErrors documents; a refusal this route raises itself (an unusable state
        // cookie, a provider outage) has no known purpose and stays counted as before.
        if (!isLink)
        {
            EvidgateTelemetry.RecordLoginAttempt(
                selected.Name,
                completed.IsSuccess ? OutcomeCompleted : completed.Error!.Code);
        }

        if (!completed.IsSuccess)
        {
            return completed;
        }

        // The seam promises an app path, and the browser is sent here right after a successful sign-in:
        // this is the sink where a completion bug (a computed target, a provider value) would become an
        // open redirect, so the allowlist is applied once more to what actually leaves the server. The value
        // is already percent-encoded by the completion (ReturnUrlEncoder), so a non-ASCII path is a valid
        // ASCII Location and this check sees the bytes Kestrel writes.
        var target = ReturnUrlPolicy.IsAppPath(completed.Value) ? completed.Value : ReturnUrlPolicy.Root;

        return Result.Success<IResult>(Results.Redirect(target), StatusCodes.Status302Found);
    }

    /// <summary>
    /// Completes an explicit second-provider link through <see cref="ILinkCompletion"/> (Q4) and
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

    /// <summary>Constant-time comparison of the stored and presented <c>state</c> (never a plain string equality).</summary>
    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
}
