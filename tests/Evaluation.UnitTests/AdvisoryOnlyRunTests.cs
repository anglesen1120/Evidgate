namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

/// <summary>
/// A fork run is advisory-only: its evidence is judged like any other, but the verdict can never be a pass, and the
/// reason says why.
/// </summary>
public sealed class AdvisoryOnlyRunTests
{
    private static EvaluationResult Evaluate(EvidenceRevisionInput evidence, bool emptySuiteAllowed = false) =>
        new PolicyEvaluator().Evaluate(
            evidence,
            Scenarios.Policy(emptySuiteAllowed: emptySuiteAllowed),
            Scenarios.NoExceptions);

    [Fact]
    public void Evaluate_ForkRunWithCompleteCleanEvidence_IsInconclusiveWithTheAdvisoryReason()
    {
        var result = Evaluate(Scenarios.Evidence(advisoryOnly: true));

        result.Completeness.ShouldBe(EvaluationCompleteness.Complete);
        result.Outcome.ShouldBe(EvaluationOutcome.AllPassed);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["advisory_only_run"]);
    }

    [Fact]
    public void Evaluate_SameEvidenceOfANonForkRun_StillPasses()
    {
        var result = Evaluate(Scenarios.Evidence());

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["scope_build_policy_pinned"]);
    }

    [Fact]
    public void Evaluate_ForkRunWithAnAllowedEmptySuite_DoesNotPass()
    {
        var result = Evaluate(
            Scenarios.Evidence(results: [], expectedExecutionCount: 0, suiteDeclaredEmpty: true, advisoryOnly: true),
            emptySuiteAllowed: true);

        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["advisory_only_run"]);
    }

    [Fact]
    public void Evaluate_ForkRunWithMissingEvidence_KeepsItsMissingReasonsAndNeverPasses()
    {
        var result = Evaluate(
            Scenarios.Evidence(missing: [MissingReasonCode.MissingShard], advisoryOnly: true));

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["missing_shard"]);
    }

    [Fact]
    public void Evaluate_ForkRunWithARequiredFailure_StaysFailed()
    {
        var result = Evaluate(
            Scenarios.Evidence(results: [Scenarios.Result(CaseStatus.Failed)], advisoryOnly: true));

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["required_assertion_failed"]);
    }

    [Fact]
    public void Evaluate_ForkRunWithASkippedRequiredCase_ReportsTheCaseNotTheFork()
    {
        var result = Evaluate(
            Scenarios.Evidence(results: [Scenarios.Result(CaseStatus.Skipped)], advisoryOnly: true));

        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["critical_not_executed"]);
    }
}
