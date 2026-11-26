namespace Evidgate.Evaluation;

/// <summary>
/// Applies the policy's flaky rules to the decision the layers produced. It may turn a pass or an inconclusive
/// verdict into a fail for a critical case, and a pass into an inconclusive verdict over budget; it never raises.
/// </summary>
internal static class FlakyBudgetPolicy
{
    /// <summary>The cases whose recovered retry forced a critical failure; what a waiver may annotate.</summary>
    public static IReadOnlySet<Guid> CriticalCaseIds(IReadOnlyList<FlakyFact> facts, PolicyRevisionInput policy)
    {
        var critical = new HashSet<Guid>(policy.CriticalCaseIds);
        return facts.Where(fact => critical.Contains(fact.CaseId)).Select(fact => fact.CaseId).ToHashSet();
    }

    /// <summary>Adds the flaky reasons to <paramref name="reasons"/> and returns the decision they leave.</summary>
    public static EvaluationDecision Apply(
        IReadOnlyList<FlakyFact> facts,
        PolicyRevisionInput policy,
        ExceptionsSnapshotInput exceptions,
        EvaluationDecision decision,
        List<EvaluationReason> reasons)
    {
        if (facts.Count == 0)
        {
            return decision;
        }

        var critical = new HashSet<Guid>(policy.CriticalCaseIds);
        var quarantined = new HashSet<Guid>(exceptions.QuarantinedCaseIds);
        var budgeted = 0;
        var anyCritical = false;
        foreach (var fact in facts)
        {
            if (critical.Contains(fact.CaseId))
            {
                anyCritical = true;
            }
            else if (!quarantined.Contains(fact.CaseId))
            {
                budgeted++;
            }
        }

        if (anyCritical)
        {
            // A retry that recovers a critical case never launders its failure, whatever else the evidence says.
            decision = EvaluationDecision.Fail;
            reasons.Add(EvaluationReason.FlakyCriticalRetryPass);
        }

        if (budgeted == 0)
        {
            return decision;
        }

        if (budgeted <= policy.FlakyBudget)
        {
            reasons.Add(EvaluationReason.FlakyWithinBudget);
            return decision;
        }

        reasons.Add(EvaluationReason.FlakyBudgetExceeded);
        return decision == EvaluationDecision.Pass ? EvaluationDecision.Inconclusive : decision;
    }
}
