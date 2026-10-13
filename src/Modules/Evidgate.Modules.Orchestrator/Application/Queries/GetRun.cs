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
/// scopes the row and another tenant's run is the same <c>404 not_found</c> as an unknown id. The body reports the
/// run's state machine position and the identity it records (05.2.2-T1: the <c>ref</c> block and
/// <c>unverified_ref</c>; <c>advisory_only</c> is 05.2.2-T3.2's). The route names the run
/// by id alone, so a project-scoped credential asking for a sibling project's run of the same org is that same
/// <c>404 not_found</c>: <see cref="OrchestratorScope.Project"/>'s scope mismatch is mapped to it below, never to
/// <c>403 project_scope_mismatch</c> — the same shape as the module's other read route
/// (<c>GetEnvironmentRevisionQueryHandler</c>, EnvironmentRevisions.cs:246). The only 403 here is the permission
/// stage's: <c>run:read</c> is outside the service-role ceiling (<c>PermissionEvaluator.Service</c>), so a
/// <c>ci_producer</c>/service credential can never hold it and is refused <c>403 forbidden</c>. Reads never write.
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class GetRunQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetRunQuery, Result<RunStateDto>>
{
    /// <summary>
    /// The one statement of the read: the run's own columns by id, including the identity block 05.2.2-T1 adds
    /// (<c>ref_kind</c>, <c>ref_name</c>, <c>pr_number</c>, <c>head_sha</c>, <c>base_sha</c>, <c>trigger_event</c>,
    /// <c>is_fork</c>, <c>unverified_ref</c>). <c>org_id</c> is left to RLS (the row is readable only inside the
    /// caller's tenant scope, and the policy is <c>FORCE</c>d, 0031), so an unknown id and another tenant's id take the
    /// same branch. The <c>project</c> join of the environment-revision read is deliberately absent: the run's other
    /// readers (<c>RunPlanReader</c>, <c>RunEventScopeReader</c>) resolve a run without filtering a deleted project
    /// either.
    /// </summary>
    private const string RunStateById = """
        select r.id as run_id, r.project_id, r.state, r.current_evidence_revision_id, r.version, r.purpose,
               r.ref_kind, r.ref_name, r.pr_number, r.head_sha, r.base_sha, r.trigger_event, r.is_fork, r.unverified_ref
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
            row.Purpose,
            new RunRef(
                row.RefKind,
                row.RefName,
                row.PrNumber,
                row.HeadSha,
                row.BaseSha,
                row.TriggerEvent,
                row.IsFork),
            row.UnverifiedRef));
    }

    /// <summary>
    /// 404 <c>not_found</c>: no run with that id is visible to this tenant — the same code, message and body for an
    /// unknown id, for another tenant's run (the RLS branch above) and for a sibling project's run of the same org
    /// (the <see cref="OrchestratorScope.Project"/> mismatch, mapped here), so the answer is no existence oracle
    /// (00 §5.4).
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

        public string? RefKind { get; set; }

        public string? RefName { get; set; }

        public int? PrNumber { get; set; }

        public string? HeadSha { get; set; }

        public string? BaseSha { get; set; }

        public string? TriggerEvent { get; set; }

        public bool IsFork { get; set; }

        public bool UnverifiedRef { get; set; }
    }
}
