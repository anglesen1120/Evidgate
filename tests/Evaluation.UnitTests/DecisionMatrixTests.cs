namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

/// <summary>The golden rows 00 §8.6 assigns to this story, each as one named theory case.</summary>
public sealed class DecisionMatrixTests
{
    public static TheoryData<string> RowNames => ["M1_All_Required_Cases_Passed", "M2_RequiredFailureWithMissingEvidence", "M3_MissingShard", "M4_BuildMismatch", "M5_UnknownStatus", "M6_SetupError", "M7_ExpectedCountZero", "M8_ExpectedCountZeroDeclaredEmptyAllowed", "M9_CriticalCaseSkipped", "M13_RequiredArtifactMissing", "M16_StaleEpochOnly"];

    [Theory]
    [MemberData(nameof(RowNames))]
    public void Matrix_Row_Produces_The_Contract_Verdict(string row)
    {
        var expected = Scenario(row);
        var result = new PolicyEvaluator().Evaluate(expected.Evidence, expected.Policy, Scenarios.NoExceptions);

        result.Completeness.ShouldBe(expected.Completeness);
        result.Outcome.ShouldBe(expected.Outcome);
        result.Decision.ShouldBe(expected.Decision);
        result.CoreDecision.ShouldBe(expected.Decision);
        result.Missing.ShouldBe(expected.Missing);
        result.Reasons.ShouldBe(expected.Reasons);
    }

    private static Row Scenario(string name) => name switch
    {
        "M1_All_Required_Cases_Passed" => new(
            Scenarios.Evidence(),
            Scenarios.Policy(),
            EvaluationCompleteness.Complete,
            EvaluationOutcome.AllPassed,
            EvaluationDecision.Pass,
            [],
            [EvaluationReason.ScopeBuildPolicyPinned]),

        "M2_RequiredFailureWithMissingEvidence" => new(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Failed)], [MissingReasonCode.MissingShard]),
            Scenarios.Policy(),
            EvaluationCompleteness.Incomplete,
            EvaluationOutcome.HasFailures,
            EvaluationDecision.Fail,
            [MissingReasonCode.MissingShard],
            [EvaluationReason.RequiredAssertionFailed]),

        "M3_MissingShard" => new(
            Scenarios.Evidence(missing: [MissingReasonCode.MissingShard]),
            Scenarios.Policy(),
            EvaluationCompleteness.Incomplete,
            EvaluationOutcome.AllPassed,
            EvaluationDecision.Inconclusive,
            [MissingReasonCode.MissingShard],
            [EvaluationReason.MissingShard]),

        "M4_BuildMismatch" => new(
            Scenarios.Evidence(missing: [MissingReasonCode.BuildMismatch]),
            Scenarios.Policy(),
            EvaluationCompleteness.Incomplete,
            EvaluationOutcome.AllPassed,
            EvaluationDecision.Inconclusive,
            [MissingReasonCode.BuildMismatch],
            [EvaluationReason.BuildMismatch]),

        "M5_UnknownStatus" => new(
            Scenarios.Evidence(missing: [MissingReasonCode.UnknownStatus]),
            Scenarios.Policy(),
            EvaluationCompleteness.Incomplete,
            EvaluationOutcome.AllPassed,
            EvaluationDecision.Inconclusive,
            [MissingReasonCode.UnknownStatus],
            [EvaluationReason.UnknownStatus]),

        "M6_SetupError" => new(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Blocked)], [MissingReasonCode.SetupError]),
            Scenarios.Policy(),
            EvaluationCompleteness.Incomplete,
            EvaluationOutcome.HasFailures,
            EvaluationDecision.Inconclusive,
            [MissingReasonCode.SetupError],
            [EvaluationReason.SetupError]),

        "M7_ExpectedCountZero" => new(
            Scenarios.Evidence([], [], 0),
            Scenarios.Policy(),
            EvaluationCompleteness.Incomplete,
            EvaluationOutcome.NoResults,
            EvaluationDecision.Inconclusive,
            [],
            [EvaluationReason.ExpectedCountZero]),

        "M8_ExpectedCountZeroDeclaredEmptyAllowed" => new(
            Scenarios.Evidence([], [], 0, suiteDeclaredEmpty: true),
            Scenarios.Policy(emptySuiteAllowed: true),
            EvaluationCompleteness.Complete,
            EvaluationOutcome.NoResults,
            EvaluationDecision.Pass,
            [],
            [EvaluationReason.EmptySuiteAllowed]),

        "M9_CriticalCaseSkipped" => new(
            Scenarios.Evidence(
                [Scenarios.Result(CaseStatus.Skipped), Scenarios.Result(CaseStatus.Passed, Scenarios.SearchCaseId, Scenarios.SearchKey)],
                expectedExecutionCount: 2),
            Scenarios.Policy(requiredCaseKeys: [], criticalCaseIds: [Scenarios.CheckoutCaseId]),
            EvaluationCompleteness.Complete,
            EvaluationOutcome.AllPassed,
            EvaluationDecision.Inconclusive,
            [],
            [EvaluationReason.CriticalNotExecuted]),

        "M13_RequiredArtifactMissing" => new(
            Scenarios.Evidence(missing: [MissingReasonCode.ArtifactMissing]),
            Scenarios.Policy(),
            EvaluationCompleteness.Incomplete,
            EvaluationOutcome.AllPassed,
            EvaluationDecision.Inconclusive,
            [MissingReasonCode.ArtifactMissing],
            [EvaluationReason.ArtifactMissing]),

        "M16_StaleEpochOnly" => new(
            Scenarios.Evidence([], [MissingReasonCode.StaleEpoch]),
            Scenarios.Policy(),
            EvaluationCompleteness.Incomplete,
            EvaluationOutcome.NoResults,
            EvaluationDecision.Inconclusive,
            [MissingReasonCode.StaleEpoch],
            [EvaluationReason.StaleEpochOnly]),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a matrix row of this story"),
    };

    private sealed record Row(
        EvidenceRevisionInput Evidence,
        PolicyRevisionInput Policy,
        EvaluationCompleteness Completeness,
        EvaluationOutcome Outcome,
        EvaluationDecision Decision,
        IReadOnlyList<MissingReasonCode> Missing,
        IReadOnlyList<EvaluationReason> Reasons);
}
