namespace Evidgate.Evaluation;

/// <summary>The decision configuration of the pinned policy revision, resolved into the shapes this function reads.</summary>
/// <param name="RequiredCaseKeys">Case keys every run must carry; empty means the manifest's own required flags decide.</param>
/// <param name="RequiredSuiteKeys">Suite keys every run must carry; empty means the manifest's own required flags decide.</param>
/// <param name="CriticalCaseIds">
/// Case ids that can never be skipped, blocked or quarantined. A critical case is in the required scope even when the
/// required lists do not name it.
/// </param>
/// <param name="AllowedSkipCaseKeys">
/// Case keys the policy tolerates as skipped. The allowance applies to cases outside the required scope only: a skipped
/// required case never passes, whatever this list says.
/// </param>
/// <param name="FlakyBudget">The non-critical fail-to-pass flips the policy tolerates; read by the flaky layer, not the core.</param>
/// <param name="RequiredArtifactKinds">Artifact kinds the run must produce, validated against the contract's closed list.</param>
/// <param name="EmptySuiteAllowed">
/// True when the policy allows a suite the catalog declares empty to pass. The policy body has no such member yet, so
/// the caller supplies what the pinned revision resolved.
/// </param>
public sealed record PolicyRevisionInput(
    IReadOnlyList<string> RequiredCaseKeys,
    IReadOnlyList<string> RequiredSuiteKeys,
    IReadOnlyList<Guid> CriticalCaseIds,
    IReadOnlyList<string> AllowedSkipCaseKeys,
    int FlakyBudget,
    IReadOnlyList<string> RequiredArtifactKinds,
    bool EmptySuiteAllowed);
