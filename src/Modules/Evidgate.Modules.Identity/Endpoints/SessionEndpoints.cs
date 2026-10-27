using System.Data.Common;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The session-management routes of 02.3.2-T2 (AC4, AC5, AC6, AC8): <c>GET /v1/auth/sessions</c>,
/// <c>DELETE /v1/auth/sessions/{s}</c>, <c>POST /v1/auth/sessions/revoke-all</c> and
/// <c>POST /v1/auth/logout</c>.
/// </summary>
/// <remarks>
/// <para>
/// PRE-TENANT, exactly like the linking routes they are mapped beside (00 §5.4.8): the caller is the
/// <c>__Host-evidgate_session</c> cookie and nothing else. <see cref="ISessionRequestAuthenticator"/> resolves
/// it in one statement and answers the lifecycle refusals (401 <c>unauthenticated</c> /
/// <c>session_expired</c> / <c>session_revoked</c>, 403 <c>account_disabled</c>) verbatim, so these routes never
/// turn a dead row into a generic 401. No route takes a user id: what the caller may list or revoke comes from
/// the presented cookie, and the database resolves the acting user from the session proof itself.
/// </para>
/// <para>
/// <b>The proof, never the cookie.</b> Every handler hands
/// <see cref="SessionRequestOutcome.GetSessionProof"/> — the stored HMAC of the live cookie value — to
/// <see cref="ISessionRoutesStore"/>, which passes it to the <c>SECURITY DEFINER</c> functions of migration
/// 0048. The raw cookie never leaves the HTTP layer, no response carries it, its hash, the pepper or the
/// pepper version, and nothing on this path logs or echoes any of them (D9).
/// </para>
/// <para>
/// <b>One 404, never a 403.</b> <c>DELETE /sessions/{s}</c> answers 204 for a row the caller owns, and for a
/// member of an org the caller administers — the <c>membership:write</c> check plus the target's membership in
/// that SAME org run inside the definer, not here. Every other answer is the identical 404 <c>not_found</c>
/// envelope: a foreign row, an unknown id, a row in another org and a row that is already dead are
/// indistinguishable, so the route is never an oracle over other accounts' sessions (AC6).
/// </para>
/// <para>
/// <b>The id is a string, parsed after authentication.</b> The segment binds as <c>string</c> and only becomes a
/// <see cref="Guid"/> once the caller IS authenticated, so a value that is not a uuid — including the literal
/// <c>revoke-all</c>, which the router happily hands to <c>{s}</c> — answers that same 404 instead of a binding
/// 400 raised before the credential was judged. The statuses the contract declares (204, 401, 403, 404, 503)
/// are therefore the only ones this route can produce, and a caller cannot probe the id format without a live
/// session.
/// </para>
/// <para>
/// <b>Cookies on the way out.</b> A successful <c>logout</c>, <c>revoke-all</c> with <c>keep_current: false</c>,
/// and a <c>DELETE /sessions/{s}</c> whose id is the caller's OWN session, expire the session cookie and the
/// antiforgery pair in the same response (<see cref="SessionCookieWriter.Expire"/>) — but only AFTER the row was
/// revoked, so a failed write never signs a live session out of its own credential. <c>revoke-all</c> defaults to
/// keeping the current session, and revoking any OTHER row of the account leaves the caller's pair untouched.
/// </para>
/// <para>
/// <b>CSRF is enforced, not only declared.</b> The three mutations are cookie-authenticated
/// <c>POST</c>/<c>DELETE</c> requests, so the host-wide <c>SessionXsrfMiddleware</c> requires the
/// <c>XSRF-TOKEN</c> cookie / <c>X-XSRF-TOKEN</c> header pair before any handler here runs (AC5); a request
/// without it answers 403 <c>csrf_failed</c>. The <c>GET</c> is a safe method and therefore outside the rule.
/// </para>
/// <para>
/// <b>No store except the antiforgery pair.</b> The list carries the caller's own device metadata, so every
/// answer sets <c>Cache-Control: no-store</c>; none of the four routes needs an <c>Idempotency-Key</c>
/// (revocation is naturally idempotent: revoking a revoked row changes nothing).
/// </para>
/// <para>
/// <b>An admin revoke is audited; an owner revoke is not (F48).</b> <c>DELETE /sessions/{s}</c> with an
/// <see cref="OrgHeader"/> writes ONE <c>auth.session_revoke_admin</c> row to that org's chain
/// (<see cref="IIdentityAuditWriter"/>) after the definer answered <c>revoked</c> — actor the admin, object the
/// revoked session. A self revoke (current row or a second device, header or not), a <c>logout</c>, a
/// <c>revoke-all</c> and every refusal write nothing: those are the account acting on its own sessions. The
/// writer opens its own org transaction, so the row is not atomic with the revoke; if it faults the route
/// answers 503 and logs the fault's type (and SQLSTATE) with the ids that identify the committed revoke, never a
/// cookie, a hash, the pepper or the writer's message text. That write is deliberately detached from the request's
/// abort token and bounded by its own deadline (see <see cref="RevokeOneAsync"/> and
/// <see cref="IIdentityAuditWriter.WriteSessionRevokedAsync"/> for the exact failure window and for why the row
/// omits the target user — F55 closes both).
/// </para>
/// </remarks>
internal static partial class SessionEndpoints
{
    /// <summary>Every route's cache policy: a session answer is never reusable by a shared cache.</summary>
    private const string NoStore = "no-store";

