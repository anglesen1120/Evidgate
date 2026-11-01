namespace Evidgate.Evaluation;

/// <summary>The decision configuration of the pinned policy revision, resolved into the shapes this function reads.</summary>
public sealed record PolicyRevisionInput
{
    private IReadOnlyList<string> requiredCaseKeys = [];
    private IReadOnlyList<string> requiredSuiteKeys = [];
    private IReadOnlyList<Guid> criticalCaseIds = [];
    private IReadOnlyList<string> allowedSkipCaseKeys = [];
    private IReadOnlyList<string> requiredArtifactKinds = [];

    /// <summary>Creates the policy input; every collection is copied.</summary>
    /// <param name="requiredCaseKeys">Case keys every run must carry; empty means the manifest's own required flags decide.</param>
    /// <param name="requiredSuiteKeys">Suite keys every run must carry; empty means the manifest's own required flags decide.</param>
    /// <param name="criticalCaseIds">
    /// Case ids that can never be skipped, blocked or quarantined. A critical case is in the required scope whatever the
    /// required lists and the manifest's own flag say.
    /// </param>
    /// <param name="allowedSkipCaseKeys">
    /// Case keys the policy tolerates as skipped. The allowance applies to cases outside the required scope only: a
    /// skipped required case never passes, whatever this list says.
    /// </param>
    /// <param name="flakyBudget">The non-critical fail-to-pass flips the policy tolerates; read by the flaky layer, not the core.</param>
    /// <param name="requiredArtifactKinds">Artifact kinds the run must produce, validated against the contract's closed list.</param>
    /// <param name="emptySuiteAllowed">
    /// True when the policy allows a suite the catalog declares empty to pass. It covers an empty run only: a required
    /// case the evidence never reported still keeps the evaluation from passing.
    /// </param>
    public PolicyRevisionInput(
        IReadOnlyList<string> requiredCaseKeys,
        IReadOnlyList<string> requiredSuiteKeys,
        IReadOnlyList<Guid> criticalCaseIds,
        IReadOnlyList<string> allowedSkipCaseKeys,
        int flakyBudget,
        IReadOnlyList<string> requiredArtifactKinds,
        bool emptySuiteAllowed)
    {
        ArgumentNullException.ThrowIfNull(requiredCaseKeys);
        ArgumentNullException.ThrowIfNull(requiredSuiteKeys);
        ArgumentNullException.ThrowIfNull(criticalCaseIds);
        ArgumentNullException.ThrowIfNull(allowedSkipCaseKeys);
        ArgumentNullException.ThrowIfNull(requiredArtifactKinds);

        RequiredCaseKeys = requiredCaseKeys;
        RequiredSuiteKeys = requiredSuiteKeys;
        CriticalCaseIds = criticalCaseIds;
        AllowedSkipCaseKeys = allowedSkipCaseKeys;
        FlakyBudget = flakyBudget;
        RequiredArtifactKinds = requiredArtifactKinds;
        EmptySuiteAllowed = emptySuiteAllowed;
    }

    /// <summary>Case keys every run must carry; empty means the manifest's own required flags decide.</summary>
    public IReadOnlyList<string> RequiredCaseKeys
    {
        get => requiredCaseKeys;
        init => requiredCaseKeys = InputSnapshot.Copy(value);
    }

    /// <summary>Suite keys every run must carry; empty means the manifest's own required flags decide.</summary>
    public IReadOnlyList<string> RequiredSuiteKeys
    {
        get => requiredSuiteKeys;
        init => requiredSuiteKeys = InputSnapshot.Copy(value);
    }

    /// <summary>Case ids that are always in the required scope, whatever the manifest said.</summary>
    public IReadOnlyList<Guid> CriticalCaseIds
    {
        get => criticalCaseIds;
        init => criticalCaseIds = InputSnapshot.Copy(value);
    }

    /// <summary>Case keys the policy tolerates as skipped, outside the required scope only.</summary>
    public IReadOnlyList<string> AllowedSkipCaseKeys
    {
        get => allowedSkipCaseKeys;
        init => allowedSkipCaseKeys = InputSnapshot.Copy(value);
    }

    /// <summary>The non-critical fail-to-pass flips the policy tolerates.</summary>
    public int FlakyBudget { get; init; }

    /// <summary>Artifact kinds the run must produce, validated against the contract's closed list.</summary>
    public IReadOnlyList<string> RequiredArtifactKinds
    {
        get => requiredArtifactKinds;
        init => requiredArtifactKinds = InputSnapshot.Copy(value);
    }

    /// <summary>True when the policy allows a suite the catalog declares empty to pass.</summary>
    public bool EmptySuiteAllowed { get; init; }
}
