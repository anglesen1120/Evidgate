using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Infrastructure.Persistence;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// Answers which of a run's evidence revisions have a stored <c>live</c> evaluation: the in-module reader behind
/// <see cref="IPolicyContracts.GetLiveEvaluationsAsync"/>. The endpoint layer never calls it directly.
/// <para>
/// One lock-free statement inside the caller's tenant transaction, for the whole page of revisions the caller is
/// serving — never one statement per row (00 §5.5 C5). The rows are matched on the run and on the ambient scope's org
/// on top of RLS, and <c>mode = 'live'</c> is part of the predicate: a <c>shadow</c> verdict about the same evidence is
/// a second opinion, and naming it would report a decision the run never published.
/// </para>
/// <para>
/// A revision with no live evaluation reads no row and is absent from the answer, so "not evaluated yet" is an empty
/// answer rather than an error — the same shape as the other members' "nothing there".
/// </para>
/// </summary>
internal sealed class GetLiveEvaluationsQuery(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>The live evaluation of each named revision, ordered by revision number.</summary>
    /// <param name="runId">The run the revisions belong to.</param>
    /// <param name="evidenceRevisions">The <c>evidence_revision.revision_no</c> values to look up.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<IReadOnlyList<LiveEvaluation>> GetAsync(
        Guid runId,
        IReadOnlyList<int> evidenceRevisions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidenceRevisions);

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetLiveEvaluationsQuery));

        // No page names no revision, and an empty delimited scalar would reach the cast below as one blank element
        // instead of an empty set: the empty answer is decided here, before the round trip.
        if (evidenceRevisions.Count == 0)
        {
            return [];
        }

        var orgId = scopes.Require().OrgId;
        var rows = await db.QueryAsync<LiveEvaluationRow>(
            EvaluationLookupSql.LiveEvaluations,
            new
            {
                org_id = orgId,
                run_id = runId,
                // The tenant Dapper path registers StringArrayTypeHandler, whose write side refuses a client array by
                // design, so the numbers travel as one delimited scalar — the same way the frozen revision's own
                // `missing` codes do. They are integers the caller read from the run's rows, never client text.
                revision_nos = string.Join(",", evidenceRevisions.Distinct().Select(Format)),
            },
            cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(row => new LiveEvaluation(row.EvidenceRevision, row.EvaluationId))];
    }

    private static string Format(int revisionNo) => revisionNo.ToString(CultureInfo.InvariantCulture);

    /// <summary>One row as the statement returns it; the column names bind through the underscore-matching type map.</summary>
    private sealed class LiveEvaluationRow
    {
        public int EvidenceRevision { get; set; }

        public Guid EvaluationId { get; set; }
    }
}
