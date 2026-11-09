using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The session-management routes: <c>GET /v1/auth/sessions</c>,
/// <c>DELETE /v1/auth/sessions/{s}</c>, <c>POST /v1/auth/sessions/revoke-all</c> and
/// <c>POST /v1/auth/logout</c>.
/// </summary>
/// <remarks>
/// <para>
/// PRE-TENANT, exactly like the linking routes they are mapped beside (00 §5.4.8): the caller is the
/// <c>__Host-evidgate_session</c> cookie and nothing else. <see cref="ISessionRequestAuthenticator"/> resolves
/// it in one statement and answers the lifecycle refusals (401 <c>unauthenticated</c> /
/// <c>session_expired</c> / <c>session_revoked</c>, 403 <c>account_disabled</c>) verbatim, so these routes never
/// turn a dead row into a generic 401. The acting user is always the presented cookie, never a route value: the
/// one route that names a second user (the administrative bulk revoke in the sibling part) names only the member
/// whose sessions are ended, and the definer judges that against the caller's own membership.
/// </para>
/// <para>
/// <b>The proof, never the cookie.</b> Every handler hands
/// <see cref="SessionRequestOutcome.GetSessionProof"/> — the stored HMAC of the live cookie value — to
/// <see cref="ISessionRoutesStore"/>, which passes it to the <c>SECURITY DEFINER</c> functions of migrations
/// 0048 and 0085 (the administrative bulk revoke's own). The raw cookie never leaves the HTTP layer, no
/// response carries it, its hash, the pepper or the pepper version, and nothing on this path logs or echoes
/// any of them (D9).
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
/// answer sets <c>Cache-Control: no-store</c>; none of these routes needs an <c>Idempotency-Key</c>
/// (revocation is naturally idempotent: revoking a revoked row changes nothing).
/// </para>
/// <para>
/// <b>An admin revoke is audited; an owner revoke is not (F48, F55).</b> <c>DELETE /sessions/{s}</c> hands the
/// <see cref="OrgHeader"/>'s org to the definer, which writes ONE <c>auth.session_revoke_admin</c> row into that
/// org's chain INSIDE its own transaction, and only when the addressed row belongs to ANOTHER member the caller
/// administers: actor the admin, object the revoked session, metadata its target user. The revoke and its row
/// therefore commit or roll back together — a client that walks away cannot drop the record of a committed
/// revoke, and a fault aborts the whole call (this route then answers 503 <c>unavailable</c> and the target
/// session stays live). A self revoke (the current row or a second device, header or not), a <c>logout</c>, a
/// <c>revoke-all</c> and every refusal write nothing: those are the account acting on its own sessions.
/// </para>
/// </remarks>
internal static partial class SessionEndpoints
{
    /// <summary>Every route's cache policy: a session answer is never reusable by a shared cache.</summary>
    private const string NoStore = "no-store";

    /// <summary>
    /// The header a member may use to name the org of an administrative revoke (02.6.2 §5). Mirrors
    /// <c>Evidgate.Api.Tenancy.TenantResolver.OrgHeader</c> — the module must not reference the host — and a
    /// test pins the two together so a rename fails instead of silently dropping the admin path.
    /// </summary>
    internal const string OrgHeader = "X-Evidgate-Org";

    /// <summary>Maps the session routes onto the <c>/v1/auth</c> group and returns it.</summary>
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

        MapAdminRevoke(group);

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

        var (proof, _, refusal) = await SessionProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
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
    /// The acting session's proof for the store, or the refusal to answer: the authenticator is asked once per
    /// request (it memoizes), and a live outcome without a proof is a composition bug, never a 401.
    /// </summary>
    /// <param name="context">The request whose cookie is authenticated.</param>
    /// <param name="authenticator">The deployment's authenticator.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// The stored HMAC of the live cookie value, the id of the row that answered (the caller's CURRENT session, so a
    /// route can tell "I just revoked my own credential" from "I revoked another row"), and the error to answer.
    /// </returns>
    private static async Task<(byte[] Proof, Guid? SessionId, Error? Refusal)> SessionProofAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var outcome = await authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);

        if (outcome.Refusal is { } refusal)
        {
            return ([], null, refusal);
        }

        return outcome.GetSessionProof() is { } proof
            ? (proof, outcome.SessionId, null)
            : ([], null, SessionErrors.Unavailable);
    }
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
