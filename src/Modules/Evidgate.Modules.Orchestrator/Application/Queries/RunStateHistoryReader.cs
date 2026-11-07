using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// The run detail's state history (<c>GET /v1/runs/{r}</c>): the transitions the write trigger recorded for one run,
/// oldest first, or null when the run predates the table and nothing can reconstruct them. The read runs inside the
/// caller's tenant transaction over the ambient scope and writes nothing.
/// </summary>
internal interface IRunStateHistoryReader
{
    /// <summary>
    /// The recorded transitions of <paramref name="runId"/>, oldest first. An empty list is a run whose history is
    /// known and which has not transitioned; null is a run with no creation marker, i.e. one created before the table
    /// existed. The org comes from the ambient scope, so another tenant's rows are never visible, and the caller must
    /// have authorised the run first.
    /// </summary>
    /// <param name="runId">The run to read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<RunStateTransition>?> HistoryAsync(Guid runId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRunStateHistoryReader" />
/// <param name="db">The Dapper path over the ambient scope.</param>
/// <param name="scopes">The ambient tenant scope the org predicate comes from.</param>
internal sealed class RunStateHistoryReader(ITenantDb db, ITenantScopeAccessor scopes) : IRunStateHistoryReader
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RunStateTransition>?> HistoryAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<StateHistoryRow>(
            RunStateHistorySql.HistoryForRun,
            new { org_id = scopes.Require().OrgId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        // No marker means the run was created before the trigger existed: "unknown", never "no transition yet".
        if (rows.Count == 0 || !rows[0].Known)
        {
            return null;
        }

        var history = new List<RunStateTransition>(rows.Count);
        foreach (var row in rows)
        {
            // The left join's padding row carries no transition; a creation row is filtered by the statement itself.
            if (row.FromState is { } from && row.ToState is { } to && row.OccurredAt is { } occurredAt)
            {
                history.Add(new RunStateTransition(from, to, occurredAt));
            }
        }

        return history;
    }

    /// <summary>One row as <see cref="RunStateHistorySql.HistoryForRun"/> returns it (snake_case columns, Dapper).</summary>
    private sealed class StateHistoryRow
    {
        public bool Known { get; set; }

        public string? FromState { get; set; }

        public string? ToState { get; set; }

        public DateTimeOffset? OccurredAt { get; set; }
    }
}
