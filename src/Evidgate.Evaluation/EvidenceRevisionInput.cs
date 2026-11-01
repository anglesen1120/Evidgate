namespace Evidgate.Evaluation;

/// <summary>A frozen evidence revision, exactly as the reconciler left it, plus the results it froze over.</summary>
/// <param name="ExpectedExecutionCount">
/// How many executions the run planned, from the manifest. Zero is the "expected count zero" case the decision rules
/// single out; it is never inferred from an empty result list, because a run whose results are all stale also has none.
/// </param>
/// <param name="SuiteDeclaredEmpty">
/// True when the pinned suite revision declares no cases. It only matters with a zero expected count, where it is one
/// half of the empty-suite allowance the policy supplies.
/// </param>
/// <param name="Missing">The closed <c>missing[]</c> codes the reconciler recorded; duplicates are collapsed.</param>
/// <param name="Results">The normalized result of every execution the evidence carries, in any order.</param>
public sealed record EvidenceRevisionInput(
    int ExpectedExecutionCount,
    bool SuiteDeclaredEmpty,
    IReadOnlyList<MissingReasonCode> Missing,
    IReadOnlyList<CaseResultInput> Results);
