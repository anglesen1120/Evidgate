using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Endpoints;
using Mediator;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Evidence.Application.Retention;

/// <summary>Read a project's retention window (<c>GET /v1/projects/{p}/retention</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
internal sealed record GetRetentionPolicyQuery(Guid ProjectId) : IQuery<Result<RetentionPolicy>>;

/// <summary>
/// Handler of <see cref="GetRetentionPolicyQuery"/>: the scope rule first, then one statement that reads the live
/// project together with its policy row, so an unknown, soft-deleted or other-tenant project reads no row at all and
/// answers the same 404. A project without a row is served the platform defaults from configuration and nothing is
/// written — the read path is the only caller that produces the defaults.
/// </summary>
[Permission(Permissions.Project.Read)]
internal sealed class GetRetentionPolicyQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IOptionsMonitor<RetentionLimits> limits)
    : IQueryHandler<GetRetentionPolicyQuery, Result<RetentionPolicy>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RetentionPolicy>> Handle(
        GetRetentionPolicyQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(GetRetentionPolicyQuery));

        if (EvidenceProjectScope.Project(query.ProjectId, principals.Current?.ProjectScope, tenant.Current?.ProjectId) is { } mismatch)
        {
            return Result.Failure<RetentionPolicy>(mismatch);
        }

        var row = await db.QuerySingleOrDefaultAsync<RetentionPolicyRow>(
            RetentionPolicySql.Read,
            new { org_id = scopes.Require().OrgId, project_id = query.ProjectId },
            cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            return Result.Failure<RetentionPolicy>(RetentionPolicyErrors.ProjectNotFound);
        }

        var configured = limits.CurrentValue;

        return Result.Success(new RetentionPolicy(
            row.ArtifactDays ?? configured.DefaultArtifactDays,
            row.MetadataDays ?? configured.DefaultMetadataDays));
    }
}

/// <summary>
/// One row of the read: the project is present, and the policy columns are null when the project has no row. The
/// record is nullable on purpose — that is how "the project exists but has no policy" is told apart from "no project".
/// </summary>
/// <param name="ArtifactDays">The stored artifact window, or null when no row exists.</param>
/// <param name="MetadataDays">The stored metadata window, or null when no row exists.</param>
internal sealed record RetentionPolicyRow(int? ArtifactDays, int? MetadataDays);

/// <summary>One stored row of the upsert: the identity the audit names and the values the database kept.</summary>
/// <param name="Id">The row's surrogate id (unchanged by an update).</param>
/// <param name="ArtifactDays">The stored artifact window.</param>
/// <param name="MetadataDays">The stored metadata window.</param>
internal sealed record RetentionPolicyUpsertedRow(Guid Id, int ArtifactDays, int MetadataDays);

/// <summary>The statements of the retention routes; both run in the request's tenant transaction.</summary>
internal static class RetentionPolicySql
{
    /// <summary>
    /// The live project with its policy row, keyed by the table's own upsert key <c>(org_id, project_id)</c>, so the
    /// lookup is one unique-index probe and never a scan. The project row is the outer relation: an unknown,
    /// other-tenant or soft-deleted project yields no row, which is the single 404 of both verbs.
    /// </summary>
    internal const string Read = """
        select rp.artifact_days, rp.metadata_days
          from public.project p
          left join public.retention_policy rp
            on rp.org_id = p.org_id
           and rp.project_id = p.id
         where p.org_id = @org_id
           and p.id = @project_id
           and p.status <> 'deleted'
        """;

    /// <summary>
    /// The upsert of the write path: one statement keyed by <c>(org_id, project_id)</c>, so two concurrent writes
    /// converge on one row and the last committed one wins instead of raising a duplicate key. `updated_at` and
    /// `version` are left to the table's convention trigger; the returning clause answers the values the database
    /// actually stored.
    /// </summary>
    internal const string Upsert = """
        insert into public.retention_policy (id, org_id, project_id, artifact_days, metadata_days)
        values (@id, @org_id, @project_id, @artifact_days, @metadata_days)
        on conflict (org_id, project_id)
        do update set artifact_days = excluded.artifact_days,
                      metadata_days = excluded.metadata_days
        returning id, artifact_days, metadata_days
        """;

    /// <summary>
    /// The live-project check of the write path, taking the row <c>FOR SHARE</c>: the check and the upsert run under
    /// this lock, so a concurrent soft delete cannot retire the project between them. Another tenant's project reads
    /// no row under row level security, which is the same 404 an unknown id gets.
    /// </summary>
    internal const string ProjectExistsForShare = """
        select 1
          from public.project
         where id = @project_id
           and status <> 'deleted'
           for share
        """;
}
