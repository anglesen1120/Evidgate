namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// One membership that <see cref="IMembershipBinder.BindAsync"/> activated in this
/// call (02.1.2-T2, T75): the pending invite of that org became <c>active</c> for the
/// signing-in user, and that org's transaction is committed.
/// <para>
/// A result carries no invite token, secret or address — the caller only needs to
/// know which orgs the user now belongs to (02.3.1-T2 issues the session; a client
/// refreshes <c>GET /v1/orgs</c>). A row that could not be bound is absent from the
/// list rather than reported: the causes (unknown prefix, wrong secret, wrong email,
/// expired, already consumed, user already a member) must stay indistinguishable
/// (T75, no oracle).
/// </para>
/// </summary>
/// <param name="OrgId">The org whose pending membership was activated.</param>
/// <param name="MembershipId">The membership row that changed from <c>pending</c> to <c>active</c>.</param>
/// <param name="Via">How it bound: <c>invite_secret</c> or <c>verified_domain</c> (the org's opt-in auto-bind).</param>
public sealed record BoundMembership(Guid OrgId, Guid MembershipId, string Via);
