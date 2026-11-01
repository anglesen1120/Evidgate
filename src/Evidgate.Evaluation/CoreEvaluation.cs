using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Evaluation;

/// <summary>
/// The core computation: completeness and outcome are derived independently, and the decision follows the contract's
/// order — a confirmed required failure first (kept even when the evidence is incomplete), then unusable evidence, then a
/// required case that was never executed, then a pass.
/// </summary>
internal static class CoreEvaluation
{
    /// <summary>Computes the core result for one evidence and policy pair.</summary>
    public static EvaluationResult Compute(EvidenceRevisionInput evidence, PolicyRevisionInput policy)
    {
        var scope = RequiredScope.Resolve(policy, evidence.Results);
        var missing = EffectiveMissing(evidence, scope);
        var completeness = CompletenessOf(evidence, policy, missing);
        var outcome = OutcomeOf(scope, missing);
        var (decision, reasons) = Decide(evidence, policy, scope, missing, completeness);

        return new EvaluationResult(completeness, missing, outcome, decision, reasons, decision);
    }

    /// <summary>
    /// The evidence's own codes, plus <c>missing_result</c> when the required scope is uncovered. The synthetic code is
    /// only added when no reported code already explains the absence (a stale-epoch-only revision keeps just its code).
    /// </summary>
    private static IReadOnlyList<MissingReasonCode> EffectiveMissing(EvidenceRevisionInput evidence, RequiredScope scope)
    {
        var codes = new List<MissingReasonCode>(evidence.Missing);

        var uncovered = evidence.Results.Count == 0
            ? codes.Count == 0
            : scope.HasUncoveredRequiredItems;
        if (evidence.ExpectedExecutionCount > 0 && uncovered)
        {
            codes.Add(MissingReasonCode.MissingResult);
        }

        codes.Sort();
        return [.. codes.Distinct()];
    }

    private static EvaluationCompleteness CompletenessOf(
        EvidenceRevisionInput evidence,
        PolicyRevisionInput policy,
        IReadOnlyList<MissingReasonCode> missing)
    {
        var emptyRun = evidence.ExpectedExecutionCount == 0;
        var emptySuiteAllowed = emptyRun && evidence.SuiteDeclaredEmpty && policy.EmptySuiteAllowed;

        return missing.Count == 0 && (!emptyRun || emptySuiteAllowed)
            ? EvaluationCompleteness.Complete
            : EvaluationCompleteness.Incomplete;
    }

    private static EvaluationOutcome OutcomeOf(RequiredScope scope, IReadOnlyList<MissingReasonCode> missing)
    {
        if (scope.Results.Count == 0)
        {
            return EvaluationOutcome.NoResults;
        }

        if (scope.Results.Any(result => IsFailure(result.Status))
            || missing.Contains(MissingReasonCode.SetupError))
        {
            return EvaluationOutcome.HasFailures;
        }

        return EvaluationOutcome.AllPassed;
    }

    private static (EvaluationDecision Decision, IReadOnlyList<EvaluationReason> Reasons) Decide(
        EvidenceRevisionInput evidence,
        PolicyRevisionInput policy,
        RequiredScope scope,
        IReadOnlyList<MissingReasonCode> missing,
        EvaluationCompleteness completeness)
    {
        var reasons = MissingReasons(missing);

        if (scope.Results.Any(result => IsFailure(result.Status)))
        {
            // A confirmed failure is the whole explanation: the codes behind an incomplete verdict stay in Missing.
            return (EvaluationDecision.Fail, [EvaluationReason.RequiredAssertionFailed]);
        }

        if (completeness == EvaluationCompleteness.Incomplete)
        {
            if (BlockedByEmptyRequiredScope(evidence, policy))
            {
                reasons.Add(EvaluationReason.ExpectedCountZero);
            }

            return (EvaluationDecision.Inconclusive, ReasonSet.Normalize(reasons));
        }

        if (policy.RequiredArtifactKinds.Any(kind => !ArtifactKinds.IsKnown(kind)))
        {
            reasons.Add(EvaluationReason.UnknownRequiredArtifactKind);
            return (EvaluationDecision.Inconclusive, ReasonSet.Normalize(reasons));
        }

        if (scope.Results.Any(result => result.Status is CaseStatus.Skipped or CaseStatus.Blocked))
        {
            reasons.Add(EvaluationReason.CriticalNotExecuted);
            return (EvaluationDecision.Inconclusive, ReasonSet.Normalize(reasons));
        }

        reasons.Add(evidence.ExpectedExecutionCount == 0
            ? EvaluationReason.EmptySuiteAllowed
            : EvaluationReason.ScopeBuildPolicyPinned);
        return (EvaluationDecision.Pass, ReasonSet.Normalize(reasons));
    }

    private static bool BlockedByEmptyRequiredScope(EvidenceRevisionInput evidence, PolicyRevisionInput policy) =>
        evidence.ExpectedExecutionCount == 0
        && !(evidence.SuiteDeclaredEmpty && policy.EmptySuiteAllowed);

    private static bool IsFailure(CaseStatus status) =>
        status is CaseStatus.Failed or CaseStatus.TimedOut or CaseStatus.Error;

    /// <summary>One reason per missing code; <c>stale_epoch</c> yields a reason only when it is the sole code.</summary>
    private static List<EvaluationReason> MissingReasons(IReadOnlyList<MissingReasonCode> missing)
    {
        var staleOnly = missing.Count == 1 && missing[0] == MissingReasonCode.StaleEpoch;
        var reasons = new List<EvaluationReason>(missing.Count);

        foreach (var code in missing)
        {
            var reason = code switch
            {
                MissingReasonCode.MissingShard => EvaluationReason.MissingShard,
                MissingReasonCode.CountMismatch => EvaluationReason.CountMismatch,
                MissingReasonCode.ArtifactMissing => EvaluationReason.ArtifactMissing,
                MissingReasonCode.BuildMismatch => EvaluationReason.BuildMismatch,
                MissingReasonCode.UnknownStatus => EvaluationReason.UnknownStatus,
                MissingReasonCode.SetupError => EvaluationReason.SetupError,
                MissingReasonCode.StaleEpoch when staleOnly => EvaluationReason.StaleEpochOnly,
                MissingReasonCode.MissingResult => EvaluationReason.MissingResult,
                _ => (EvaluationReason?)null,
            };

            if (reason is { } value)
            {
                reasons.Add(value);
            }
        }

        return reasons;
    }
}
