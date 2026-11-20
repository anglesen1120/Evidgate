using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Application;

/// <summary>
/// The refusals of the three support-grant routes, in one place so their codes cannot drift apart. The approver
/// code is the same whether the pre-check or the database trigger refuses: a client must not be able to tell which
/// check ran, and a lost race must not turn into a 500.
/// </summary>
internal static class SupportGrantErrors
{
    /// <summary>403: the caller holds <c>support:approve</c> but is not an active built-in <c>tenant_admin</c>.</summary>
    public static Error ApproverNotTenantAdmin { get; } = Error.Denied(
        "approver_not_tenant_admin",
        "the approver must be an active tenant_admin of the grant's organization");

    /// <summary>422: the requester and the approver must be different people; revoke has no such rule.</summary>
    public static Error ApproverMustDiffer { get; } =
        Error.Unprocessable("approver_must_differ", "the grant's requester cannot approve it");

    /// <summary>409: the grant is no longer filed — already approved, revoked, or past its expiry.</summary>
    public static Error GrantNotRequested { get; } = Error.Conflict(
        "grant_not_requested",
        "the support grant is not awaiting approval");

    /// <summary>409: the grant was already revoked; a second revocation changes nothing.</summary>
    public static Error GrantAlreadyRevoked { get; } = Error.Conflict(
        "grant_already_revoked",
        "the support grant is already revoked");

    /// <summary>404: no grant of the caller's tenant has that id.</summary>
    public static Error NotFound { get; } = Error.NotFound("no support grant with that id is visible to this tenant");
}
