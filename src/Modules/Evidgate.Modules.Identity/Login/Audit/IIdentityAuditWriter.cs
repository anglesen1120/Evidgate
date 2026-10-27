namespace Evidgate.Modules.Identity.Login.Audit;

/// <summary>
/// The identity audit writer of the federated login and linking paths (02.3.1-T2.2, Q5) and of the
/// administrative session revoke (F48): it appends one <c>auth.login</c> / <c>auth.link</c> /
/// <c>auth.link_move</c> row to the hash chain of every active org the signed-in user belongs to, and one
/// <c>auth.session_revoke_admin</c> row to the chain of the org a member's session was revoked in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a writer of its own.</b> The login callback and the linking confirm run before any tenant
/// exists — a user may belong to several orgs, or to none — so they cannot use the pipeline's
/// per-unit-of-work <see cref="Evidgate.BuildingBlocks.Persistence.Audit.IAuditTrail"/> flush, which
/// appends to the org of the one request scope. This seam opens the org's own scope and transaction,
/// records there through the same <see cref="Evidgate.BuildingBlocks.Persistence.Audit.IAuditTrail"/>
/// buffer every other audited action uses, and commits, exactly like
/// <c>Invites.MembershipBinder</c> opens one transaction per org for the bind itself.
/// </para>
/// <para>
/// <b>Fail-closed.</b> An implementation must let a database fault (a busy chain, a dead connection, a
/// vanished org) propagate: the caller maps it to 503 <c>unavailable</c> and issues no session, so a
/// login that cannot be audited does not happen. Rows of orgs that already committed in the same call
/// stay committed — each org is its own transaction — and the org that failed keeps nothing.
/// </para>
/// <para>
/// <b>Nothing sensitive.</b> The row names the user id, the provider name and allowlisted metadata; the
/// provider-verified address and the provider subject are never written, and nothing here logs.
/// </para>
/// </remarks>
internal interface IIdentityAuditWriter
{
    /// <summary>
    /// Appends one row for <paramref name="auditEvent"/> to the chain of every org in
    /// <paramref name="orgIds"/>, each in its own tenant transaction.
    /// </summary>
    /// <param name="auditEvent">The action, the user, the provider and the extra allowlisted metadata.</param>
    /// <param name="orgIds">
    /// The orgs whose chains receive the row — every active org of the user at the moment of the
    /// call. Duplicates are written once, in ascending <see cref="Guid"/> order; an empty list writes
    /// nothing (a user without an org has no chain to extend).
    /// </param>
    /// <param name="cancellationToken">Request abort; it also rolls back the org being written.</param>
    /// <returns>A task that completes once every org has committed.</returns>
    ValueTask WriteAsync(
        IdentityAuditEvent auditEvent,
        IReadOnlyCollection<Guid> orgIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends one <c>auth.session_revoke_admin</c> row for a member's session an org admin revoked, to the
    /// chain of <paramref name="orgId"/> — the org named by <c>X-Evidgate-Org</c>, whose
    /// <c>membership:write</c> the definer checked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called only after the definer answered <c>revoked</c>.</b> The handler writes nothing for a self
    /// revoke, a logout, a <c>revoke-all</c> or a refusal: those are the account acting on its own sessions, and
    /// they are already visible on the <c>session</c> row. This row records the one case an org cannot see from
    /// the victim's own account — an administrator ended someone else's session.
    /// </para>
    /// <para>
    /// <b>The row omits the target user.</b> <c>evidgate_resolve.session_revoke_one</c> answers a status only, and
    /// the target's user id is not obtainable without a migration; F48 therefore records the acting admin, the
    /// org and the revoked session (whose <c>session.user_id</c> names the member while the row exists) and not
    /// the target user. F55 returns <c>target_user_id</c> from the definer and writes this row inside the
    /// definer's transaction, which is also what removes the window described below.
    /// </para>
    /// <para>
    /// <b>Its own transaction, like every row this writer appends.</b> The org's scope is opened here because the
    /// definer's transaction has already committed; the row is not atomic with the revoke. A writer that throws
    /// therefore leaves the revoke committed and the audit row missing, and the caller answers 503 — the retry
    /// finds the session already dead, answers 404 and writes nothing, so that case stays unrecorded until the
    /// F55 retrofit. The exception propagates (fail closed).
    /// </para>
    /// <para>
    /// <b>Nothing sensitive.</b> The row carries the admin's user id, the session id and no metadata: never the
    /// cookie value, its HMAC, the pepper or its version, and nothing here logs.
    /// </para>
    /// </remarks>
    /// <param name="actorUserId">The admin whose session performed the revoke (<c>SessionRequestOutcome.UserId</c>).</param>
    /// <param name="orgId">The org the revoke was authorised in — the chain that receives the row.</param>
    /// <param name="sessionId">The <c>session</c> row that was revoked; it is the row's object id.</param>
    /// <param name="cancellationToken">Request abort; it also rolls back the org's transaction.</param>
    /// <returns>A task that completes once the org has committed.</returns>
    ValueTask WriteSessionRevokedAsync(
        Guid actorUserId,
        Guid orgId,
        Guid sessionId,
        CancellationToken cancellationToken);
}

/// <summary>What one identity audit entry says (02.3.1-T2.2, Q5).</summary>
/// <param name="Action">One of <see cref="IdentityAuditActions"/>: <c>auth.login</c>, <c>auth.link</c> or <c>auth.link_move</c>.</param>
/// <param name="UserId">The user whose chain participation is recorded; it is also the row's object id.</param>
/// <param name="Provider">The provider name (<c>google</c>, <c>github</c>) — a name, never an address or a subject.</param>
/// <param name="ExtraMetadata">
/// Further allowlisted context (for a move, the user the identity came from). Keys must be on
/// <see cref="Evidgate.BuildingBlocks.Persistence.Audit.AuditMetadata"/>'s allowlist or the write
/// refuses before touching any org; <c>provider</c> is always the event's own value.
/// </param>
internal sealed record IdentityAuditEvent(
    string Action,
    Guid UserId,
    string Provider,
    IReadOnlyDictionary<string, string>? ExtraMetadata = null);

/// <summary>The action strings the identity audit rows carry (Q5). Each is at most 128 characters, the database's bound.</summary>
internal static class IdentityAuditActions
{
    /// <summary>A federated login completed and a session was issued.</summary>
    internal const string Login = "auth.login";

    /// <summary>A second provider identity was linked to an existing user.</summary>
    internal const string Link = "auth.link";

    /// <summary>A second provider identity was moved from one user to another (explicit confirm only).</summary>
    internal const string LinkMove = "auth.link_move";

    /// <summary>
    /// An org admin revoked another member's session (F48): the row names the admin as actor, the org whose
    /// <c>membership:write</c> authorised it, and the revoked session as the object. A self revoke, a logout and a
    /// <c>revoke-all</c> record nothing here.
    /// </summary>
    internal const string SessionRevokeByAdmin = "auth.session_revoke_admin";
}
