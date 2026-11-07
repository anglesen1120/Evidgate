using System.Text.Json;
using Evidgate.Modules.Orchestrator.Contracts;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// The run detail's state history (<c>GET /v1/runs/{r}</c>): the transitions the write trigger recorded for one run,
/// oldest first, or null when the run predates the table and nothing can reconstruct them. The two columns it maps are
/// selected by the statement that reads the run row itself, so the served state and the served history can never come
/// from two snapshots. Writes nothing.
/// </summary>
internal static class RunStateHistoryReader
{
    /// <summary>
    /// The transitions the run row carries, oldest first. An empty list is a run whose history is known and which has
    /// not transitioned; null is a run with no creation marker, i.e. one created before the table existed. The org
    /// filter and RLS are the statement's, so another tenant's rows are never visible.
    /// </summary>
    /// <param name="known">The marker column: the run's own sequence 1 carries no from state.</param>
    /// <param name="transitions">The transitions column: one item per transition, already ordered and bounded.</param>
    internal static IReadOnlyList<RunStateTransition>? FromRow(bool known, string transitions)
    {
        // No marker means the run was created before the trigger existed: "unknown", never "no transition yet".
        if (!known)
        {
            return null;
        }

        var history = new List<RunStateTransition>();
        using var document = JsonDocument.Parse(transitions);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            history.Add(new RunStateTransition(
                item.GetProperty("from_state").GetString()!,
                item.GetProperty("to_state").GetString()!,
                item.GetProperty("occurred_at").GetDateTimeOffset()));
        }

        return history;
    }
}
