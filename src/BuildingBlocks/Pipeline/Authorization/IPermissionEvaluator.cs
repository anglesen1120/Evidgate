using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.BuildingBlocks.Pipeline.Authorization;

/// <summary>
/// The 02.2.1-T2 permission contract the C2 authorization stage consults (00 §5.5
/// C2 stage 4): whether <paramref name="principal"/> holds
/// <paramref name="permission"/> for the request's organization and project.
/// <para>
/// The evaluation runs inside the already-open tenant scope (C2 orders the tenant
/// transaction before authorization) because membership, role and role_permission
/// rows are tenant rows under RLS — the check is a query of the tenant's own data,
/// so it cannot see another org's roles even by mistake (02 §4.5). The
/// implementation is the Identity module's (<c>PermissionEvaluator</c>), registered
/// by <c>AddIdentityModule</c> in both entry points; <see cref="DenyAllPermissionEvaluator"/>
/// is the fail-closed default a host without the module keeps.
/// </para>
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>The verdict for <paramref name="permission"/>, with the 00 §9.2 code when denied.</summary>
    ValueTask<PermissionDecision> EvaluateAsync(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// The fail-closed default (01.4.2 plan §3, kept by 02.2.1-T2): every
/// pipeline-managed message is denied, so a host that forgot the Identity module —
/// or a caller the host never resolved a principal for — fails closed, never open.
/// Tests substitute their own <see cref="IPermissionEvaluator"/>; nobody registers a
/// permissive default.
/// </summary>
public sealed class DenyAllPermissionEvaluator : IPermissionEvaluator
{
    /// <inheritdoc />
    public ValueTask<PermissionDecision> EvaluateAsync(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(PermissionDecision.Denied(ApiErrorCode.Forbidden));
}