    /// <summary>
    /// Largest body accepted by <c>revoke-all</c>: one optional boolean plus envelope slack. A larger body is a
    /// client error (413), never a reason to buffer something unbounded (SEC3).
    /// </summary>
    internal const int MaxBodyBytes = 1024;

    /// <summary>
    /// The bound on the post-commit audit write of an administrative revoke (F48): long enough for the org's chain
    /// lock to clear, short enough that a hung writer cannot hold a response the revoke already earned. The write
    /// is deliberately NOT bound to the request token — see <see cref="AuditAdminRevokeAsync"/>.
    /// </summary>
    private const int AuditWriteTimeoutSeconds = 10;

    /// <summary>
    /// The header a member may use to name the org of an administrative revoke (02.6.2 §5). Mirrors
    /// <c>Evidgate.Api.Tenancy.TenantResolver.OrgHeader</c> — the module must not reference the host — and a
    /// test pins the two together so a rename fails instead of silently dropping the admin path.
    /// </summary>
    internal const string OrgHeader = "X-Evidgate-Org";

    private static readonly JsonSerializerOptions BodyOptions =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Maps the four session routes onto the <c>/v1/auth</c> group and returns it.</summary>
    /// <param name="group">The <c>/v1/auth</c> group <see cref="LoginEndpoints"/> built.</param>
    /// <returns>The same group, so one composition applies to every route on it.</returns>
    internal static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // `.Produces<T>(200)` declares the wire shape; the bodiless `.Produces(200)` on the three mutations
        // replaces the response ASP.NET would infer from the delegate's `Result` return type when the real
        // success status is not 200 (the 204 of a revocation), so the C8 wrapper never reaches the served
        // OpenAPI document (the same shape as the link routes).
        group.MapGet("/sessions", ListAsync)
            .Produces<SessionListResponse>(StatusCodes.Status200OK)
            .WithSummary("List the live sessions of the signed-in account, the current one marked (02.3.2-T2)");

        group.MapDelete("/sessions/{s}", RevokeOneAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Revoke one session: the caller's own, or a member's when the caller administers the org (02.3.2-T2)");

        group.MapPost("/sessions/revoke-all", RevokeAllAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Revoke every session of the signed-in account, keeping the current one unless told otherwise (02.3.2-T2)");

        group.MapPost("/logout", LogoutAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Revoke the current session and expire its cookies (02.3.2-T2)");

        return group;
    }

    /// <summary>
    /// <c>GET /v1/auth/sessions</c> (AC6): 200 with the caller's LIVE sessions, newest first and exactly one
    /// marked <c>current</c>, or the authenticator's own 401/403. Only the documented members are emitted — no
    /// cookie value, no hash and no pepper version can reach the body.
    /// </summary>
    private static async Task<Result> ListAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        ISessionRoutesStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, _, _, refusal) = await SessionProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure(refusal);
        }

