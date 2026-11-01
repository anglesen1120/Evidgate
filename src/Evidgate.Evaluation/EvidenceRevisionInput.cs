namespace Evidgate.Evaluation;

/// <summary>A frozen evidence revision, exactly as the reconciler left it, plus the results it froze over.</summary>
public sealed record EvidenceRevisionInput
{
    private IReadOnlyList<MissingReasonCode> missing = [];
    private IReadOnlyList<CaseResultInput> results = [];

    /// <summary>Creates a frozen evidence revision; both collections are copied.</summary>
    /// <param name="expectedExecutionCount">
    /// How many executions the run planned, from the manifest. Zero is the "expected count zero" case the decision
    /// rules single out; it is never inferred from an empty result list, because a run whose results are all stale also
    /// has none. A negative value is malformed and never yields a pass.
    /// </param>
    /// <param name="suiteDeclaredEmpty">
    /// True when the pinned suite revision declares no cases. It only matters with a zero expected count, where it is
    /// one half of the empty-suite allowance the policy supplies.
    /// </param>
    /// <param name="missing">The closed <c>missing[]</c> codes the reconciler recorded; duplicates are collapsed.</param>
    /// <param name="results">The normalized result of every execution the evidence carries, in any order.</param>
    public EvidenceRevisionInput(
        int expectedExecutionCount,
        bool suiteDeclaredEmpty,
        IReadOnlyList<MissingReasonCode> missing,
        IReadOnlyList<CaseResultInput> results)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(results);

        ExpectedExecutionCount = expectedExecutionCount;
        SuiteDeclaredEmpty = suiteDeclaredEmpty;
        Missing = missing;
        Results = results;
    }

    /// <summary>How many executions the run planned; zero and negatives are handled by the decision rules.</summary>
    public int ExpectedExecutionCount { get; init; }

    /// <summary>True when the pinned suite revision declares no cases.</summary>
    public bool SuiteDeclaredEmpty { get; init; }

    /// <summary>The closed <c>missing[]</c> codes the reconciler recorded; duplicates are collapsed.</summary>
    public IReadOnlyList<MissingReasonCode> Missing
    {
        get => missing;
        init => missing = InputSnapshot.Copy(value);
    }

    /// <summary>The normalized result of every execution the evidence carries, in any order.</summary>
    public IReadOnlyList<CaseResultInput> Results
    {
        get => results;
        init => results = InputSnapshot.Copy(value);
    }
}
