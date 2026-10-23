using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Attempts;

/// <summary>
/// <see cref="IBlockedAttemptResolver"/> over the caller's tenant transaction (05.3.3-T2.2, FR-05-15, AC4): it marks
/// the dependants of a failed setup <c>blocked</c> so a dependant never reads as passed after its setup failed. Same
/// transaction rules as <see cref="CaseAttemptWriter"/>: it never opens a connection or a transaction, never commits
/// and never rolls back, and it reaches Catalog only through <see cref="ICatalogContracts"/>.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
/// <param name="scopes">The tenant-scope accessor; <c>Require()</c> is the guard that throws before any statement.</param>
/// <param name="catalog">The Catalog surface that walks the suite revision's <c>case_dependency</c> DAG.</param>
internal sealed class BlockedAttemptResolver(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    ICatalogContracts catalog) : IBlockedAttemptResolver
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ResolveDependantsAsync(
        Guid runId,
        string failedExecutionKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failedExecutionKey);

        var scope = scopes.Require();

        // A key outside the 00 §7 shape can never be planned, so it is an empty answer with no statement (step a).
        if (!CaseAttemptRules.IsValidExecutionKey(failedExecutionKey))
        {
            return Array.Empty<string>();
        }

        // Statement A: the run's suite revision and the failed execution's member, plus whether it has a failed,
        // timed_out or error attempt in this run. An unknown or invisible run/key answers no row (step a).
        var failed = await db.QuerySingleOrDefaultAsync<FailedExecutionRow>(
            CaseAttemptSql.FailedExecutionHead,
            new
            {
                org_id = scope.OrgId,
                run_id = runId,
                project_id = scope.ProjectId,
                execution_key = failedExecutionKey,
            },
            cancellationToken).ConfigureAwait(false);

        if (failed is null || !failed.Failing)
        {
            // Unknown/invisible run or key, or an execution that has not failed: nothing is written (step a).
            return Array.Empty<string>();
        }

        // The Catalog walk: one bounded statement however deep the dependency graph (step b).
        var dependants = await catalog.GetDependantsAsync(
            failed.SuiteRevisionId, failed.CaseRevisionId, failed.VariantHash, cancellationToken).ConfigureAwait(false);
        if (dependants.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Statement B: one insert for every dependant this run plans in the SAME target_env with no attempt yet
        // (step c). The statement count is constant in the plan size: A, the Catalog walk and B, nothing per dependant.
        var pairs = new PairRow[dependants.Count];
        for (var index = 0; index < dependants.Count; index++)
        {
            pairs[index] = new PairRow(dependants[index].CaseRevisionId, dependants[index].VariantHash);
        }

        var written = await db.QueryAsync<string>(
            CaseAttemptSql.InsertBlockedAttempts,
            new
            {
                org_id = scope.OrgId,
                run_id = runId,
                target_env = failed.TargetEnv,
                failed_key = failedExecutionKey,
                pairs = JsonSerializer.Serialize(pairs),
            },
            cancellationToken).ConfigureAwait(false);

        if (written.Count > 0)
        {
            EvidgateTelemetry.RecordCaseAttemptBlockedBySetup(written.Count);
            EvidgateTelemetry.RecordCaseAttemptWritten(CaseAttemptStatus.Blocked, written.Count);
        }

        return written;
    }

    /// <summary>The failed execution's head (statement A), snake_case columns mapped by Dapper.</summary>
    private sealed class FailedExecutionRow
    {
        public Guid SuiteRevisionId { get; set; }

        public Guid CaseRevisionId { get; set; }

        public string VariantHash { get; set; } = string.Empty;

        public string TargetEnv { get; set; } = string.Empty;

        public bool Failing { get; set; }
    }

    /// <summary>One dependant member of statement B as the <c>jsonb_to_recordset</c> batch reads it.</summary>
    private sealed record PairRow(
        [property: JsonPropertyName("case_revision_id")] Guid CaseRevisionId,
        [property: JsonPropertyName("variant_hash")] string VariantHash);
}
