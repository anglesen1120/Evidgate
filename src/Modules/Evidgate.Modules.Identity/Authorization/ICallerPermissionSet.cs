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
    /// <returns>The caller's set; empty when no active membership resolves or the read fails.</returns>
    ValueTask<IReadOnlySet<string>> ResolveAsync(
        Guid orgId,
        IRequestPrincipal principal,
        CancellationToken cancellationToken);
}
