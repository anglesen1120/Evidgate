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
        var missing = EffectiveMissing(evidence, policy, scope);
        var completeness = CompletenessOf(evidence, policy, missing);
        var outcome = OutcomeOf(scope, missing);
        var (decision, reasons) = Decide(evidence, policy, scope, missing, completeness);

        return new EvaluationResult(completeness, missing, outcome, decision, reasons, decision);
    }

    /// <summary>
    /// The evidence's own codes, normalized into the closed vocabulary and ordered by wire token, plus the synthetic
    /// <c>missing_result</c> when nothing the evidence reports explains an absent required result. Malformed inputs
    /// resolve to closed codes rather than throwing or passing silently: a code or a status outside the enums is
    /// <c>unknown_status</c>, and a negative planned count is <c>missing_result</c> because it describes no execution
    /// at all (a producer's status is what <c>unknown_status</c> covers, not the plan).
    /// </summary>
    private static IReadOnlyList<MissingReasonCode> EffectiveMissing(
        EvidenceRevisionInput evidence,
        PolicyRevisionInput policy,
        RequiredScope scope)
    {
        var codes = new HashSet<MissingReasonCode>();
        var malformed = false;

        foreach (var code in evidence.Missing)
        {
            if (Enum.IsDefined(code))
            {
                codes.Add(code);
            }
            else
            {
                malformed = true;
            }
        }

        foreach (var result in evidence.Results)
        {
            malformed |= !Enum.IsDefined(result.Status);
        }

        if (malformed)
        {
            codes.Add(MissingReasonCode.UnknownStatus);
        }

        if (evidence.ExpectedExecutionCount < 0)
        {
            codes.Add(MissingReasonCode.MissingResult);
        }

        if (codes.Count == 0 && ExpectedResultMissing(evidence, policy, scope))
        {
            codes.Add(MissingReasonCode.MissingResult);
        }

        return [.. codes.OrderBy(EvaluationCodes.Code, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether the evidence owes a required result it never reported. The synthetic code is only added when no
    /// reported code already explains the absence (a stale-epoch-only revision, or an expected-count-zero run, keeps
    /// just its own code).
    /// </summary>
    private static bool ExpectedResultMissing(
        EvidenceRevisionInput evidence,
        PolicyRevisionInput policy,
        RequiredScope scope) =>
        !BlockedByEmptyRequiredScope(evidence, policy)
        && (scope.HasUncoveredRequiredItems
            || (evidence.ExpectedExecutionCount > 0
                && (scope.ResultCount == 0 || evidence.Results.Count < evidence.ExpectedExecutionCount)));

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
        if (scope.ResultCount == 0)
        {
            return EvaluationOutcome.NoResults;
        }

        return scope.HasFailure || missing.Contains(MissingReasonCode.SetupError)
            ? EvaluationOutcome.HasFailures
            : EvaluationOutcome.AllPassed;
    }

    private static (EvaluationDecision Decision, IReadOnlyList<EvaluationReason> Reasons) Decide(
        EvidenceRevisionInput evidence,
        PolicyRevisionInput policy,
        RequiredScope scope,
        IReadOnlyList<MissingReasonCode> missing,
        EvaluationCompleteness completeness)
    {
        var reasons = MissingReasons(missing);

        if (scope.HasFailure)
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

        if (scope.HasNotExecuted)
        {
            reasons.Add(EvaluationReason.CriticalNotExecuted);
            return (EvaluationDecision.Inconclusive, ReasonSet.Normalize(reasons));
        }

        if (evidence.AdvisoryOnly)
        {
            // Complete, clean evidence from a fork run still cannot release anything: the verdict stays open and says why.
            reasons.Add(EvaluationReason.AdvisoryOnlyRun);
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
