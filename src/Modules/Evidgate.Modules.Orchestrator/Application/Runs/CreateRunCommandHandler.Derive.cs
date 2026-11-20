using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

internal sealed partial class CreateRunCommandHandler
{
    /// <summary>
    /// The verified <c>ref</c>/<c>sha</c> claims of the caller's credential (05.2.2-T1.3): the <c>ci_token_issue</c>
    /// row the 02.4.1 exchange wrote for the presented <c>ci_producer</c> token, found by the run reference the minted
    /// token carries (<c>repository_id</c>, <c>run_id</c>, <c>run_attempt</c>). The exchange mints the token's
    /// <c>jti</c> as a fresh GUID and stores the GitHub OIDC <c>jti</c> separately, so that key is not a link between
    /// the token in hand and the row; the run reference is (owner decision D-S45-Q2). Only a principal that carries a
    /// <see cref="CiRunReference"/> can be a <c>ci_producer</c>, so every other caller short-circuits without a
    /// statement (00 §5.7 P3).
    /// <para>
    /// The Identity module owns <c>ci_token_issue</c>; this module reads it as raw SQL like the Evidence module reads
    /// <c>run</c> (the cross-module read precedent), so no Identity type or contract is involved. RLS already scopes
    /// the row to this tenant (<c>ci_token_issue_app_tenant</c>); <c>org_id</c> and <c>project_id</c> repeat the scope
    /// in the statement as defence in depth (00 §5.4). A row that is not there — purged after its replay window,
    /// another project's, or another run attempt's — means NOT verified: the caller's body values are stored and
    /// labelled <c>unverified_ref</c> rather than trusted. <c>ref</c>/<c>sha</c> are fixed per run attempt, so the
    /// newest row for the attempt is authoritative and the statement takes it with <c>limit 1</c>.
    /// </para>
    /// </summary>
    private async ValueTask<CiRefClaim?> ReadVerifiedClaimAsync(
        IRequestPrincipal? principal,
        Guid projectId,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        if (principal?.CiRun is not { } ciRun)
        {
            return null;
        }

        var row = await db.QuerySingleOrDefaultAsync<CiTokenIssueRow>(
            OrchestratorSql.CiTokenIssueClaim,
            new
            {
                org_id = orgId,
                project_id = projectId,
                repository_id = ciRun.RepositoryId,
                run_id = ciRun.RunId,
                run_attempt = ciRun.RunAttempt,
            },
            cancellationToken).ConfigureAwait(false);

        return row is null ? null : new CiRefClaim(row.Ref, row.Sha);
    }

    /// <summary>
    /// Builds the planned executions from the manifest document: case ids through Catalog, variant hashes through
    /// Catalog's normaliser (computed once per distinct params object), the server-side <c>execution_key</c>, and the
    /// shard id from the shard plan's 1-based ordinal. Any unresolved case revision or variant, or a duplicate
    /// execution key, is a refusal before any write.
    /// </summary>
    private async ValueTask<(PlannedSet? Planned, Error? Refusal)> DeriveAsync(
        Guid projectId,
        ManifestRow manifest,
        IngestionLimits limits,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(manifest.Document);
        var root = document.RootElement;
        var planned = root.GetProperty("planned_executions");
        var shards = root.GetProperty("shard_plan").GetProperty("shards");

        // The counts are read from the stored document before any list is allocated for them: an over-cap
        // manifest costs one property read and creates nothing, however large the document is.
        var count = planned.GetArrayLength();
        if (count > limits.MaxManifestExecutions)
        {
            return (null, CapExceeded("manifest_executions", limits.MaxManifestExecutions, count));
        }

        var variants = root.GetProperty("matrix").GetProperty("variants").GetArrayLength();
        if (variants > limits.MaxManifestVariants)
        {
            return (null, CapExceeded("manifest_variants", limits.MaxManifestVariants, variants));
        }
        var caseRevisionIds = new List<Guid>(count);
        var variantParams = new List<JsonElement>(count);
        var targetEnvs = new List<string>(count);
        var shardIds = new List<string>(count);

        var ranges = new List<(int From, int To, int Ordinal)>(shards.GetArrayLength());
        var ordinal = 0;
        foreach (var shard in shards.EnumerateArray())
        {
            ordinal++;
            ranges.Add((shard.GetProperty("from_index").GetInt32(), shard.GetProperty("to_index").GetInt32(), ordinal));
        }

        var index = 0;
        foreach (var execution in planned.EnumerateArray())
        {
            caseRevisionIds.Add(execution.GetProperty("case_revision_id").GetGuid());
            variantParams.Add(execution.GetProperty("variant").GetProperty("params"));
            targetEnvs.Add(execution.GetProperty("target_env").GetString()!);
            shardIds.Add(ShardFor(ranges, index));
            index++;
        }

        var caseIds = await catalog.GetCaseIdsAsync(projectId, caseRevisionIds, cancellationToken).ConfigureAwait(false);
        var hashes = catalog.NormalizeVariantHashes(variantParams);

        var rows = new List<PlannedRow>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            if (!caseIds.TryGetValue(caseRevisionIds[i], out var caseId))
            {
                return (null, ManifestInvalidError(
                    [new ErrorDetail($"/planned_executions/{i}/case_revision_id", UnknownCaseRevision, i)]));
            }

            if (hashes[i] is not { } variantHash)
            {
                return (null, ManifestInvalidError(
                    [new ErrorDetail($"/planned_executions/{i}/variant/params", InvalidVariant, i)]));
            }

            var key = ExecutionKey.Compute(caseId, variantHash, targetEnvs[i]);
            if (!seen.Add(key))
            {
                return (null, OrchestratorErrors.ExecutionKeyConflict(
                    [new ErrorDetail($"/planned_executions/{i}", ApiErrorCode.ExecutionKeyConflict, i)]));
            }

            rows.Add(new PlannedRow(Guid.CreateVersion7(), key, caseRevisionIds[i], variantHash, targetEnvs[i], shardIds[i]));
        }

