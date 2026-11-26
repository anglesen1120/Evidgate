using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Identity.Application.Queries;

/// <summary>
/// The caller's own effective grants in the org (<c>GET /v1/me/permissions</c>).
/// </summary>
/// <param name="ProjectId">The project the caller asked about, echoed back; null when none was asked.</param>
/// <param name="RequestError">
/// The edge's refusal of a malformed <c>project</c>, carried here so the pipeline's 401/403 still
/// precede this 422; the handler returns it first.
/// </param>
internal sealed record GetMyPermissionsQuery(Guid? ProjectId, Error? RequestError = null)
    : IQuery<Result<MyPermissionsResponse>>;

/// <summary>
/// Handler of <see cref="GetMyPermissionsQuery"/>. The codes come from <see cref="ICallerPermissionSet"/>,
/// the join the C2 evaluator uses, so this answer can never disagree with what the next request would be
/// granted. A member's roles carry no project dimension in V1, so a project changes nothing but the echo.
/// </summary>
[AuthenticatedMember]
internal sealed class GetMyPermissionsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    ICallerPermissionSet callerPermissions)
    : IQueryHandler<GetMyPermissionsQuery, Result<MyPermissionsResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MyPermissionsResponse>> Handle(
        GetMyPermissionsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.RequestError is { } invalid)
        {
            return Result.Failure<MyPermissionsResponse>(invalid);
        }

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(GetMyPermissionsQuery));

        var orgId = scopes.Require().OrgId;
        if (principals.Current is not { UserId: { } userId } caller)
        {
            return Result.Failure<MyPermissionsResponse>(NoMembership());
        }

        var access = await callerPermissions.ResolveAsync(orgId, caller, cancellationToken).ConfigureAwait(false);
        if (access.MembershipId is null)
        {
            return Result.Failure<MyPermissionsResponse>(NoMembership());
        }

        var roles = await db.QuerySingleOrDefaultAsync<MemberRoleCodesRow>(
            IdentityAccessSql.MemberRoleCodes,
            new { org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);

        return Result.Success(new MyPermissionsResponse(
            orgId,
            query.ProjectId,
            [.. roles?.Codes ?? []],
            [.. access.Codes.Order(StringComparer.Ordinal)]));
    }

    private static Error NoMembership() => PermissionDecision.Denied(ApiErrorCode.NoMembership).ToError();
}
