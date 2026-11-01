namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

/// <summary>Every enum the evaluator emits is a closed contract list; a new member without a contract token fails here.</summary>
public sealed class ReasonCatalogueTests
{
    private static readonly string[] ContractReasons =
    [
        "artifact_missing",
        "build_mismatch",
        "count_mismatch",
        "critical_not_executed",
        "empty_suite_allowed",
        "expected_count_zero",
        "flaky_budget_exceeded",
        "missing_result",
        "missing_shard",
        "required_assertion_failed",
        "scope_build_policy_pinned",
        "setup_error",
        "stale_epoch_only",
        "unknown_required_artifact_kind",
        "unknown_status",
    ];

    private static readonly string[] ContractMissing =
    [
        "artifact_missing",
        "build_mismatch",
        "count_mismatch",
        "missing_result",
        "missing_shard",
        "setup_error",
        "stale_epoch",
        "unknown_status",
    ];

    [Fact]
    public void Reason_Catalogue_Matches_The_Contract()
    {
        var tokens = Enum.GetValues<EvaluationReason>().Select(EvaluationCodes.Code).ToArray();

        tokens.ShouldBe(ContractReasons, ignoreOrder: true);
        tokens.Distinct(StringComparer.Ordinal).Count().ShouldBe(tokens.Length);
    }

    [Fact]
    public void Missing_Catalogue_Matches_The_Contract()
    {
        var tokens = Enum.GetValues<MissingReasonCode>().Select(EvaluationCodes.Code).ToArray();

        tokens.ShouldBe(ContractMissing, ignoreOrder: true);
        tokens.Distinct(StringComparer.Ordinal).Count().ShouldBe(tokens.Length);
    }

    [Fact]
    public void Every_Enum_Maps_To_A_Token()
    {
        Enum.GetValues<EvaluationCompleteness>().Select(EvaluationCodes.Code).ShouldBe(["complete", "incomplete"]);
        Enum.GetValues<EvaluationOutcome>().Select(EvaluationCodes.Code).ShouldBe(["all_passed", "has_failures", "no_results"]);
        Enum.GetValues<EvaluationDecision>().Select(EvaluationCodes.Code).ShouldBe(["PASS", "FAIL", "INCONCLUSIVE"]);
        Enum.GetValues<CaseStatus>().Select(EvaluationCodes.Code).ShouldBe(["passed", "failed", "skipped", "timed_out", "error", "blocked"]);
    }
}
