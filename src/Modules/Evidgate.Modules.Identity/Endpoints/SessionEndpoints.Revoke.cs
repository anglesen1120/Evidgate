using System.Diagnostics;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Identity.Endpoints;

internal static partial class SessionEndpoints
{
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
    /// <b>The row carries the call's correlation ids (F58).</b> The request id the edge put in the
    /// <see cref="AuditTrail.RequestIdBaggage"/> baggage item and the request activity's W3C trace id are handed to
    /// the definer from the SAME two sources <see cref="AuditTrail.Record"/> reads, so the SQL row and the
    /// application rows of one request agree on both. Nothing is validated here: the definer stores a value only
    /// when it is a bounded printable request id or a 32-hex trace id, and writes null for anything else, so a
    /// caller-supplied id can never abort a revoke.
    /// </para>
    /// <para>
    /// <b>Own or administered, and audited, all inside the definer (F48, F55).</b> The handler names the org and
    /// nothing else: <c>evidgate_resolve.session_revoke_one</c> resolves the acting user from the proof, checks
    /// <c>membership:write</c> and the target's membership in that same org, decides whether the addressed row is
    /// the caller's own, and — only when it is another member's — appends the <c>auth.session_revoke_admin</c> row
    /// in the same transaction. A self revoke of the current row or of a second device therefore writes no admin
    /// row even with a header present, and this handler cannot get that decision wrong because it never makes it.
    /// </para>
    /// <para>
    /// <b>The revoke and its row commit or roll back together (F55).</b> Any fault inside the definer — the org's
    /// busy chain, a lock timeout (<c>55P03</c>), a deadlock (<c>40P01</c>), an <c>audit_log</c> refusal — aborts the
    /// whole call, so the addressed session stays live: there is no committed-but-unrecorded revoke, and a caller
    /// that disconnects after the write cannot drop the record of it. The fault reaches this handler as an
    /// <see cref="NpgsqlException"/> and, when the request itself was not cancelled, the handler's own catch answers
    /// 503 <c>unavailable</c>: <c>SessionRoutesStore</c> — the store behind the revoke statement — catches nothing,
    /// so this catch is that statement's mapping (the authenticator's store already maps the faults of its own
    /// statement, before this code runs); a fault on a request the caller abandoned keeps propagating, because a
    /// caller that hung up is not an outage. Nothing here logs: the session id stays out of every log line,
    /// and the id is not itself a credential (the row this call writes stores it as its <c>object_id</c>, a
    /// reference to the revoked session, while the value the browser proved — the cookie — stays out of the row and
    /// the logs alike); with the revoke rolled back there is also no committed action to reconcile.
    /// </para>
    /// </remarks>
    private static async Task<Result> RevokeOneAsync(
        HttpContext context,
        string s,
        ISessionRequestAuthenticator authenticator,
        ISessionRoutesStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, currentSessionId, refusal) = await SessionProofAsync(context, authenticator, cancellationToken)
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

        return await RevokeNamedAsync(context, proof, sessionId, currentSessionId, store, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The revoke statement itself and its fault mapping, once the caller and the addressed id were judged.
    /// </summary>
    private static async Task<Result> RevokeNamedAsync(
        HttpContext context,
        byte[] proof,
        Guid sessionId,
        Guid? currentSessionId,
        ISessionRoutesStore store,
        CancellationToken cancellationToken)
    {
        SessionRouteStatus status;
        try
        {
            // The named org (or null) travels to the definer, which authorises the admin path and writes its row;
            // so do the call's correlation ids, read from the same two sources AuditTrail.Record reads (F58).
            var activity = Activity.Current;

            status = await store
                .RevokeOneAsync(
                    proof,
                    sessionId,
                    OrgOf(context),
                    activity?.GetBaggageItem(AuditTrail.RequestIdBaggage),
                    activity?.TraceId.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NpgsqlException) when (!cancellationToken.IsCancellationRequested)
        {
            // The definer aborted: a lock timeout (55P03), a deadlock (40P01) or the audit insert's own refusal.
            // The row is still live and, on the admin path, no audit row exists either. There is nothing committed
            // to reconcile, so this catch is what answers 503 — SessionRoutesStore maps nothing — and it takes no
            // log line.
            return Result.Failure(SessionErrors.Unavailable);
        }

        switch (status)
        {
            case SessionRouteStatus.Done:
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
    /// The one 404 of <c>DELETE /sessions/{s}</c> (AC6): foreign, unknown, other-org and already-dead ids all
    /// answer this exact envelope, so the route distinguishes nothing about other accounts' sessions.
    /// </summary>
    private static Error SessionNotFound { get; } = Error.NotFound("no such session");
}