        var (status, items) = await store.ListAsync(proof, cancellationToken).ConfigureAwait(false);

        return status switch
        {
            SessionRouteStatus.Done => Result.Success(new SessionListResponse(
                [.. items.Select(item => new SessionView(
                    item.Id,
                    item.Current,
                    item.CreatedAt,
                    item.LastSeenAt,
                    item.ExpiresAt,
                    item.AbsoluteExpiresAt,
                    item.Ip,
                    item.UserAgent))])),
            // The session died between the authenticator's statement and this one: the browser signs in again.
            SessionRouteStatus.Unauthenticated => Result.Failure(SessionErrors.Unauthenticated),
            // A list has no addressed row, so `not_found` — and any status a build does not know — is a
            // composition bug, not an answer about a session: fail closed (503) rather than invent one.
            _ => Result.Failure(SessionErrors.Unavailable),
        };
    }

    /// <summary>
    /// <c>DELETE /v1/auth/sessions/{s}</c> (AC6): 204 when the row was revoked; the identical 404
    /// <c>not_found</c> for a foreign, unknown, other-org or already-dead id; the authenticator's 401/403.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The segment is a string, and it is parsed LAST.</b> <paramref name="s"/> binds as text and is turned into
    /// a <see cref="Guid"/> only after the caller was authenticated, so a malformed id — including the literal
    /// <c>revoke-all</c>, which the router also matches for this template — answers the same single 404
    /// <see cref="SessionNotFound"/> envelope instead of the undocumented 400 a typed binding raises BEFORE the
    /// credential is judged. Authentication therefore outranks the id's shape, exactly as it outranks every other
    /// answer on this route.
    /// </para>
    /// <para>
    /// A present <see cref="OrgHeader"/> names the org the caller administers; without it only the owner path
    /// applies. A value that is not a uuid is treated as absent — it can only narrow what the definer is asked
    /// to authorise, never widen it, and it keeps the answer for a malformed header identical to the owner path's.
    /// </para>
    /// <para>
    /// Revoking the caller's OWN row ends the request's credential, so the answer expires the session cookie and
    /// the antiforgery pair exactly as <c>logout</c> does (and only after the write succeeded). Any other
    /// revocable row — the account's second device, or a member's session an admin ends — sets no cookie: the
    /// caller's pair keeps authenticating.
    /// </para>
    /// <para>
    /// <b>Own or administered, decided before the write (F48).</b> The definer answers a status only, so it cannot
    /// tell the handler whether the addressed row was the caller's own: when an <see cref="OrgHeader"/> is present
    /// this handler lists the caller's live sessions first (the same <see cref="ISessionRoutesStore.ListAsync"/>
    /// the read route uses) and treats the id as its own when that list contains it. A list that is not
    /// <see cref="SessionRouteStatus.Done"/> is answered exactly as the list route answers it (401
    /// <c>unauthenticated</c>, otherwise 503) and no revoke is attempted. Only the OTHER-member branch — a named
    /// org AND an id that is not in the caller's own list — records <c>auth.session_revoke_admin</c>, so a self
    /// revoke of the current row or of a second device writes no admin row even with a header present.
    /// </para>
    /// <para>
    /// <b>The audit row is written after the revoke committed, in the writer's own transaction (F48).</b> A row
    /// the writer could not commit is answered 503 <c>unavailable</c> (the revocation itself stays committed; the
    /// retry answers 404 and writes nothing, and the row omits the target user because the definer does not return
    /// it — F55 closes both with a definer that returns <c>target_user_id</c> and writes the row in its own
    /// transaction). That failure logs <see cref="LogAuditFailed"/> with the actor, the org and the fault's type (and
    /// SQLSTATE) and nothing else: the session id is a credential value under 02.3.2 D9 and never reaches a log line,
    /// so reconciliation of the committed revoke uses the actor, the org and the operator's own record of the
    /// request. The write runs on its own bounded token, never on <c>RequestAborted</c>: the revoke has committed, so
    /// a caller that disconnects must not cancel the only record of it (F48 S1).
    /// </para>
    /// </remarks>
    private static async Task<Result> RevokeOneAsync(
        HttpContext context,
        string s,
        ISessionRequestAuthenticator authenticator,
        ISessionRoutesStore store,
        IIdentityAuditWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, actorUserId, currentSessionId, refusal) = await SessionProofAsync(
                context,
                authenticator,
                cancellationToken)
            .ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure(refusal);
        }

        // Authenticated first: a caller without a live session never learns the id's shape, and a value that is not
        // a uuid is answered with the route's one 404 rather than a binding 400 no response declares.
        if (!Guid.TryParse(s, out var sessionId))
        {
            return Result.Failure(SessionNotFound);
        }

        var orgId = OrgOf(context);

        // Whether the addressed row is the caller's own decides everything below: the admin branch audits, the
        // owner branch does not. Only the admin branch needs the answer, and only it pays for the extra list.
        Guid? adminActorUserId = null;
        var isOwn = false;
        if (orgId is not null)
        {
            // A live outcome always names its user; a build that does not is a composition bug, never a 401.
            if (actorUserId is not { } actor)
            {
                return Result.Failure(SessionErrors.Unavailable);
            }

            var (listStatus, items) = await store.ListAsync(proof, cancellationToken).ConfigureAwait(false);

            // The list route's mapping, verbatim: a dead session between the two statements is 401, and any other
            // code is a composition bug answered 503 — never an invented answer about a session.
            if (listStatus is not SessionRouteStatus.Done)
            {
                return listStatus is SessionRouteStatus.Unauthenticated
                    ? Result.Failure(SessionErrors.Unauthenticated)
                    : Result.Failure(SessionErrors.Unavailable);
            }

            adminActorUserId = actor;
            isOwn = items.Any(item => item.Id == sessionId);
        }

        var status = await store
            .RevokeOneAsync(proof, sessionId, orgId, cancellationToken)
            .ConfigureAwait(false);

        switch (status)
        {
            case SessionRouteStatus.Done:
                // The revoke committed. Only the administered case (a named org and a row that is not the caller's
                // own) is recorded: a self revoke is the account acting on its own session, not an admin action.
                if (orgId is { } auditedOrg && adminActorUserId is { } actor && !isOwn)
                {
                    // Resolved here, not bound as a handler parameter: `ILogger` itself is not a registered
                    // service (only `ILogger<T>` and the factory are), and this branch is the only one that logs.
                    var logger = context.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(SessionEndpoints));

                    // Written on its own bounded token, never `cancellationToken`: the definer has already committed,
                    // so a client that walks away must not cancel the only record of the revoke (F48 S1).
                    if (!await AuditAdminRevokeAsync(audit, logger, actor, auditedOrg, sessionId)
                        .ConfigureAwait(false))
                    {
                        // The row is revoked and the record of it is not: the caller is told the request did not
                        // complete (503), never signed out, and the ids the log carries reconcile the two.
                        return Result.Failure(SessionErrors.Unavailable);
                    }
                }

                // The row that was revoked IS the request's own row: nothing the browser holds names a live session
                // any more, so the pair is expired like a logout (after the write, never before it).
                if (sessionId == currentSessionId)
                {
                    SessionCookieWriter.Expire(context);
                }

                return Result.Success(StatusCodes.Status204NoContent);

            case SessionRouteStatus.NotFound:
                return Result.Failure(SessionNotFound);

            case SessionRouteStatus.Unauthenticated:
                return Result.Failure(SessionErrors.Unauthenticated);

            default:
                return Result.Failure(SessionErrors.Unavailable);
        }
    }

    /// <summary>
    /// The one audit row of an administrative revoke (F48), written after the definer answered <c>revoked</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the row says.</b> The actor is the admin whose session made the call, the chain is the org the admin
    /// named, and the object is the revoked <c>session</c> row. The target user is deliberately absent:
    /// <c>evidgate_resolve.session_revoke_one</c> returns a status only, and obtaining the member's id needs a
    /// migration. F55 returns <c>target_user_id</c> from the definer and writes this row inside the definer's
    /// transaction; until then F48 is a partial record, not a complete one.
    /// </para>
    /// <para>
    /// <b>The failure window.</b> The writer opens its own org scope and transaction — it cannot share the
    /// definer's, which has already committed. A fault therefore leaves the revoke committed and its row missing;
    /// the route answers 503, and a retry finds the session dead, answers 404 and writes nothing, so that one
    /// revoke stays unrecorded. Only the F55 retrofit (one transaction across the revoke and the row) closes it.
    /// </para>
    /// <para>
    /// <b>Why this write does not take the request token.</b> The revoke has committed by the time this is called,
    /// so a caller that aborts its request must not cancel the only record of it: the write runs on its own source
    /// bounded by <see cref="AuditWriteTimeoutSeconds"/>. The bound is a writer fault like any other — a write that
    /// outlives it is cancelled here and the route answers 503 <c>unavailable</c>, never a 500 and never a
    /// silent 204.
    /// </para>
    /// </remarks>
    /// <param name="audit">The identity audit writer.</param>
    /// <param name="logger">The route's logger: the failure is an Error, with the ids that identify the revoke.</param>
    /// <param name="actorUserId">The admin whose session performed the revoke.</param>
    /// <param name="orgId">The org whose chain receives the row.</param>
    /// <param name="sessionId">The revoked session row.</param>
    /// <returns>True when the row committed; false when the writer faulted (the route answers 503).</returns>
    private static async ValueTask<bool> AuditAdminRevokeAsync(
        IIdentityAuditWriter audit,
        ILogger logger,
        Guid actorUserId,
        Guid orgId,
        Guid sessionId)
    {
        // Deliberately its own source: `HttpContext.RequestAborted` is the client leaving, and the row is the
        // record of a revoke that already committed. The deadline keeps a hung writer from holding the response.
        using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AuditWriteTimeoutSeconds));

        try
        {
            await audit
                .WriteSessionRevokedAsync(actorUserId, orgId, sessionId, writeTimeout.Token)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception exception)
        {
            // Every writer fault is the same answer: NpgsqlException, TimeoutException, the chain's
            // AuditBusyException, this write's own timeout surfaced as cancellation, and a defect nobody predicted.
            // The caller is answered 503 (the revoke stays committed), and only the fault's type — plus a database
            // SQLSTATE, which is a code and not a value — enters the log line; `exception.Message` never does.
            LogAuditFailed(logger, actorUserId, orgId, DescribeAuditFault(exception));

            return false;
        }
    }

    /// <summary>
    /// The writer's fault as a log may render it: the exception's type name and, for a database fault, its
    /// SQLSTATE (a code, as <c>ReadinessProbe</c> and <c>IsolationSelfCheck</c> already log it). Never
    /// <c>exception.ToString()</c>, <c>exception.Message</c> or its data: an Npgsql message echoes the failing
    /// statement and its parameters, and 02.3.2 D9 keeps every session value out of a log line.
    /// </summary>
    /// <param name="exception">The writer's own fault.</param>
    /// <returns>The type name, with the SQLSTATE appended for a database fault that carries one.</returns>
    private static string DescribeAuditFault(Exception exception) =>
        exception is DbException { SqlState: { Length: > 0 } sqlState }
            ? string.Concat(exception.GetType().Name, " (SQLSTATE ", sqlState, ")")
            : exception.GetType().Name;

    /// <summary>
    /// <c>POST /v1/auth/sessions/revoke-all</c>: 204 (idempotent) after revoking every live session of the
    /// caller, keeping the current one unless <c>keep_current</c> is false; with <c>false</c> the response also
    /// expires the session and antiforgery cookies, exactly like <c>logout</c>.
    /// </summary>
    private static async Task<Result> RevokeAllAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        ISessionRoutesStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, _, _, refusal) = await SessionProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure(refusal);
        }

        var (keepCurrent, bodyError) = await ReadKeepCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure(bodyError);
        }

        var (status, _) = await store
            .RevokeOthersAsync(proof, keepCurrent, cancellationToken)
            .ConfigureAwait(false);

        switch (status)
        {
            case SessionRouteStatus.Done:
                // The current row is gone with the rest, so its cookie names nothing: expire the pair only
                // after the write so a failed revocation never signs a live session out of its own credential.
                if (!keepCurrent)
                {
                    SessionCookieWriter.Expire(context);
                }

                return Result.Success(StatusCodes.Status204NoContent);

            case SessionRouteStatus.Unauthenticated:
                return Result.Failure(SessionErrors.Unauthenticated);

            // No `NotFound` branch: the definer behind this route has no addressed row to miss, it answers
            // `unauthenticated` when the proof names nothing live. A code this build does not map — an outage, or a
            // function/migration mismatch — is 503, never an invented answer about a session.
            default:
                return Result.Failure(SessionErrors.Unavailable);
        }
    }

    /// <summary>
    /// <c>POST /v1/auth/logout</c> (AC8): 204 after revoking the CURRENT row, and the response expires the
    /// session cookie and the antiforgery pair. A replay of the old value afterwards answers 401
    /// <c>session_revoked</c>, because revocation is a row state and not a key rotation (D9).
    /// </summary>
    private static async Task<Result> LogoutAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        ISessionRoutesStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, _, _, refusal) = await SessionProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure(refusal);
        }

        var status = await store.LogoutAsync(proof, cancellationToken).ConfigureAwait(false);

        switch (status)
        {
            case SessionRouteStatus.Done:
                SessionCookieWriter.Expire(context);

                return Result.Success(StatusCodes.Status204NoContent);

            case SessionRouteStatus.Unauthenticated:
                return Result.Failure(SessionErrors.Unauthenticated);

            // No `NotFound` branch: `session_revoke_current` addresses no row by id and answers `unauthenticated`
            // when the proof names nothing live, so any other code is an outage or a migration mismatch — 503.
            default:
                return Result.Failure(SessionErrors.Unavailable);
        }
    }

    /// <summary>
    /// The acting session's proof for the store, or the refusal to answer: the authenticator is asked once per
    /// request (it memoizes), and a live outcome without a proof is a composition bug, never a 401.
    /// </summary>
    /// <param name="context">The request whose cookie is authenticated.</param>
    /// <param name="authenticator">The deployment's authenticator.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// The stored HMAC of the live cookie value, the acting user id (the audit row's actor, and the one identity the
    /// database resolved from the cookie itself), the id of the row that answered (the caller's CURRENT session, so a
    /// route can tell "I just revoked my own credential" from "I revoked another row"), and the error to answer.
    /// </returns>
    private static async Task<(byte[] Proof, Guid? UserId, Guid? SessionId, Error? Refusal)> SessionProofAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var outcome = await authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);

        if (outcome.Refusal is { } refusal)
        {
            return ([], null, null, refusal);
        }

        return outcome.GetSessionProof() is { } proof
            ? (proof, outcome.UserId, outcome.SessionId, null)
            : ([], null, null, SessionErrors.Unavailable);
    }

    /// <summary>The org the caller names for an administrative revoke, or null when it names none (or not a uuid).</summary>
    /// <param name="context">The request.</param>
    private static Guid? OrgOf(HttpContext context)
    {
        // A single well-formed value only: a repeated header is not something a client should send, and the
        // safe reading of it is "no org named", which can only narrow the definer's authorisation.
        if (!context.Request.Headers.TryGetValue(OrgHeader, out var values) || values.Count != 1)
        {
            return null;
        }

        return Guid.TryParse(values[0], out var orgId) ? orgId : null;
    }

    /// <summary>
    /// The optional body of <c>revoke-all</c>: <c>{"keep_current": false}</c>, or no body at all, which keeps
    /// the current session (the safe default). Bounded read, JSON content type required, and an unparseable or
    /// oversized body is refused before the store is called.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>The flag to pass to the store, or the error to answer.</returns>
    private static async Task<(bool KeepCurrent, Error? Error)> ReadKeepCurrentAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is 0)
        {
            return (KeepCurrent: true, null);
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (KeepCurrent: true, TooLarge());
        }

        using var limited = new MemoryStream();
        var buffer = new byte[512];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (limited.Length > MaxBodyBytes)
            {
                return (KeepCurrent: true, TooLarge());
            }
        }

        if (limited.Length == 0)
        {
            return (KeepCurrent: true, null);
        }

        // The media type is a security boundary here too (the invite staging route's reasoning): a cross-site
        // form post cannot be labelled `application/json`, so no browser form reaches this body.
        if (!context.Request.HasJsonContentType())
        {
            return (KeepCurrent: true, Invalid("the request body must be application/json"));
        }

        RevokeAllSessionsRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<RevokeAllSessionsRequest>(limited.ToArray(), BodyOptions);
        }
        catch (JsonException)
        {
            return (KeepCurrent: true, Invalid("the request body is not valid JSON"));
        }

        // Absent member = the documented default: keep the current session. Only an explicit `false` kills it.
        return (KeepCurrent: body?.KeepCurrent ?? true, null);
    }

    private static Error TooLarge() => new(
        ApiErrorCode.PayloadTooLarge,
        "the request body is too large",
        StatusCodes.Status413PayloadTooLarge);

    private static Error Invalid(string message) => Error.Invalid(message, [new ErrorDetail("body", "invalid")]);

    /// <summary>
    /// The single Error line of a failed administrative audit (F48): the revoke committed, its row did not. The actor,
    /// the org and the writer fault's type (with its SQLSTATE when it is a database fault) are the whole line — the row
    /// id is a credential value under 02.3.2 D9 and never reaches a log line, so the org's chain and the operator's own
    /// record of the request are what reconcile the two — and no credential, hash, pepper value or exception text is
    /// ever a parameter here, while the caller's response says only <c>unavailable</c>.
    /// </summary>
    /// <remarks>
    /// The writer's fault enters as <see cref="DescribeAuditFault"/>'s text — its type name, plus the SQLSTATE of a
    /// database fault — never as the exception object: the logger's own formatter would append
    /// <c>exception.ToString()</c>, whose message can echo the failing statement and its parameter values.
    /// </remarks>
    /// <param name="logger">The route's logger (the module logs through source-generated delegates).</param>
    /// <param name="actorUserId">The admin whose session performed the revoke.</param>
    /// <param name="orgId">The org whose chain should have received the row.</param>
    /// <param name="writerFault">The fault's type, and its SQLSTATE when it is a database fault.</param>
    [LoggerMessage(
        EventId = 2413,
        Level = LogLevel.Error,
        Message = "An administrative session revoke committed but its audit row did not (actor {ActorUserId}, org {OrgId}, writer fault {WriterFault}); the caller is answered unavailable and the revoke stays committed")]
    private static partial void LogAuditFailed(
        ILogger logger,
        Guid actorUserId,
        Guid orgId,
        string writerFault);

    /// <summary>
    /// The one 404 of <c>DELETE /sessions/{s}</c> (AC6): foreign, unknown, other-org and already-dead ids all
    /// answer this exact envelope, so the route distinguishes nothing about other accounts' sessions.
    /// </summary>
    private static Error SessionNotFound { get; } = Error.NotFound("no such session");
}

