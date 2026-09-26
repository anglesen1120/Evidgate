using Evidgate.BuildingBlocks.Pipeline.Tenancy;

namespace Evidgate.BuildingBlocks.Pipeline.Authorization;

/// <summary>
/// The 02.6.2 permission contract the C2 authorization behaviour checks (00 §5.5
/// C2 stage 4): whether the principal behind <paramref name="context"/> holds the
/// handler's declared <c>[Permission]</c> for the request's organization and
/// project. Membership and role rows are tenant rows under RLS, so the check runs
/// inside the already-open tenant scope — it is a query of the tenant's own data.
/// </summary>
public interface IPermissionChecker
{
    /// <summary>True when <paramref name="permission"/> is held for the request's org/project.</summary>
    ValueTask<bool> IsGrantedAsync(string permission, IRequestTenantContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The fail-closed default (01.4.2 plan §3): until 02.6.2's permission catalogue
/// lands, every pipeline-managed message is denied — a host that forgot the real
/// checker fails closed, never open. Tests substitute their own
/// <see cref="IPermissionChecker"/>; nobody registers a permissive default.
/// </summary>
public sealed class DenyAllPermissionChecker : IPermissionChecker
{
    /// <inheritdoc />
    public ValueTask<bool> IsGrantedAsync(string permission, IRequestTenantContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);
}
