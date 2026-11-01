namespace Evidgate.Evaluation;

/// <summary>
/// The wire token of every evaluator enum. Persistence and the read API store the contract's own spellings, so the
/// mapping lives in one place instead of being re-derived by each caller.
/// </summary>
public static class EvaluationCodes
{
    /// <summary>The <c>evidence_revision.completeness</c> token.</summary>
    public static string Code(EvaluationCompleteness value) => value switch
    {
        EvaluationCompleteness.Complete => "complete",
        EvaluationCompleteness.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "not a completeness value"),
    };

    /// <summary>The <c>evidence_revision.outcome</c> token.</summary>
    public static string Code(EvaluationOutcome value) => value switch
    {
        EvaluationOutcome.AllPassed => "all_passed",
        EvaluationOutcome.HasFailures => "has_failures",
        EvaluationOutcome.NoResults => "no_results",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "not an outcome value"),
    };

    /// <summary>The <c>evaluation.decision</c> token.</summary>
    public static string Code(EvaluationDecision value) => value switch
    {
        EvaluationDecision.Pass => "PASS",
        EvaluationDecision.Fail => "FAIL",
        EvaluationDecision.Inconclusive => "INCONCLUSIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "not a decision value"),
    };

    /// <summary>The <c>missing[]</c> token.</summary>
    public static string Code(MissingReasonCode value) => value switch
    {
        MissingReasonCode.MissingShard => "missing_shard",
        MissingReasonCode.CountMismatch => "count_mismatch",
        MissingReasonCode.ArtifactMissing => "artifact_missing",
        MissingReasonCode.BuildMismatch => "build_mismatch",
        MissingReasonCode.UnknownStatus => "unknown_status",
        MissingReasonCode.SetupError => "setup_error",
        MissingReasonCode.StaleEpoch => "stale_epoch",
        MissingReasonCode.MissingResult => "missing_result",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "not a missing code"),
    };

    /// <summary>The <c>reasons[]</c> token.</summary>
    public static string Code(EvaluationReason value) => value switch
    {
        EvaluationReason.ScopeBuildPolicyPinned => "scope_build_policy_pinned",
        EvaluationReason.RequiredAssertionFailed => "required_assertion_failed",
        EvaluationReason.MissingShard => "missing_shard",
        EvaluationReason.CountMismatch => "count_mismatch",
        EvaluationReason.ArtifactMissing => "artifact_missing",
        EvaluationReason.BuildMismatch => "build_mismatch",
        EvaluationReason.UnknownStatus => "unknown_status",
        EvaluationReason.SetupError => "setup_error",
        EvaluationReason.StaleEpochOnly => "stale_epoch_only",
        EvaluationReason.MissingResult => "missing_result",
        EvaluationReason.ExpectedCountZero => "expected_count_zero",
        EvaluationReason.EmptySuiteAllowed => "empty_suite_allowed",
        EvaluationReason.CriticalNotExecuted => "critical_not_executed",
        EvaluationReason.UnknownRequiredArtifactKind => "unknown_required_artifact_kind",
        EvaluationReason.FlakyBudgetExceeded => "flaky_budget_exceeded",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "not a reason code"),
    };

    /// <summary>The normalized case attempt status token.</summary>
    public static string Code(CaseStatus value) => value switch
    {
        CaseStatus.Passed => "passed",
        CaseStatus.Failed => "failed",
        CaseStatus.Skipped => "skipped",
        CaseStatus.TimedOut => "timed_out",
        CaseStatus.Error => "error",
        CaseStatus.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "not a case status"),
    };
}
