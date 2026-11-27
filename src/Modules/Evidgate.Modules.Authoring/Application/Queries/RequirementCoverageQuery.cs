using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>One page of a requirement's coverage (<c>GET …/authoring/requirements/{r}/coverage</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="RequirementId">The requirement from the route.</param>
/// <param name="AfterCode">The last case code of the previous page, or null for the first page.</param>
/// <param name="Limit">The page size the route already bounded.</param>
/// <param name="RequestError">The first edge validation error, answered after authorization.</param>
internal sealed record GetRequirementCoverageQuery(
    Guid ProjectId,
    Guid RequirementId,
    string? AfterCode,
    int Limit,
    Error? RequestError = null) : IQuery<Result<RequirementCoverageResponse>>;

/// <summary>
/// Handler of <see cref="GetRequirementCoverageQuery"/>. The answer never carries a result figure while no result
/// source exists: <c>result_source_available</c> is false and every case says <c>unavailable</c>.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement reads.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetRequirementCoverageQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer) : IQueryHandler<GetRequirementCoverageQuery, Result<RequirementCoverageResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementCoverageResponse>> Handle(
        GetRequirementCoverageQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetRequirementCoverageQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<RequirementCoverageResponse>(scopeError);
        }

        if (query.RequestError is { } requestError)
        {
            return Result.Failure<RequirementCoverageResponse>(requestError);
        }

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, query.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<RequirementCoverageResponse>(refusal);
        }

        var coverage = await writer.GetCoverageAsync(
            scopes.Require().OrgId,
            query.ProjectId,
            query.RequirementId,
            query.Limit,
            query.AfterCode,
            cancellationToken).ConfigureAwait(false);
        if (!coverage.IsSuccess)
        {
            return Result.Failure<RequirementCoverageResponse>(coverage.Error!);
        }

        var value = coverage.Value;

        return Result.Success(new RequirementCoverageResponse(
            value.RequirementId,
            value.HeadVersionId,
            value.HeadVersionNo,
            value.LinkedCaseCount,
            value.ApprovedCaseCount,
            value.LinkCount,
            value.StaleLinkCount,
            value.ResultSourceAvailable,
            [.. value.Targets.Select(target => new RequirementCoverageTargetDto(
                target.LinkId,
                target.VersionId,
                target.VersionNo,
                target.ChecklistItemId,
                target.ManualTestCaseId,
                target.Stale))],
            value.TargetsTruncated,
            [.. value.Cases.Select(item => new RequirementCoverageCaseDto(
                item.CaseId,
                item.Code,
                item.Title,
                item.State,
                item.ChecklistItemId,
                item.LinkPath,
                item.AutomatedCaseIds,
                item.ResultAvailability))],
            value.NextCode));
    }
}
