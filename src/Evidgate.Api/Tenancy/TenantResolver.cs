using Evidgate.BuildingBlocks.Persistence.Resolve;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Api.Tenancy;

/// <summary>The tenant a request runs in, resolved before the C2 pipeline opens its scope.</summary>
/// <param name="OrgId">The org.</param>
/// <param name="ProjectId">The project a service token is bound to, if any.</param>
public sealed record ResolvedTenant(Guid OrgId, Guid? ProjectId);

/// <summary>
/// The resolution matrix of 02.6.2 §5. It never trusts a tenant the caller merely names:
/// a member's org is accepted only if <c>evidgate_resolve.member_org</c> confirms an active membership
/// (read per request, no cache), and a service token only ever runs in the org its claims
/// fix. The scope opens strictly after this answers (T1).
/// </summary>
/// <param name="members">The pre-tenant membership lookup.</param>
public sealed class TenantResolver(IMemberOrgResolver members)
{
    /// <summary>The header a member may use to pick an org on routes without <c>{o}</c> (e.g. <c>/projects/{p}</c>).</summary>
    public const string OrgHeader = "X-Evidgate-Org";

    /// <summary>Resolves the tenant for a caller that asked for <paramref name="requestedOrg"/> (route value or header).</summary>
    /// <param name="caller">The authenticated caller.</param>
    /// <param name="requestedOrg">The org the request names, if it names one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The tenant, or the 00 §9.2 error to answer.</returns>
    public async ValueTask<Result<ResolvedTenant>> ResolveAsync(
        AuthenticatedCaller caller,
        Guid? requestedOrg,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        switch (caller.Principal.Kind)
        {
            case PrincipalKind.Member when caller.Principal.UserId is { } userId:
                return await ResolveMemberAsync(userId, requestedOrg, cancellationToken).ConfigureAwait(false);

            case PrincipalKind.ServicePrincipal:
                return ResolveService(caller, requestedOrg);

            default:
                // An operator has no standing tenant access (T76), and a member without
                // a user id is malformed: neither may open a tenant scope here.
                return Result.Failure<ResolvedTenant>(Error.Forbidden("this credential cannot act inside an organization"));
        }
    }

    private async ValueTask<Result<ResolvedTenant>> ResolveMemberAsync(
        Guid userId,
        Guid? requestedOrg,
        CancellationToken cancellationToken)
    {
        if (requestedOrg is not { } org)
        {
            return Result.Failure<ResolvedTenant>(Error.Invalid(
                $"name the organization: use /orgs/{{o}} or the {OrgHeader} header",
                [new ErrorDetail(OrgHeader, "required")]));
        }

        var membership = await members.MemberOrgAsync(userId, org, cancellationToken).ConfigureAwait(false);

        // 403 for a non-member, whether or not the org exists: the answer must not
        // tell a caller which org ids are real (AC5).
        return membership is not null
            ? Result.Success(new ResolvedTenant(org, ProjectId: null))
            : Result.Failure<ResolvedTenant>(Error.Forbidden("no active membership in this organization"));
    }

    private static Result<ResolvedTenant> ResolveService(AuthenticatedCaller caller, Guid? requestedOrg)
    {
        if (caller.BoundOrgId is not { } bound)
        {
            // A service token with no org claim is malformed, not merely unauthorised.
            return Result.Failure<ResolvedTenant>(new Error(
                ApiErrorCode.Unauthenticated,
                "the token names no organization",
                StatusCodes.Status401Unauthorized));
        }

        return requestedOrg is { } org && org != bound
            ? Result.Failure<ResolvedTenant>(Error.NotFound("no such organization is visible to this tenant"))
            : Result.Success(new ResolvedTenant(bound, caller.BoundProjectId));
    }
}
