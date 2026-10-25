using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Resolve;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The explicit second-provider link of 02.3.1-T2.2 (Q4, AC6): <c>POST /v1/auth/link/{provider}</c>,
/// <c>GET</c>/<c>DELETE /v1/auth/link/pending</c> and <c>POST /v1/auth/link/confirm</c>.
/// </summary>
/// <remarks>
/// <para>
/// PRE-TENANT, like the login routes they are mapped beside (00 §5.4.8): only the 00 §9.2 envelope filter
/// applies, and the caller is authenticated by the <c>__Host-evidgate_session</c> cookie alone —
/// <see cref="ISessionRequestAuthenticator"/> resolves it in one statement (0045), enforces the session's
/// lifecycle (401 <c>unauthenticated</c> / <c>session_expired</c> / <c>session_revoked</c>) and the account's
/// status (403 <c>account_disabled</c>), and is the only user any of these routes acts on. No route takes a
/// user id, so one account can never link or read another's staged identities, and <c>confirm</c> hands the
/// cookie — not the resolved id — to the database, which resolves the target account itself (D8).
/// </para>
/// <para>
/// <b>Three steps, one confirmation.</b> <c>start</c> builds the provider authorization URL through the same
/// <see cref="LoginEndpoints.BeginAuthorization"/> the sign-in route uses, with the calling account as the
/// state's <c>link</c> target; the shared callback verifies the identity and
/// <see cref="ILinkCompletion"/> stages it in <see cref="LinkConfirmCookie"/>; <c>pending</c> shows what is
/// waiting and <c>confirm</c> writes it. That order is what makes "an explicit confirm, never an automatic
/// merge" (AC6, D3) true at the API, not only in a comment.
/// </para>
/// <para>
/// <b>Every route</b> answers <c>Cache-Control: no-store</c> (its body carries personal data or a redirect
/// target), spends its own prefix of the CI exchange limiter's per-client budget — so a flood of one route
/// cannot spend another's, and none of them can spend the sign-in routes' — and refuses a present
/// <c>Origin</c> header that is not this deployment's public origin with 403 <c>forbidden</c>. The origin check
/// is defence in depth on top of <c>SameSite=Lax</c>: a same-site browser form post still carries the session
/// cookie, so the routes refuse a cross-origin caller that got the browser to send one.
/// </para>
/// <para>
/// <b>CSRF is enforced, not only discouraged.</b> All three cookie-authenticated mutations — <c>start</c>
/// (<c>POST /v1/auth/link/{provider}</c>), <c>confirm</c> (<c>POST /v1/auth/link/confirm</c>) and
/// <c>DELETE /v1/auth/link/pending</c> — are on the deployment's antiforgery filter's scope (02.3.2-T1, AC5,
/// the contract's <c>XsrfToken</c> on those three operations): they require the <c>XSRF-TOKEN</c> cookie /
/// <c>X-XSRF-TOKEN</c> header pair bound to this session, so a request without it is refused with 403
/// <c>csrf_failed</c> before any handler on this page runs. The origin check above stays as the second,
/// cheaper line, and <c>DELETE pending</c> keeps its session requirement.
/// </para>
/// <para>
/// <b>Nothing sensitive on the wire.</b> No token, code, subject, email or cookie value is echoed, logged or
/// counted here; the responses carry the provider's name, the verified address of the caller's OWN staged
/// link, a relative app path or the authorization URL.
/// </para>
/// </remarks>
internal static class LinkEndpoints
{
    /// <summary>Every route's cache policy: a linking answer is never reusable.</summary>
    private const string NoStore = "no-store";

    /// <summary>Largest body accepted by <c>start</c>: one optional <c>return_url</c> plus envelope slack.</summary>
    internal const int MaxBodyBytes = 4096;

    /// <summary>
    /// The <c>organization.status</c> whose chain a link is recorded in: a suspended org's row would be
    /// refused by its own scope, so only the active ones are asked for.
    /// </summary>
    private const string ActiveOrgStatus = "active";

