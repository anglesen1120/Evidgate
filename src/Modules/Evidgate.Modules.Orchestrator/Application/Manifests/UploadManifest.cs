using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Contracts.Validation;
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
/// Upload a manifest (<c>POST /v1/projects/{p}/manifests</c>, 05.1.1 §5). The raw body travels unparsed: it is parsed and
/// checked against <c>manifest.v1.json</c> by the handler, after the C2 pipeline authorized the caller, so an anonymous or
/// unauthorized request costs no parse and no schema run (review finding). The idempotency record hashes
/// <paramref name="BodySha256"/>, never the body itself.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The raw UTF-8 body, at most 256 KiB.</param>
/// <param name="BodySha256">Hex SHA-256 of <paramref name="Body"/>: the request identity the idempotency store compares.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record UploadManifestCommand(
    Guid ProjectId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string BodySha256,
    string IdempotencyKey)
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
    Lazy<ContractSchemaSet> schemas,
    IAuditTrail audit)
    : ICommandHandler<UploadManifestCommand, Result<ManifestAccepted>>
{
    internal const string UnknownCaseRevision = "unknown_case_revision";
    internal const string UnknownSuiteRevision = "unknown_suite_revision";
    internal const string UnknownEnvironmentRevision = "unknown_environment_revision";
    internal const string InvalidValue = "invalid_value";
    internal const string InvalidJson = "invalid_json";

    /// <inheritdoc />
    public async ValueTask<Result<ManifestAccepted>> Handle(UploadManifestCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(UploadManifestCommand));

        if (OrchestratorScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<ManifestAccepted>(mismatch);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(command.Body);
        }
        catch (JsonException)
        {
            return Refuse(InvalidJson, [new ErrorDetail(string.Empty, InvalidJson)]);
        }

        using var parsed = document;
        var body = parsed.RootElement;
        if (JsonBody.HasUnstorableText(body))
        {
            // A lone surrogate or a NUL: neither the canonical form nor jsonb can carry it.
            return Refuse(InvalidValue, [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        var verdict = schemas.Value.Validate(ContractSchemaKeys.Manifest, body);
        if (!verdict.IsValid)
        {
            EvidgateTelemetry.RecordManifestRejected("schema");
            return Result.Failure<ManifestAccepted>(OrchestratorErrors.ManifestInvalid(
                "the manifest does not match manifest.v1",
                [.. verdict.Errors.Select(error => new ErrorDetail(JsonBody.SafePath(error), error.Reason, error.Index))]));
        }

        var (facts, refusal) = ManifestVerifier.Verify(body);
        if (refusal is not null)
        {
            return Refuse(refusal.Reason, refusal.Details);
        }

        string hash;
        try
        {
            // One canonicalisation, straight from the received bytes.
            hash = CanonicalHash.Compute(command.Body.Span);
        }
        catch (Exception exception) when (exception is JsonCanonicalizationException or ArgumentException)
        {
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
                document = Encoding.UTF8.GetString(command.Body.Span),
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
