namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The membership lookup other modules consume (02.1.2-T1.3): "is this user a member
/// of this org, and with which roles". Runs inside the caller's
/// <c>ITenantScope</c>, so row level security decides visibility exactly as it does
/// for <see cref="IIdentityDirectory"/>. The write side is the module's commands —
/// there is deliberately no cross-module write interface, because every membership
/// mutation must run through the C2 pipeline (permission, idempotency, audit).
/// </summary>
public interface IMembership
{
    /// <summary>
    /// The membership of <paramref name="userId"/> in <paramref name="orgId"/>, or
    /// null when there is none (including another org's row: RLS makes it
    /// indistinguishable from a missing one, AC7).
    /// </summary>
    ValueTask<MembershipSummary?> FindAsync(
        Guid orgId,
        Guid userId,
        CancellationToken cancellationToken);
}
