using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Manifests;

/// <summary>
/// Upload a manifest (<c>POST /v1/projects/{p}/manifests</c>, 05.1.1 §5). <paramref name="Body"/> already matches
/// <c>manifest.v1.json</c>: the route validates it before anything binds (00 §5.5 edge).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The schema-valid manifest.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record UploadManifestCommand(Guid ProjectId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<ManifestAccepted>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="UploadManifestCommand"/>: the pure checks (secret literals, matrix coverage) first, then the
/// catalogue in three bounded statements (suite revision, every case revision in one, project + environment revision in one),
/// then one insert that dedupes on the content hash. Every refusal is 422 <c>manifest_invalid</c> with indexed details and
/// increments <c>evidgate_manifest_rejected_total{reason}</c>; no submitted value is echoed.
/// </summary>
[AuditedAction("manifest.upload")]
[Permission(Permissions.Run.Create)]
internal sealed class UploadManifestCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ICatalogContracts catalog,
    IAuditTrail audit)
    : ICommandHandler<UploadManifestCommand, Result<ManifestAccepted>>
{
    internal const string UnknownCaseRevision = "unknown_case_revision";
    internal const string UnknownSuiteRevision = "unknown_suite_revision";
    internal const string UnknownEnvironmentRevision = "unknown_environment_revision";
    internal const string InvalidValue = "invalid_value";

    /// <inheritdoc />
    public async ValueTask<Result<ManifestAccepted>> Handle(UploadManifestCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(UploadManifestCommand));

        if (OrchestratorScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<ManifestAccepted>(mismatch);
        }

        if (ManifestVerifier.HasNul(command.Body))
        {
            return Refuse(InvalidValue, [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        var (facts, refusal) = ManifestVerifier.Verify(command.Body);
        if (refusal is not null)
        {
            return Refuse(refusal.Reason, refusal.Details);
        }

        string hash;
        try
        {
            hash = CanonicalHash.Compute(command.Body);
        }
        catch (Exception exception) when (exception is JsonCanonicalizationException or ArgumentException)
        {
            // A string the canonical form (and jsonb) cannot carry: a lone surrogate, a NUL.
            return Refuse(InvalidValue, [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        var targets = await db.QuerySingleOrDefaultAsync<TargetsRow>(
            OrchestratorSql.ManifestTargets,
            new { project_id = command.ProjectId, environment_revision_id = facts!.EnvironmentRevisionId },
            cancellationToken).ConfigureAwait(false);
        if (targets is null || !targets.ProjectExists)
        {
            return Result.Failure<ManifestAccepted>(OrchestratorErrors.ProjectNotFound);
        }

        if (!await catalog.SuiteRevisionExistsAsync(command.ProjectId, facts.SuiteRevisionId, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(UnknownSuiteRevision, [new ErrorDetail("/suite_revision_id", UnknownSuiteRevision)]);
        }

        if (!targets.EnvironmentKnown)
        {
            return Refuse(UnknownEnvironmentRevision, [new ErrorDetail("/environment_revision_id", UnknownEnvironmentRevision)]);
        }

        var known = await catalog.FindCaseRevisionsAsync(command.ProjectId, facts.CaseRevisionIds, cancellationToken).ConfigureAwait(false);
        if (known.Count != facts.CaseRevisionIds.Count)
        {
            // One detail per planned execution that names an unknown revision (the coverage check made every planned
            // case a matrix case), then the matrix entries themselves for an empty plan.
            var details = new List<ErrorDetail>();
            for (var index = 0; index < facts.PlannedCaseRevisionIds.Count && details.Count < ManifestVerifier.MaxDetails; index++)
            {
                if (!known.Contains(facts.PlannedCaseRevisionIds[index]))
                {
                    details.Add(new ErrorDetail($"/planned_executions/{index}/case_revision_id", UnknownCaseRevision, index));
                }
            }

            for (var index = 0; index < facts.CaseRevisionIds.Count && details.Count < ManifestVerifier.MaxDetails; index++)
            {
                if (!known.Contains(facts.CaseRevisionIds[index]))
                {
                    details.Add(new ErrorDetail($"/matrix/case_revision_ids/{index}", UnknownCaseRevision));
                }
            }

            return Refuse(UnknownCaseRevision, details);
        }

        var orgId = scopes.Require().OrgId;
        var actor = AuditActorResolver.From(principals.Current);
        var body = command.Body;
        var insertedId = await db.ScalarAsync<Guid?>(
            OrchestratorSql.InsertManifest,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                project_id = command.ProjectId,
                manifest_hash = hash,
                suite_revision_id = facts.SuiteRevisionId,
                environment_revision_id = facts.EnvironmentRevisionId,
                shard_plan = body.GetProperty("shard_plan").GetRawText(),
                matrix = body.GetProperty("matrix").GetRawText(),
                env_needs = body.GetProperty("env_needs").GetRawText(),
                planned_count = facts.PlannedCount,
                document = body.GetRawText(),
                verified_by = $"{actor.Type}:{actor.Id}",
            },
            cancellationToken).ConfigureAwait(false);

        // The same canonical content in the same project is the same manifest (AC6): a concurrent identical upload waited
        // on the unique key and now reads the committed row.
        var manifestId = insertedId ?? await db.ScalarAsync<Guid>(
            OrchestratorSql.ManifestByHash,
            new { org_id = orgId, project_id = command.ProjectId, manifest_hash = hash },
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("manifest", manifestId.ToString("D")));

        return Result.Success(new ManifestAccepted(manifestId, hash), 201);
    }

    private static Result<ManifestAccepted> Refuse(string reason, IReadOnlyList<ErrorDetail> details)
    {
        EvidgateTelemetry.RecordManifestRejected(reason);

        return Result.Failure<ManifestAccepted>(OrchestratorErrors.ManifestInvalid("the manifest cannot be verified", details));
    }

    private sealed class TargetsRow
    {
        public bool ProjectExists { get; set; }

        public bool EnvironmentKnown { get; set; }
    }
}