        return (new PlannedSet(JsonSerializer.Serialize(rows), ranges.Count), null);
    }

    /// <summary>The shard id of the planned execution at <paramref name="index"/>: <c>shard-&lt;1-based ordinal&gt;</c> (T54).</summary>
    private static string ShardFor(List<(int From, int To, int Ordinal)> ranges, int index)
    {
        foreach (var range in ranges)
        {
            if (index >= range.From && index < range.To)
            {
                return $"shard-{range.Ordinal}";
            }
        }

        // The manifest verifier guarantees the shards partition [0, count), so this is unreachable for a verified manifest.
        throw new InvalidOperationException($"planned execution {index} is not inside any shard of the manifest");
    }
    /// <summary>The 422 a manifest over one published cap earns: the cap name, its limit and the declared count.</summary>
    private static Error CapExceeded(string cap, long limit, long actual)
    {
        EvidgateTelemetry.RecordIngestCapExceeded(cap);

        return Error.Unprocessable(
            ApiErrorCode.CapExceeded,
            string.Create(CultureInfo.InvariantCulture, $"the manifest exceeds the {cap} limit"),
            [new ErrorDetail(cap, ApiErrorCode.CapExceeded, Limit: limit, Actual: actual)]);
    }

    private static Result<RunCreated> ManifestInvalid(IReadOnlyList<ErrorDetail> details) =>
        Result.Failure<RunCreated>(ManifestInvalidError(details));

    private static Error ManifestInvalidError(IReadOnlyList<ErrorDetail> details) =>
        OrchestratorErrors.ManifestInvalid("the manifest cannot be used to create a run", details);

    /// <summary>The manifest row the handler derives from (Dapper maps the snake_case columns).</summary>
    private sealed class ManifestRow
    {
        public Guid SuiteRevisionId { get; set; }

        public Guid? EnvironmentRevisionId { get; set; }

        public string Document { get; set; } = string.Empty;
    }

    /// <summary>Whether the manifest's suite and environment revisions resolve in the project.</summary>
    private sealed class RevisionRow
    {
        public bool SuiteKnown { get; set; }

        public bool EnvironmentKnown { get; set; }
    }

    /// <summary>
    /// One <c>ci_token_issue</c> row as <see cref="OrchestratorSql.CiTokenIssueClaim"/> returns it (Dapper maps the
    /// snake_case columns): the two verified claims 05.2.2-T1.3 reads. <c>ref</c> and <c>sha</c> are fixed per run
    /// attempt, so the statement returns the newest row of the attempt and the read is a single row or none.
    /// </summary>
    private sealed class CiTokenIssueRow
    {
        public string Ref { get; set; } = string.Empty;

        public string Sha { get; set; } = string.Empty;
    }

    /// <summary>The one <c>jsonb</c> batch parameter of the planned-execution insert, plus the shard count for the slot registration.</summary>
    private sealed record PlannedSet(string RowsJson, int ShardCount);

    /// <summary>One planned execution as the <c>jsonb_to_recordset</c> batch reads it (the wire names are the column names).</summary>
    private sealed record PlannedRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("execution_key")] string ExecutionKey,
        [property: JsonPropertyName("case_revision_id")] Guid CaseRevisionId,
        [property: JsonPropertyName("variant_hash")] string VariantHash,
        [property: JsonPropertyName("target_env")] string TargetEnv,
        [property: JsonPropertyName("shard_id")] string ShardId);
}
