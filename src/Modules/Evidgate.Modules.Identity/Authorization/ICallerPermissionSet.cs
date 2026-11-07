using Evidgate.BuildingBlocks.Pipeline.Authorization;

namespace Evidgate.Modules.Identity.Authorization;

/// <summary>
/// The caller's effective permission set, resolved by the very machinery the C2
/// authorization stage uses (W7-SEC-02, 00 §2 "`tenant_admin` never reads secret
/// values"). The write commands ask it what the caller may hand out: the
/// self-escalation rule refuses a grant that carries a code the caller does not hold,
/// and the set a role's permission set is compared against must be the set the next
/// request is evaluated with — a second, differently-scoped resolution path would be
/// the bug it is meant to prevent.
/// </summary>
/// <remarks>
/// <see cref="PermissionEvaluator"/> is the only implementation: the member path is one
/// statement over the same <c>membership_role → role → role_permission → permission</c>
/// join the per-permission verdict uses (<c>IdentityAccessSql.MemberPermissionSet</c>,
/// which shares its join with <c>MemberGrant</c>), and the service path is the token's
/// claim set bounded by the catalogue exactly as the evaluator bounds it. An operator
/// holds nothing until 02.8.1's support grant exists, and an unresolved or unknown kind
/// holds nothing: the set is empty rather than guessed, so a caller whose rows cannot be
/// read is refused rather than trusted.
/// </remarks>
internal interface ICallerPermissionSet
{
    /// <summary>
    /// Every code <paramref name="principal"/> effectively holds in
    /// <paramref name="orgId"/> right now, on the same terms as
    /// <see cref="IPermissionEvaluator.EvaluateAsync"/>: member roles of kind
    /// <c>member</c>, grants tied to their role's org, and catalogue codes that are
    /// <c>tenant_assignable</c> (TL T79).
    /// </summary>
    /// <param name="orgId">The request tenant (never the route's org, 00 §5.5 C2).</param>
    /// <param name="principal">The published caller.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>
    /// The caller's access; an empty set and no membership when no active membership resolves or the read fails.
    /// </returns>
    ValueTask<CallerAccess> ResolveAsync(
        Guid orgId,
        IRequestPrincipal principal,
        CancellationToken cancellationToken);
}

/// <summary>
/// What <see cref="ICallerPermissionSet"/> resolved: the caller's ACTIVE membership in the tenant and the codes it
/// effectively holds. Both halves come from one statement — the active-membership subquery and the grant join —
/// because a tenant-scoped write has to name the membership as its actor (<c>api_token.created_by</c> is
/// <c>(org_id, created_by) → membership (org_id, id)</c>) while the grant check has to read that same membership's
/// roles. Resolving the id on a second path could name one membership and authorize another.
/// </summary>
/// <param name="MembershipId">
/// The caller's active membership of the tenant — the row a <c>created_by</c> column names — or null when the
/// caller has none (a service identity, the operator stub, or a member whose row is not <c>active</c>). A command
/// that must record an actor row refuses the request rather than guessing one.
/// </param>
/// <param name="Codes">Every code the caller holds, never null; empty whenever <paramref name="MembershipId"/> is null.</param>
internal sealed record CallerAccess(Guid? MembershipId, IReadOnlySet<string> Codes);
