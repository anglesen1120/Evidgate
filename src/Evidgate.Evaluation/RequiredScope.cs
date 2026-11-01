namespace Evidgate.Evaluation;

/// <summary>
/// The executions the policy's required scope covers, summarized in one pass, plus whether it names an item the
/// evidence never reported.
/// </summary>
/// <param name="ResultCount">How many reported results the required scope holds.</param>
/// <param name="HasFailure">True when a result in scope reports a confirmed failure.</param>
/// <param name="HasNotExecuted">True when a result in scope was skipped or blocked.</param>
/// <param name="HasUncoveredRequiredItems">
/// True when the policy names a required case, a required suite or a critical case id the evidence never reported.
/// </param>
internal sealed record RequiredScope(
    int ResultCount,
    bool HasFailure,
    bool HasNotExecuted,
    bool HasUncoveredRequiredItems)
{
    /// <summary>
    /// Resolves the scope: the policy's required cases and suites when it names either, else the manifest's own
    /// required executions, with a critical case always in scope whatever the manifest said. A policy-named item with
    /// no result is reported, so it can never pass silently.
    /// </summary>
    /// <remarks>
    /// Coverage is inverted: the pending sets start as copies of the policy's own lists and shrink as results resolve
    /// them, so the sweep allocates in the policy's size and never keeps a per-result collection. Selection, failures
    /// and skips are decided once per result, in the same loop.
    /// </remarks>
    public static RequiredScope Resolve(PolicyRevisionInput policy, IReadOnlyList<CaseResultInput> results)
    {
        var requiredCaseKeys = new HashSet<string>(policy.RequiredCaseKeys, StringComparer.Ordinal);
        var requiredSuiteKeys = new HashSet<string>(policy.RequiredSuiteKeys, StringComparer.Ordinal);
        var criticalCaseIds = new HashSet<Guid>(policy.CriticalCaseIds);
        var narrowed = requiredCaseKeys.Count > 0 || requiredSuiteKeys.Count > 0;

        var pendingCaseKeys = new HashSet<string>(requiredCaseKeys, StringComparer.Ordinal);
        var pendingSuiteKeys = new HashSet<string>(requiredSuiteKeys, StringComparer.Ordinal);
        var pendingCaseIds = new HashSet<Guid>(criticalCaseIds);

        var resultCount = 0;
        var hasFailure = false;
        var hasNotExecuted = false;

        foreach (var result in results)
        {
            pendingCaseKeys.Remove(result.CaseKey);
            pendingSuiteKeys.Remove(result.SuiteKey);

            var critical = criticalCaseIds.Contains(result.CaseId);
            if (critical)
            {
                pendingCaseIds.Remove(result.CaseId);
            }

            var selected = critical
                || (narrowed
                    ? requiredCaseKeys.Contains(result.CaseKey) || requiredSuiteKeys.Contains(result.SuiteKey)
                    : result.Required);
            if (!selected)
            {
                continue;
            }

            resultCount++;
            hasFailure |= IsFailure(result.Status);
            hasNotExecuted |= result.Status is CaseStatus.Skipped or CaseStatus.Blocked;
        }

        return new RequiredScope(
            resultCount,
            hasFailure,
            hasNotExecuted,
            pendingCaseKeys.Count > 0 || pendingSuiteKeys.Count > 0 || pendingCaseIds.Count > 0);
    }

    private static bool IsFailure(CaseStatus status) =>
        status is CaseStatus.Failed or CaseStatus.TimedOut or CaseStatus.Error;
}
