namespace Evidgate.Evaluation;

/// <summary>
/// The executions the policy's required scope covers, summarized in one pass, plus whether it names an item the
/// evidence never reported.
/// </summary>
/// <param name="ResultCount">How many reported results the required scope holds.</param>
/// <param name="HasFailure">
/// True when a result in scope reports a confirmed failure that still gates: a quarantined non-critical case's
/// failure does not count.
/// </param>
/// <param name="HasNotExecuted">True when a result in scope was skipped or blocked.</param>
/// <param name="HasUncoveredRequiredItems">
/// True when the policy names a required case, a required suite or a critical case id the evidence never reported.
/// </param>
/// <param name="QuarantinedCases">
/// The non-critical quarantined cases whose confirmed failure was excluded, ascending and without duplicates.
/// </param>
/// <param name="FailedCaseIds">
/// The cases whose confirmed failure still gates; filled only when the snapshot holds a case waiver, the one reader.
/// </param>
internal sealed record RequiredScope(
    int ResultCount,
    bool HasFailure,
    bool HasNotExecuted,
    bool HasUncoveredRequiredItems,
    IReadOnlyList<Guid> QuarantinedCases,
    IReadOnlySet<Guid> FailedCaseIds)
{
    /// <summary>
    /// Resolves the scope: the policy's required cases and suites when it names either, else the manifest's own
    /// required executions, with a critical case always in scope whatever the manifest said. A policy-named item with
    /// no result is reported, so it can never pass silently.
    /// </summary>
    /// <remarks>
    /// Coverage is inverted: the pending sets start as copies of the policy's own lists and shrink as results resolve
    /// them, so the sweep allocates in the policy's size and never keeps a per-result collection. The quarantine and
    /// failed-case sets exist only when the snapshot carries quarantined or waived cases.
    /// </remarks>
    public static RequiredScope Resolve(
        PolicyRevisionInput policy,
        IReadOnlyList<CaseResultInput> results,
        ExceptionsSnapshotInput exceptions)
    {
        var sweep = new Sweep(policy, exceptions);
        foreach (var result in results)
        {
            sweep.Add(result);
        }

        return sweep.ToScope();
    }

    /// <summary>True for the statuses that count as a failing assertion; skipped and blocked are not failures.</summary>
    internal static bool IsFailure(CaseStatus status) =>
        status is CaseStatus.Failed or CaseStatus.TimedOut or CaseStatus.Error;

    private sealed class Sweep
    {
        private static readonly IReadOnlySet<Guid> NoCases = new HashSet<Guid>();

        private readonly HashSet<string> requiredCaseKeys;
        private readonly HashSet<string> requiredSuiteKeys;
        private readonly HashSet<Guid> criticalCaseIds;
        private readonly HashSet<Guid>? quarantined;
        private readonly HashSet<Guid>? failedCaseIds;
        private readonly bool narrowed;
        private readonly HashSet<string> pendingCaseKeys;
        private readonly HashSet<string> pendingSuiteKeys;
        private readonly HashSet<Guid> pendingCaseIds;
        private SortedSet<Guid>? excluded;
        private int resultCount;
        private bool hasFailure;
        private bool hasNotExecuted;

        public Sweep(PolicyRevisionInput policy, ExceptionsSnapshotInput exceptions)
        {
            requiredCaseKeys = new HashSet<string>(policy.RequiredCaseKeys, StringComparer.Ordinal);
            requiredSuiteKeys = new HashSet<string>(policy.RequiredSuiteKeys, StringComparer.Ordinal);
            criticalCaseIds = new HashSet<Guid>(policy.CriticalCaseIds);
            narrowed = requiredCaseKeys.Count > 0 || requiredSuiteKeys.Count > 0;
            pendingCaseKeys = new HashSet<string>(requiredCaseKeys, StringComparer.Ordinal);
            pendingSuiteKeys = new HashSet<string>(requiredSuiteKeys, StringComparer.Ordinal);
            pendingCaseIds = new HashSet<Guid>(criticalCaseIds);

            if (exceptions.QuarantinedCaseIds.Count > 0)
            {
                quarantined = new HashSet<Guid>(exceptions.QuarantinedCaseIds);
            }

            if (exceptions.WaivedCaseIds.Count > 0)
            {
                failedCaseIds = [];
            }
        }

        public void Add(CaseResultInput result)
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
                return;
            }

            resultCount++;
            hasNotExecuted |= result.Status is CaseStatus.Skipped or CaseStatus.Blocked;
            if (!IsFailure(result.Status))
            {
                return;
            }

            // A critical case never takes the quarantine exemption, whatever the snapshot says.
            if (!critical && quarantined is not null && quarantined.Contains(result.CaseId))
            {
                (excluded ??= []).Add(result.CaseId);
                return;
            }

            hasFailure = true;
            failedCaseIds?.Add(result.CaseId);
        }

        public RequiredScope ToScope() => new(
            resultCount,
            hasFailure,
            hasNotExecuted,
            pendingCaseKeys.Count > 0 || pendingSuiteKeys.Count > 0 || pendingCaseIds.Count > 0,
            excluded is null ? [] : [.. excluded],
            failedCaseIds ?? NoCases);
    }
}
