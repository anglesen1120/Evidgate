using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Orchestrator.Domain;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// The order of the run list. Both orders break <c>created_at</c> ties by <c>id</c> ascending, because the
/// descending list is read forward from <c>run_project_created_idx (org_id, project_id, created_at desc, id)</c>;
/// a cursor carries its order and is refused under the other one.
/// </summary>
internal enum RunListOrder
{
    /// <summary><c>(created_at, id)</c> ascending: oldest first, the default.</summary>
    Ascending,

    /// <summary><c>created_at</c> descending, then <c>id</c> ascending: newest first.</summary>
    Descending,
}

/// <summary>
/// The optional, AND-combined, exact-match filters of the run list (05.2.2 §5, AC6; F43/S143 adds
/// <c>state</c>, <c>decision</c>, <c>rerun_scope</c> and <c>parent_run_id</c>). Every member is a value a
/// caller supplied and the endpoint already validated: <c>ref_name</c> an identity name (1..255 code points, no
/// control character), <c>pr_number</c> at least 1 and inside <c>int</c>, <c>head_sha</c> a
/// <c>^[0-9a-f]{40}([0-9a-f]{24})?$</c> commit, <c>trigger_event</c> one of the closed vocabulary,
/// <c>is_fork</c> a boolean, <c>state</c> one of the seven 00 §8.1 states, <c>decision</c> <c>pending</c> or one
/// verdict in lower snake (<c>pass</c>, <c>fail</c>, <c>inconclusive</c>, mapped to the stored 00 §7 verdict by
/// <see cref="RunListFilterRules.StoredDecision"/>), <c>rerun_scope</c> <c>full</c> or <c>failed_subset</c>,
/// <c>parent_run_id</c> a uuid and <c>completeness</c> <c>complete</c> or <c>incomplete</c>. A null
/// member means "no filter on that column",
/// never "match null" — a plain run (no parent, no rerun scope) matches neither of those two filters.
/// <para>
/// The F43 members and <c>Completeness</c> are declared with a default so a caller that only narrows the recorded
/// identity (the <c>EXPLAIN</c> harness of <c>ListRunsIndexPlanTests</c>) keeps stating exactly the filters it binds;
/// the produced statement and its parameters stay shape-driven either way.
/// </para>
/// </summary>
/// <param name="RefName">Exact <c>ref_name</c>; the one filter the <c>run_project_ref_idx</c> keyset serves.</param>
/// <param name="PrNumber">Exact <c>pr_number</c>.</param>
/// <param name="HeadSha">Exact <c>head_sha</c>.</param>
/// <param name="TriggerEvent">Exact <c>trigger_event</c>.</param>
/// <param name="IsFork">Exact <c>is_fork</c>.</param>
/// <param name="State">Exact <c>run.state</c>, one of the seven 00 §8.1 states; not index-served, so it scans.</param>
/// <param name="Decision">
/// The run's newest live evaluation verdict: <c>PASS</c>, <c>FAIL</c>, <c>INCONCLUSIVE</c>, or <c>pending</c> for a
/// run with no live evaluation. The verdicts live in Policy, so this filter declares no SQL predicate and is applied
/// by a bounded chunked scan of the keyset order; any other value is refused 422 <c>request_invalid</c>.
/// </param>
/// <param name="RerunScope">Exact <c>run.rerun_scope</c> (0031): <c>full</c> or <c>failed_subset</c>.</param>
/// <param name="ParentRunId">Exact <c>run.parent_run_id</c> (0031): the run a rerun was created from.</param>
/// <param name="Completeness">
/// Exact <c>completeness</c> of the run's <em>current</em> evidence revision (the list's own join): <c>complete</c> or
/// <c>incomplete</c>, the only two values <c>evidence_revision_completeness_check</c> (0049) stores. A run with no
/// current revision has no completeness and is excluded by both values.
/// </param>
internal sealed record RunListFilters(
    string? RefName,
    int? PrNumber,
    string? HeadSha,
    string? TriggerEvent,
    bool? IsFork,
    string? State = null,
    string? Decision = null,
    string? RerunScope = null,
    Guid? ParentRunId = null,
    string? Completeness = null);