    /// <summary>The audit metadata key carrying the account an identity was moved from (on the writer's allowlist).</summary>
    private const string PreviousIdMetadataKey = "previous_id";

    /// <summary>The limiter key prefix of <c>start</c>, under the CI exchange limiter's per-client key.</summary>
    private const string StartLimiterPrefix = "login-link-start:";

    /// <summary>The limiter key prefix of the two <c>pending</c> routes (one resource, one budget).</summary>
    private const string PendingLimiterPrefix = "login-link-pending:";

    /// <summary>The limiter key prefix of <c>confirm</c>, the route that writes.</summary>
    private const string ConfirmLimiterPrefix = "login-link-confirm:";

    /// <summary>Limiter units one <c>start</c> spends: the same cost as a sign-in's <c>start</c>.</summary>
    private const int StartCost = 1;

    /// <summary>Limiter units one <c>pending</c> call spends: a read over the caller's own cookie.</summary>
    private const int PendingCost = 1;

    /// <summary>Limiter units one <c>confirm</c> spends: it writes a row and an audit chain.</summary>
    private const int ConfirmCost = 2;

    private static readonly JsonSerializerOptions BodyOptions =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Maps the four linking routes onto the login group and returns it.</summary>
    /// <param name="group">The <c>/v1/auth</c> group <see cref="LoginEndpoints"/> built.</param>
    /// <returns>The same group, so one composition applies to every route on it.</returns>
    internal static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // `.Produces<T>(200)` declares the wire shape; `.Produces(200)` alone replaces the response ASP.NET
        // would infer from the delegate's `Result` return type when the real success status is not 200 (the
        // 204 of the deletion), so the wrapper never reaches the served OpenAPI document.
        group.MapPost("/link/{provider}", StartAsync)
            .Produces<LinkStartResponse>(StatusCodes.Status200OK)
            .WithSummary("Start adding a second sign-in provider to the signed-in account (02.3.1-T2)");

        group.MapGet("/link/pending", PendingAsync)
            .Produces<LinkPendingResponse>(StatusCodes.Status200OK)
            .WithSummary("The second-provider link this session has staged and not confirmed yet (02.3.1-T2)");

        group.MapDelete("/link/pending", DeletePendingAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Discard the staged second-provider link of this session (02.3.1-T2)");

        group.MapPost("/link/confirm", ConfirmAsync)
            .Produces<LinkConfirmResponse>(StatusCodes.Status200OK)
            .WithSummary("Confirm the staged second-provider link and attach the identity to the signed-in account (02.3.1-T2)");

        return group;
    }

    /// <summary>
    /// <c>start</c>: a 429 once the client's window is spent, a 403 for a foreign origin, a 401 without a live
    /// session, a 404 for an unknown provider, a 413/422 for a body that is not one optional
    /// <c>return_url</c>, otherwise 200 with the provider's authorization URL and the staged state cookie.
    /// </summary>
    private static async Task<Result> StartAsync(
        HttpContext context,
        string provider,
        IEnumerable<ILoginProvider> providers,
        LoginStateCookie stateCookie,
        ISessionRequestAuthenticator authenticator,
        ExchangeRateLimiter limiter,
        IOptions<LoginOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stateCookie);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        context.Response.Headers.CacheControl = NoStore;

        // First: the per-client budget, then the origin, then anything that costs a database read.
        if (Throttle(context, limiter, StartLimiterPrefix, StartCost) is { } throttled)
        {
            return Result.Failure(throttled);
        }

        var login = options.Value;

        if (OriginRefused(context, login) is { } foreign)
        {
            return Result.Failure(foreign);
        }

        var selected = Find(providers, provider);
        if (selected is null)
        {
            return Result.Failure(LoginErrors.UnknownProvider);
        }

