namespace Evidgate.Evaluation;

/// <summary>The executions the policy's required scope covers, and whether it names one the evidence never reported.</summary>
internal sealed record RequiredScope(IReadOnlyList<CaseResultInput> Results, bool HasUncoveredRequiredItems)
{
    /// <summary>
    /// Resolves the scope: the policy's required cases and suites when it names any (critical cases included), else the
    /// manifest's own required executions. A policy-named item with no result is reported, so it can never pass silently.
    /// </summary>
    public static RequiredScope Resolve(PolicyRevisionInput policy, IReadOnlyList<CaseResultInput> results)
    {
        var requiredCaseKeys = new HashSet<string>(policy.RequiredCaseKeys, StringComparer.Ordinal);
        var requiredSuiteKeys = new HashSet<string>(policy.RequiredSuiteKeys, StringComparer.Ordinal);
        var criticalCaseIds = new HashSet<Guid>(policy.CriticalCaseIds);
        var narrowed = requiredCaseKeys.Count > 0 || requiredSuiteKeys.Count > 0;

        var inScope = new List<CaseResultInput>(results.Count);
        foreach (var result in results)
        {
            var selected = narrowed
                ? requiredCaseKeys.Contains(result.CaseKey)
                    || requiredSuiteKeys.Contains(result.SuiteKey)
                    || criticalCaseIds.Contains(result.CaseId)
                : result.Required;

            if (selected)
            {
                inScope.Add(result);
            }
        }

        return new RequiredScope(inScope, HasUncoveredItems(results, requiredCaseKeys, requiredSuiteKeys, criticalCaseIds));
    }

    private static bool HasUncoveredItems(
        IReadOnlyList<CaseResultInput> results,
        HashSet<string> requiredCaseKeys,
        HashSet<string> requiredSuiteKeys,
        HashSet<Guid> criticalCaseIds)
    {
        if (requiredCaseKeys.Count == 0 && requiredSuiteKeys.Count == 0 && criticalCaseIds.Count == 0)
        {
            return false;
        }

        var caseKeys = new HashSet<string>(results.Select(result => result.CaseKey), StringComparer.Ordinal);
        var suiteKeys = new HashSet<string>(results.Select(result => result.SuiteKey), StringComparer.Ordinal);
        var caseIds = new HashSet<Guid>(results.Select(result => result.CaseId));

        return requiredCaseKeys.Any(key => !caseKeys.Contains(key))
            || requiredSuiteKeys.Any(key => !suiteKeys.Contains(key))
            || criticalCaseIds.Any(id => !caseIds.Contains(id));
    }
}
