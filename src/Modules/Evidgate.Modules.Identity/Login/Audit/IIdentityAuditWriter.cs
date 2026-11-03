namespace Evidgate.Modules.Identity.Login.Audit;

/// <summary>
/// The identity audit writer of the federated login and linking paths (02.3.1-T2.2, Q5): it appends one
/// <c>auth.login</c> / <c>auth.link</c> / <c>auth.link_move</c> row to the hash chain of every active org the
/// signed-in user belongs to. The administrative session revoke is NOT written here: its row is appended by
/// <c>evidgate_resolve.session_revoke_one</c> inside that definer's own transaction (F55), so the record cannot
/// outlive or precede the revoke it describes.
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
}

/// <summary>What one identity audit entry says (02.3.1-T2.2, Q5).</summary>
/// <param name="Action">
/// One of <see cref="IdentityAuditActions"/>: a federated login, a link, a link move, or the account's own
/// language change.
/// </param>
/// <param name="UserId">The user whose chain participation is recorded; it is also the row's object id.</param>
/// <param name="Provider">
/// The provider name (<c>google</c>, <c>github</c>) — a name, never an address or a subject — or null for a row
/// that no provider took part in (an own-profile change), which then carries no <c>provider</c> key at all.
/// </param>
/// <param name="ExtraMetadata">
/// Further allowlisted context (for a move, the user the identity came from). Keys must be on
/// <see cref="Evidgate.BuildingBlocks.Persistence.Audit.AuditMetadata"/>'s allowlist or the write
/// refuses before touching any org; <c>provider</c> is always the event's own value.
/// </param>
internal sealed record IdentityAuditEvent(
    string Action,
    Guid UserId,
    string? Provider,
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

    /// <summary>The account changed its own preferred language: the actor and the object are the same user.</summary>
    internal const string LocaleChanged = "user.locale_changed";
}