        var resolved = await AuthenticateAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result.Failure(resolved.Error!);
        }

        var (returnUrl, bodyError) = await ReadReturnUrlAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure(bodyError);
        }

        // The same helper the sign-in route uses, with the calling account as the link target: one place
        // builds the cookie, the PKCE challenge and the authorization URL, so the two cannot drift apart, and
        // the callback decides from the SIGNED state cookie which seam completes the attempt.
        var began = LoginEndpoints.BeginAuthorization(context, selected, stateCookie, options, clock, returnUrl, resolved.Value);
        if (!began.IsSuccess)
        {
            return Result.Failure(began.Error!);
        }

        return Result.Success(new LinkStartResponse(began.Value.AbsoluteUri));
    }

    /// <summary>
    /// <c>pending</c>: a 429/403/401 as above, then 200 with the staged identity's provider, address and
    /// expiry, or 404 <c>not_found</c> when nothing usable is staged. A presented cookie that is expired,
    /// foreign or unreadable is deleted on the way out.
    /// </summary>
    private static async Task<Result> PendingAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        LinkConfirmCookie cookie,
        ExchangeRateLimiter limiter,
        IOptions<LoginOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);

        context.Response.Headers.CacheControl = NoStore;

        if (Throttle(context, limiter, PendingLimiterPrefix, PendingCost) is { } throttled)
        {
            return Result.Failure(throttled);
        }

        if (OriginRefused(context, options.Value) is { } foreign)
        {
            return Result.Failure(foreign);
        }

        var resolved = await AuthenticateAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result.Failure(resolved.Error!);
        }

        var presented = context.Request.Cookies[LinkConfirmCookie.Name];
        var pending = cookie.Read(presented);

        if (pending is null || pending.UserId != resolved.Value)
        {
            Discard(context, presented);

            return Result.Failure(LinkErrors.NothingPending);
        }

        return Result.Success(
            new LinkPendingResponse(
                pending.Provider,
                pending.Email,
                pending.DisplayName,
                pending.IssuedAt + LinkConfirmCookie.Lifetime));
    }

    /// <summary>
    /// <c>DELETE pending</c>: 204 and the staged link is gone. A session is required, so a cross-site caller
    /// that somehow sent no cookie cannot clear another browser's staging.
    /// </summary>
    private static async Task<Result> DeletePendingAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        ExchangeRateLimiter limiter,
        IOptions<LoginOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);

        context.Response.Headers.CacheControl = NoStore;

        if (Throttle(context, limiter, PendingLimiterPrefix, PendingCost) is { } throttled)
        {
            return Result.Failure(throttled);
        }

        if (OriginRefused(context, options.Value) is { } foreign)
        {
            return Result.Failure(foreign);
        }

        var deleted = await AuthenticateAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (!deleted.IsSuccess)
        {
            return Result.Failure(deleted.Error!);
        }

        LinkConfirmCookie.Delete(context);

        return Result.Success(StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// <c>confirm</c>: a 429/403/401 as above, 401 when nothing usable is staged (or the staged link belongs
    /// to another account), then the one write — 200 with the outcome, 409 when the identity belongs to an
    /// account still in use, 403 <c>account_disabled</c>, or 503 when the audit chain could not be extended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the story's: the identity row first (<see cref="ILinkIdentityStore"/>, one call, the
    /// database's own decision), then the audit rows of every ACTIVE org of the confirming user. A 503 from the
    /// audit leaves the staged link in place, so the retry converges on <c>already_linked</c> and writes the rows
    /// again — including <c>auth.link_move</c> when the first attempt moved the identity, because that attempt
    /// re-issues the staging carrying the account it came from; every other outcome deletes the cookie.
    /// </para>
    /// <para>
    /// A retry therefore audits AT LEAST once per org, not exactly once: an org whose row committed before
    /// another org faulted keeps that row and gains a second one. <see cref="AuditAsync"/> records why that trade
    /// is the one this route takes.
    /// </para>
    /// <para>
    /// What the deletion ends is the BROWSER's staging, not a server-side single use: the payload is a protected
    /// cookie and Q4 ruled out a table for its ten minutes, so there is no spent marker to consult. A client that
    /// kept the deleted value can present it again; the database then answers <c>already_linked</c> (or completes
    /// the same link once), no second identity row is written, and the audit rows are appended again (the
    /// at-least-once rule above).
    /// </para>
    /// </remarks>
    private static async Task<Result> ConfirmAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        LinkConfirmCookie cookie,
        ILinkIdentityStore identities,
        IMemberOrgResolver memberOrgs,
        IIdentityAuditWriter audit,
        ExchangeRateLimiter limiter,
        IOptions<LoginOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(memberOrgs);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);

        context.Response.Headers.CacheControl = NoStore;

        if (Throttle(context, limiter, ConfirmLimiterPrefix, ConfirmCost) is { } throttled)
        {
            return Result.Failure(throttled);
        }

        var login = options.Value;

        if (OriginRefused(context, login) is { } foreign)
        {
            return Result.Failure(foreign);
        }

        var resolved = await AuthenticateAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result.Failure(resolved.Error!);
        }

        var userId = resolved.Value;
        var presented = context.Request.Cookies[LinkConfirmCookie.Name];
        var pending = cookie.Read(presented);

        if (pending is null || pending.UserId != userId)
        {
            Discard(context, presented);

            return Result.Failure(LinkErrors.LinkNotPending);
        }

        // The proof of the live session, never a user id (D8 / SEC2, coordinator ruling): login_link_identity
        // resolves the target account from the HMAC the authenticator computed for the session that this
        // request was authenticated with — after a lazy rotation that is the newly issued cookie's hash, so a
        // privilege-marked session links instead of answering `unauthenticated`. No raw cookie leaves the
        // HTTP layer and nothing is re-resolved from the presented value.
        var proof = await SessionProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        var linked = await identities
            .LinkAsync(proof, pending.Provider, pending.Subject, pending.Email, cancellationToken)
            .ConfigureAwait(false);

        if (!linked.IsSuccess)
        {
            return Result.Failure(linked.Error!);
        }

        var outcome = linked.Value;
        switch (outcome.Result)
        {
            case LinkResult.Linked:
            case LinkResult.AlreadyLinked:
            case LinkResult.Moved:
                break;

            case LinkResult.Unauthenticated:
                // The session died between the authenticator's own check and the database's: nothing was written,
                // and the browser has to sign in again (401 unauthenticated, the authenticator's own answer).
                Discard(context, presented);

                return Result.Failure(SessionErrors.Unauthenticated);

            case LinkResult.Disabled:
                // The account was disabled between the authenticator's check and the confirm (0045 answers
                // `disabled`, never `unauthenticated`, so the two stay distinguishable): 403 account_disabled.
                Discard(context, presented);

                return Result.Failure(SessionErrors.AccountDisabled);

            case LinkResult.Conflict:
                // Another account still holds this identity and is in use (AC6): the route deletes the staged cookie.
                Discard(context, presented);

                return Result.Failure(LinkErrors.IdentityInUse);

            default:
                // An outcome this build does not know: fail closed, and keep the staging so a retry after the
                // deployment is fixed converges instead of losing the verified identity.
                return Result.Failure(LinkErrors.UnavailableWithRetryAfter(context, login));
        }

        // Which account the identity came FROM, when this confirm moved it. A first attempt reports that
        // itself; a retry answers already_linked and reports nothing, so the fact comes from the staging the
        // failed audit re-issued (F1, D-S81-Q5). Without it a move whose audit rows never committed would keep
        // its `auth.link_move` row forever. No other outcome reads the carried fact: a carried move plus a
        // `linked` answer is not the same link, and inventing a move row for it would be worse than the gap.
        var movedFrom = outcome.Result switch
        {
            LinkResult.Moved => outcome.MovedFromUserId,
            LinkResult.AlreadyLinked => pending.MovedFromUserId,
            _ => null,
        };

        var audited = await AuditAsync(audit, memberOrgs, userId, pending.Provider, movedFrom, cancellationToken)
            .ConfigureAwait(false);

        if (!audited)
        {
            // The link is committed and the audit is not: fail closed (503) and KEEP the staging, so the retry
            // converges on already_linked and writes the rows this attempt could not. A move needs one thing
            // more than keeping it — the retry would no longer know the identity was moved — so the staging is
            // re-issued carrying the account it came from, with its original expiry and binding.
            if (movedFrom is { } previous)
            {
                cookie.Restage(context, pending with { MovedFromUserId = previous });
            }

            return Result.Failure(LinkErrors.UnavailableWithRetryAfter(context, login));
        }

        LinkConfirmCookie.Delete(context);

        return Result.Success(new LinkConfirmResponse(pending.Provider, OutcomeName(outcome.Result)));
    }

    /// <summary>
    /// The audit rows of one confirmed link: <c>auth.link</c> in the chain of every active org of the
    /// confirming user, and — when the identity was moved — <c>auth.link_move</c> with the account it came
    /// from. Both are written through <see cref="IIdentityAuditWriter"/>, which opens and commits one tenant
    /// transaction per org and propagates a fault (fail closed).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A retry can repeat <c>auth.link</c>.</b> Each org commits its own transaction, so an audit that faulted
    /// on the third of four orgs leaves the first two rows written; the retry that converges on
    /// <c>already_linked</c> writes them again, and the chains of those orgs then hold the row twice. The
    /// alternative would be to read each org's chain back before appending and to skip the orgs that already
    /// hold this row — a per-org read plus a "was it this link or another one of the same provider" judgement on
    /// a pre-tenant path, for an at-least-once audit that a hash chain is designed to tolerate (a duplicate row
    /// is visible and verifiable; a missing one is not). Duplicates are therefore accepted, and only the
    /// move — which the retry could not record at all without the staging carrying it — is carried forward.
    /// </para>
    /// <para>
    /// <b>The move row is subject to the same rule.</b> It is a second write over the same org list, so a fault in
    /// the middle of it leaves the earlier orgs' <c>auth.link_move</c> rows committed and the retry — which still
    /// carries the account the identity came from — appends them again. Identical <c>previous_id</c>, identical
    /// verifiability argument, same acceptance: the move is at least once per org, never exactly once.
    /// </para>
    /// </remarks>
    /// <returns>True when every row committed, false when one did not (the route answers 503).</returns>
    private static async Task<bool> AuditAsync(
        IIdentityAuditWriter audit,
        IMemberOrgResolver memberOrgs,
        Guid userId,
        string provider,
        Guid? movedFrom,
        CancellationToken cancellationToken)
    {
        try
        {
            var orgIds = (await memberOrgs.MemberOrgsAsync(userId, cancellationToken).ConfigureAwait(false))
                .Where(org => string.Equals(org.Status, ActiveOrgStatus, StringComparison.Ordinal))
                .Select(org => org.OrgId)
                .ToArray();

            await audit
                .WriteAsync(new IdentityAuditEvent(IdentityAuditActions.Link, userId, provider), orgIds, cancellationToken)
                .ConfigureAwait(false);

            if (movedFrom is { } previous)
            {
                await audit
                    .WriteAsync(
                        new IdentityAuditEvent(
                            IdentityAuditActions.LinkMove,
                            userId,
                            provider,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                [PreviousIdMetadataKey] = previous.ToString("D"),
                            }),
                        orgIds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (Npgsql.NpgsqlException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A database timeout that surfaced as cancellation is an outage, not the caller leaving.
            return false;
        }
    }

    /// <summary>The wire name of a successful confirm outcome.</summary>
    /// <param name="result">One of the three successful outcomes.</param>
    private static string OutcomeName(LinkResult result) => result switch
    {
        LinkResult.Moved => "moved",
        LinkResult.AlreadyLinked => "already_linked",
        _ => "linked",
    };

    /// <summary>
    /// The acting user of this request, from the deployment's session authenticator (02.3.2-T1, D2/D7): the
    /// refusal the authenticator produced (401 <c>unauthenticated</c> / <c>session_expired</c> /
    /// <c>session_revoked</c>, 403 <c>account_disabled</c>) is answered verbatim, so the link routes no longer
    /// collapse a disabled account into "no session".
    /// </summary>
    /// <param name="context">The request whose cookie is authenticated (the authenticator reads it itself).</param>
    /// <param name="authenticator">The deployment's authenticator, memoized per request.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>The acting user, or the refusal to answer.</returns>
    private static async Task<Result<Guid>> AuthenticateAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var outcome = await authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);

        if (outcome.Refusal is { } refusal)
        {
            return Result.Failure<Guid>(refusal);
        }

        // An outcome that is neither a refusal nor a live user and session is a composition bug, not a 401: it
        // must not read as "signed out" to a caller that is in fact authenticated.
        return outcome is { UserId: { } userId, SessionId: not null }
            ? Result.Success(userId)
            : Result.Failure<Guid>(SessionErrors.Unavailable);
    }

    /// <summary>
    /// The live session's proof for the link store (<c>SessionRequestOutcome.SessionProof</c>): the HMAC the
    /// authenticator computed for that session's cookie, which after an in-request lazy rotation is the hash of
    /// the newly issued cookie. Asked of the same per-request memoized authenticator, so this costs no second
    /// statement and no second rotation.
    /// </summary>
    /// <param name="context">The authenticated request.</param>
    /// <param name="authenticator">The deployment's authenticator.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>The hash, or null when the authenticator refuses or reports no proof (answered as 401 <c>unauthenticated</c>).</returns>
    private static async Task<byte[]?> SessionProofAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var outcome = await authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);

        return outcome.Refusal is null ? outcome.SessionProof : null;
    }

    /// <summary>Deletes the staged-link cookie when the request presented one.</summary>
    private static void Discard(HttpContext context, string? presented)
    {
        if (!string.IsNullOrEmpty(presented))
        {
            LinkConfirmCookie.Delete(context);
        }
    }

    /// <summary>
    /// Refuses a request that named an <c>Origin</c> other than this deployment's public origin, and any
    /// <c>Origin</c> at all when the deployment has no public base URL to compare with (fail closed).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="options">The bound login options (the browser-facing base URL).</param>
    /// <returns>The 403 to answer, or null when the request may continue.</returns>
    private static Error? OriginRefused(HttpContext context, LoginOptions options)
    {
        if (!context.Request.Headers.TryGetValue(HeaderNames.Origin, out var values) || values.Count == 0)
        {
            // A same-origin navigation and every non-browser client send no Origin; SameSite=Lax already
            // keeps the cookie off a cross-site request.
            return null;
        }

        var presented = values[0];
        if (string.IsNullOrEmpty(presented)
            || string.Equals(presented, "null", StringComparison.Ordinal)
            || !Uri.TryCreate(presented, UriKind.Absolute, out var origin)
            || PublicBaseUri(options) is not { } expected
            || !SameOrigin(origin, expected))
        {
            return LinkErrors.ForeignOrigin;
        }

        return null;
    }

    /// <summary>The deployment's browser-facing base URL, or null when it is absent or unusable.</summary>
    private static Uri? PublicBaseUri(LoginOptions options) =>
        Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri ? uri : null;

    /// <summary>Scheme, host and port equality (the three parts an origin is; the path never belongs to it).</summary>
    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    /// <summary>
    /// Reads the optional <c>{"return_url": "/path"}</c> body under <see cref="MaxBodyBytes"/> and returns the
    /// allowlisted target, or the error the route answers (413 or 422). The value goes through
    /// <see cref="ReturnUrlPolicy"/>, so a refused path is the app root rather than an error. Nothing read here
    /// is logged.
    /// </summary>
    private static async Task<(string ReturnUrl, Error? Error)> ReadReturnUrlAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is 0)
        {
            // The body is optional: a client that sends none lands on the app root after the link.
            return (ReturnUrlPolicy.Root, null);
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (ReturnUrlPolicy.Root, TooLarge());
        }

        using var limited = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (limited.Length > MaxBodyBytes)
            {
                return (ReturnUrlPolicy.Root, TooLarge());
            }
        }

        if (limited.Length == 0)
        {
            return (ReturnUrlPolicy.Root, null);
        }

        // The media type is a security boundary here too (the invite staging route's reasoning): a cross-site
        // form post cannot be labelled `application/json`, so a handful of fields never arrives as one.
        if (!context.Request.HasJsonContentType())
        {
            return (ReturnUrlPolicy.Root, Invalid("the request body must be application/json"));
        }

        LinkStartRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<LinkStartRequest>(limited.ToArray(), BodyOptions);
        }
        catch (JsonException)
        {
            return (ReturnUrlPolicy.Root, Invalid("the request body is not valid JSON"));
        }

        return (ReturnUrlPolicy.Resolve(body?.ReturnUrl), null);
    }

    /// <summary>
    /// Spends up to <paramref name="cost"/> units of the client's window in the CI exchange limiter and
    /// returns the 00 §9.2 429 <c>rate_limited</c> failure (with <c>Retry-After</c> set) when the budget is
    /// gone, or null to continue. Same shape as the login routes' own throttle.
    /// </summary>
    private static Error? Throttle(HttpContext context, ExchangeRateLimiter limiter, string prefix, int cost)
    {
        var key = prefix + ExchangeRateLimiter.ClientKey(context.Connection.RemoteIpAddress);

        if (!limiter.TryAcquire(key, out var retryAfter))
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

            return new Error(
                ApiErrorCode.RateLimited,
                "too many provider-link attempts; retry later",
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

    private static Error TooLarge() => new(
        ApiErrorCode.PayloadTooLarge,
        "the request body is too large",
        StatusCodes.Status413PayloadTooLarge);

    private static Error Invalid(string message) => Error.Invalid(message, [new ErrorDetail("body", "invalid")]);
}

/// <summary>The wire body of <c>POST /v1/auth/link/{provider}</c>: <c>{"return_url": "/settings/security"}</c>, optional.</summary>
/// <param name="ReturnUrl">Where the app should land after the link, as a same-origin path; null when the body omitted it.</param>
internal sealed record LinkStartRequest(string? ReturnUrl);

/// <summary>The answer of <c>POST /v1/auth/link/{provider}</c>.</summary>
/// <param name="AuthorizationUrl">The provider's authorization URL the browser is sent to next.</param>
internal sealed record LinkStartResponse(string AuthorizationUrl);

/// <summary>The answer of <c>GET /v1/auth/link/pending</c>: the caller's own staged identity.</summary>
/// <param name="Provider">The provider the identity was verified by.</param>
/// <param name="Email">The address that provider verified.</param>
/// <param name="DisplayName">The provider's profile name, or null.</param>
/// <param name="ExpiresAt">When the staged link stops being confirmable.</param>
internal sealed record LinkPendingResponse(
    string Provider,
    string Email,
    string? DisplayName,
    DateTimeOffset ExpiresAt);

/// <summary>The answer of <c>POST /v1/auth/link/confirm</c>.</summary>
/// <param name="Provider">The provider the identity was verified by.</param>
/// <param name="Outcome"><c>linked</c>, <c>already_linked</c> or <c>moved</c>.</param>
internal sealed record LinkConfirmResponse(string Provider, string Outcome);
