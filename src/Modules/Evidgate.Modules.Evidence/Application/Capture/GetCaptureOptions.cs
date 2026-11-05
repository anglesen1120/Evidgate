using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Mediator;

namespace Evidgate.Modules.Evidence.Application.Capture;

/// <summary>Read a project's capture options for every artifact kind (<c>GET /v1/projects/{p}/artifact-kind-policy</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
internal sealed record GetCaptureOptionsQuery(Guid ProjectId) : IQuery<Result<IReadOnlyList<CaptureOptionsKind>>>;

/// <summary>
/// Handler of <see cref="GetCaptureOptionsQuery"/>: the scope rule first, then the project's own row, so an unknown,
/// soft-deleted or other-tenant project answers the same 404 instead of a page of defaults that would read as "this
/// project captures everything". A project that exists but declares nothing is served every kind's effective default and
/// nothing is written.
/// </summary>
[Permission(Permissions.Project.Read)]
internal sealed class GetCaptureOptionsQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetCaptureOptionsQuery, Result<IReadOnlyList<CaptureOptionsKind>>>
{
    /// <summary>The project's status, so a missing, erased or other-tenant project is the one 404.</summary>
    private const string ProjectStatusSql = """
        select status
          from public.project
         where org_id = @org_id
           and id = @project_id
        """;

    /// <summary>The <c>project.status</c> a read refuses: a project being deleted is not a configuration surface.</summary>
    private const string ProjectDeleted = "deleted";

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<CaptureOptionsKind>>> Handle(
        GetCaptureOptionsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(GetCaptureOptionsQuery));

        var context = tenant.Require();
        if (EvidenceProjectScope.Project(query.ProjectId, principals.Current?.ProjectScope, context.ProjectId) is { } mismatch)
        {
            return Result.Failure<IReadOnlyList<CaptureOptionsKind>>(mismatch);
        }

        var parameters = new { org_id = context.OrgId, project_id = query.ProjectId };

        var status = await db.QuerySingleOrDefaultAsync<string>(ProjectStatusSql, parameters, cancellationToken)
            .ConfigureAwait(false);
        if (status is null || string.Equals(status, ProjectDeleted, StringComparison.Ordinal))
        {
            return Result.Failure<IReadOnlyList<CaptureOptionsKind>>(EvidenceErrors.ProjectNotFound);
        }

        return Result.Success(await CaptureOptionsRead.LoadAsync(db, context.OrgId, query.ProjectId, cancellationToken)
            .ConfigureAwait(false));
    }
}
