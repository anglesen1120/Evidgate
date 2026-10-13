using Dapper;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>Read one run's state machine position (<c>GET /v1/runs/{r}</c>, 05.3.1 §5, AC7).</summary>
/// <param name="RunId">The run from the route (<c>{r}</c>).</param>
internal sealed record GetRunQuery(Guid RunId) : IQuery<Result<RunStateDto>>;

/// <summary>
/// Handler of <see cref="GetRunQuery"/>: one Dapper read over the ambient tenant scope (00 §5.5 C1/C3/C5), so RLS
/// scopes the row and another tenant's run is the same <c>404 not_found</c> as an unknown id. A credential scoped to
/// another project is refused <c>403 project_scope_mismatch</c>, exactly like the module's other read route
/// (05.1.2 §5); reads never write.
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class GetRunQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetRunQuery, Result<RunStateDto>>
{
    /// <summary>
    /// The one statement of the read: the run's own columns by id. <c>org_id</c> is left to RLS (the row is readable
    /// only inside the caller's tenant scope, and the policy is <c>FORCE</c>d, 0031), so an unknown id and another
    /// tenant's id take the same branch. The <c>project</c> join of the environment-revision read is deliberately
    /// absent: the run's other readers (<c>RunPlanReader</c>, <c>RunEventScopeReader</c>) resolve a run without
    /// filtering a deleted project either.
    /// </summary>
    private const string RunStateById = """
        select r.id as run_id, r.project_id, r.state, r.current_evidence_revision_id, r.version, r.purpose
          from public.run r
         where r.id = @run_id
        """;

    /// <inheritdoc />
    public async ValueTask<Result<RunStateDto>> Handle(GetRunQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(GetRunQuery));

        var row = await db.QuerySingleOrDefaultAsync<RunStateRow>(
                RunStateById,
                new { run_id = query.RunId },
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null || OrchestratorScope.Project(row.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<RunStateDto>(RunNotFound);
        }

        return Result.Success(new RunStateDto(
            row.RunId,
            row.State,
            row.CurrentEvidenceRevisionId,
            row.Version,
            row.Purpose));
    }

    /// <summary>
    /// 404 <c>not_found</c>: no run with that id is visible to this tenant — the same code, message and body for an
    /// unknown id and for another tenant's (the RLS branch above), so the answer is no existence oracle (00 §5.4).
    /// </summary>
    private static Error RunNotFound => Error.NotFound("no run with that id is visible to this tenant");

    /// <summary>One row as <see cref="RunStateById"/> returns it (snake_case columns mapped by Dapper).</summary>
    private sealed class RunStateRow
    {
        public Guid RunId { get; set; }

        public Guid ProjectId { get; set; }

        public string State { get; set; } = string.Empty;

        public Guid? CurrentEvidenceRevisionId { get; set; }

        public int Version { get; set; }

        public string Purpose { get; set; } = string.Empty;
    }
}
