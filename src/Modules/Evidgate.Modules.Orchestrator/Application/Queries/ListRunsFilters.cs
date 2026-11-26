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
/// Exact-match filters are AND-combined before paging. A null member omits its predicate; it does not select
/// rows with a null column. Defaults let callers specify only the identity filters they bind.
/// </summary>
/// <param name="RefName">Exact <c>ref_name</c>; the one filter the <c>run_project_ref_idx</c> keyset serves.</param>
/// <param name="PrNumber">Exact <c>pr_number</c>.</param>
/// <param name="HeadSha">Exact <c>head_sha</c>.</param>
/// <param name="TriggerEvent">Exact <c>trigger_event</c>.</param>
/// <param name="IsFork">Exact <c>is_fork</c>.</param>
/// <param name="State">Exact stored run state.</param>
/// <param name="Decision">
/// A lower-case verdict filter, or <c>pending</c> for a run with no live evaluation. Verdicts belong to Policy,
/// so the handler filters a bounded scan rather than joining Policy's tables.
/// </param>
/// <param name="RerunScope">Exact <c>run.rerun_scope</c>: <c>full</c> or <c>failed_subset</c>.</param>
/// <param name="ParentRunId">The run a rerun was created from.</param>
/// <param name="Completeness">
/// Exact stored completeness of the current evidence revision. A run with no current revision matches neither
/// <c>complete</c> nor <c>incomplete</c>.
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
/// The endpoint and handler share the same filter vocabularies so non-HTTP callers cannot silently select
/// a different set of rows with an unrecognized value.
/// </summary>
internal static class RunListFilterRules
{
    /// <summary>The <c>decision</c> value for a run whose newest live evaluation does not exist yet.</summary>
    internal const string PendingDecision = "pending";

    /// <summary>
    /// Filter tokens are lower-case; <see cref="StoredDecision"/> maps them to stored verdicts. The additional
    /// <c>pending</c> token selects a run without a live verdict.
    /// </summary>
    internal static readonly IReadOnlyList<string> Decisions = ["pending", "pass", "fail", "inconclusive"];

    /// <summary>
    /// Maps validated filters to stored verdicts; <c>pending</c> selects the absence of a live verdict.
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

    /// <summary>Rerun scopes accepted by the stored run's check constraint.</summary>
    internal static readonly IReadOnlyList<string> RerunScopes = ["full", "failed_subset"];

    /// <summary>Completeness values accepted by the stored evidence revision's check constraint.</summary>
    internal static readonly IReadOnlyList<string> Completenesses = ["complete", "incomplete"];

    /// <summary>The response reason for a filter value outside its vocabulary.</summary>
    internal const string InvalidValue = "invalid_value";

    /// <summary>Uses the run state machine's vocabulary so the filter cannot drift from the domain.</summary>
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
    /// Collects errors in parameter order. The endpoint parses <c>parent_run_id</c> to a GUID before it reaches here.
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