/// <summary>
/// The closed vocabularies of the run list's F43 filters and the one refusal they own (00 §9.2 <c>422
/// request_invalid</c> with <c>details[].path</c>). The endpoint validates a query-string value with the same
/// rules, so a malformed value never reaches the handler; the handler re-checks the typed filters because the
/// contract it serves is the handler's — a <c>decision</c> outside the vocabulary must be a 422 and never a silently
/// narrowed (or silently widened) page, whatever bound the query.
/// </summary>
internal static class RunListFilterRules
{
    /// <summary>The <c>decision</c> value for a run whose newest live evaluation does not exist yet.</summary>
    internal const string PendingDecision = "pending";

    /// <summary>
    /// The accepted <c>decision</c> filter vocabulary, lower snake because a filter is not a stored value: the
    /// three verdicts plus <c>pending</c> for a run whose newest live evaluation does not exist. Mapped to the
    /// stored (00 §7 upper case) verdict by <see cref="StoredDecision"/>; upper case is not accepted, so a filter
    /// can never be confused with the value it selects.
    /// </summary>
    internal static readonly IReadOnlyList<string> Decisions = ["pending", "pass", "fail", "inconclusive"];

    /// <summary>
    /// The single filter-to-stored mapping: <c>pending</c> selects the absence of a live verdict (<c>null</c>),
    /// every other token its 00 §7 verdict. Callers validate first, so an unknown token throws.
    /// </summary>
    internal static string? StoredDecision(string filter) => filter switch
    {
        PendingDecision => null,
        "pass" => "PASS",
        "fail" => "FAIL",
        "inconclusive" => "INCONCLUSIVE",
        _ => throw new ArgumentOutOfRangeException(
            nameof(filter), filter, "not a run-list decision filter"),
    };

    /// <summary>The <c>run.rerun_scope</c> values 0031's <c>run_rerun_scope_check</c> admits.</summary>
    internal static readonly IReadOnlyList<string> RerunScopes = ["full", "failed_subset"];

    /// <summary>The <c>evidence_revision.completeness</c> values 0049's <c>evidence_revision_completeness_check</c> admits.</summary>
    internal static readonly IReadOnlyList<string> Completenesses = ["complete", "incomplete"];

    /// <summary>The 00 §9.2 reason of a filter value the column's own vocabulary cannot hold.</summary>
    internal const string InvalidValue = "invalid_value";

    /// <summary>True when <paramref name="state"/> is one of the seven 00 §8.1 run states (00 §8.1, 0031's <c>run_state_check</c>).</summary>
    internal static bool IsKnownState(string state) => RunStateMachine.IsKnownState(state);

    /// <summary>The wire token of an order: <c>asc</c> or <c>desc</c>, lower case only.</summary>
    internal static bool TryParseOrder(string token, out RunListOrder order)
    {
        switch (token)
        {
            case "asc":
                order = RunListOrder.Ascending;
                return true;
            case "desc":
                order = RunListOrder.Descending;
                return true;
            default:
                order = default;
                return false;
        }
    }

    /// <summary>
    /// The per-parameter failures of the F43 filter values, in parameter order; empty when every present value is
    /// one the column can hold. <c>parent_run_id</c> is a <see cref="Guid"/> already, so a non-UUID value is the
    /// endpoint's refusal and never reaches here.
    /// </summary>
    internal static List<ErrorDetail> Validate(RunListFilters filters)
    {
        var errors = new List<ErrorDetail>(3);

        if (filters.State is { } state && !IsKnownState(state))
        {
            errors.Add(new ErrorDetail("state", InvalidValue));
        }

        if (filters.Decision is { } decision && !Decisions.Contains(decision, StringComparer.Ordinal))
        {
            errors.Add(new ErrorDetail("decision", InvalidValue));
        }

        if (filters.RerunScope is { } rerunScope && !RerunScopes.Contains(rerunScope, StringComparer.Ordinal))
        {
            errors.Add(new ErrorDetail("rerun_scope", InvalidValue));
        }

        if (filters.Completeness is { } completeness && !Completenesses.Contains(completeness, StringComparer.Ordinal))
        {
            errors.Add(new ErrorDetail("completeness", InvalidValue));
        }

        return errors;
    }
}