/// <summary>The body of <c>GET /v1/auth/sessions</c>.</summary>
/// <param name="Items">The caller's live sessions, newest first, exactly one marked current.</param>
internal sealed record SessionListResponse(IReadOnlyList<SessionView> Items);

/// <summary>
/// One live session as the browser may see it (AC6): its id (so the same account can address it on another
/// device), the current flag, the three instants and the device metadata. No cookie value, no hash, no pepper
/// version — the store does not return them and this shape could not carry them.
/// </summary>
/// <param name="Id">The <c>session</c> row.</param>
/// <param name="Current">Whether this is the session that made the request.</param>
/// <param name="CreatedAt">When the row was minted.</param>
/// <param name="LastSeenAt">The last accepted request (the idle slide's anchor).</param>
/// <param name="ExpiresAt">The sliding deadline.</param>
/// <param name="AbsoluteExpiresAt">The hard deadline.</param>
/// <param name="Ip">The address the session was minted from, or null.</param>
/// <param name="UserAgent">The user agent the session was minted under, or null.</param>
internal sealed record SessionView(
    Guid Id,
    bool Current,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    string? Ip,
    string? UserAgent);

/// <summary>The body of <c>POST /v1/auth/sessions/revoke-all</c>: optional, defaulting to keeping the current session.</summary>
/// <param name="KeepCurrent">True to keep the session that made the request; false revokes it too.</param>
internal sealed record RevokeAllSessionsRequest(bool? KeepCurrent);
