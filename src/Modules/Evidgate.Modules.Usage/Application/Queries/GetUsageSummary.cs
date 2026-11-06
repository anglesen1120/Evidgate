using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Usage.Application.Queries;

/// <summary>
/// <c>GET /v1/orgs/{o}/usage/summary</c>: the current period's counters against quota for the org and, when the request
/// names one, a project. The period comes from the server clock, the same one the quota gate reads, so the answer
/// describes the counter the gate would refuse against.
/// </summary>
/// <param name="OrgId">The org the route names; it must be the request's tenant.</param>
/// <param name="ProjectId">The project the request named, or null for the org-wide answer.</param>
internal sealed record GetUsageSummaryQuery(Guid OrgId, Guid? ProjectId) : IQuery<Result<UsageSummaryDto>>;

/// <summary>
/// Handler of <see cref="GetUsageSummaryQuery"/>: the C2 project-scope rule, then one statement that reads the period's
/// counters, roll-up totals and the requested project's visibility together. The organization resolves through the
/// pipeline's tenant context, so an org outside the caller's visibility never reaches this handler.
/// </summary>
/// <remarks>
/// The statement answers "is the named project in this org?" by itself, so a foreign id is the same 404 an unknown one
/// is and the read is never an existence oracle. Only the current period's counter can contribute a limit: a month
/// whose counter does not exist yet reports the roll-up total with a null limit, because carrying the previous month's
/// limit is the gate's write-time job and a read that invented the same carry could disagree with it.
/// </remarks>
[Permission(Permissions.Usage.Read)]
internal sealed class GetUsageSummaryQueryHandler(
    UsageReadStore store,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    TimeProvider time)
    : IQueryHandler<GetUsageSummaryQuery, Result<UsageSummaryDto>>
{
    /// <summary>The 404 of a project this org does not hold; unknown and foreign ids share it.</summary>
    internal static readonly Error ProjectNotFound = Error.NotFound("no such project");

    /// <inheritdoc />
    public async ValueTask<Result<UsageSummaryDto>> Handle(
        GetUsageSummaryQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Usage,
            nameof(GetUsageSummaryQuery));

        if (query.OrgId != scopes.Require().OrgId)
        {
            return Result.Failure<UsageSummaryDto>(Error.NotFound("no such organization"));
        }

        if (query.ProjectId is { } projectId
            && UsageProjectScope.Project(projectId, principals.Current?.ProjectScope, tenant.Current?.ProjectId)
                is { } mismatch)
        {
            return Result.Failure<UsageSummaryDto>(mismatch);
        }

        var now = time.GetUtcNow();
        var period = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var rows = await store
            .ReadSummaryAsync(scopes.Require().OrgId, period, query.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        var summary = UsageSummaryComposition.Compose(period, query.ProjectId, now, rows);

        return summary is null
            ? Result.Failure<UsageSummaryDto>(ProjectNotFound)
            : Result.Success(summary);
    }
}

/// <summary>
/// The module's project-scope rule for the summary: a credential bound to another project, or a request already
/// narrowed to another project, is outside the scope and is refused 403 <c>project_scope_mismatch</c> before any row is
/// read. A project the org does not hold is the route's 404 instead (the statement's marker row), so the refusal is
/// never an oracle for another org's ids.
/// </summary>
internal static class UsageProjectScope
{
    /// <summary>Null when the addressed project is inside every scope the request carries.</summary>
    /// <param name="projectId">The project the request named.</param>
    /// <param name="credentialScope">The project the credential is bound to, if any.</param>
    /// <param name="requestProject">The project the request's tenant context is narrowed to, if any.</param>
    internal static Error? Project(Guid projectId, Guid? credentialScope, Guid? requestProject)
    {
        if (credentialScope is { } tokenScope && tokenScope != projectId)
        {
            return Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the project is outside the credential's scope");
        }

        return requestProject is { } narrowed && narrowed != projectId
            ? Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the project is outside the request's scope")
            : null;
    }
}
