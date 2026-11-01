namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

public sealed class PolicyEvaluatorTests
{
    [Fact]
    public void Evaluate_StructurallyEqualInputs_ReturnTheSameResult()
    {
        var (firstEvidence, firstPolicy, firstSnapshot) = Inputs();
        var (secondEvidence, secondPolicy, secondSnapshot) = Inputs();

        firstEvidence.ShouldNotBeSameAs(secondEvidence);
        firstPolicy.ShouldNotBeSameAs(secondPolicy);

        var first = new PolicyEvaluator().Evaluate(firstEvidence, firstPolicy, firstSnapshot);
        var second = new PolicyEvaluator().Evaluate(secondEvidence, secondPolicy, secondSnapshot);

        Fingerprint(second).ShouldBe(Fingerprint(first));
        second.Completeness.ShouldBe(first.Completeness);
        second.Outcome.ShouldBe(first.Outcome);
        second.Decision.ShouldBe(first.Decision);
        second.Missing.ShouldBe(first.Missing);
        second.Reasons.ShouldBe(first.Reasons);
    }

    [Fact]
    public void Evaluate_RepeatedOnTheSameInputs_KeepsOneResult()
    {
        var (evidence, policy, snapshot) = Inputs();
        var evaluator = new PolicyEvaluator();

        var fingerprints = Enumerable.Range(0, 100)
            .Select(_ => Fingerprint(evaluator.Evaluate(evidence, policy, snapshot)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        fingerprints.Length.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_MissingAndReasons_AreSortedByTokenWithoutDuplicates()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(
                missing: [MissingReasonCode.UnknownStatus, MissingReasonCode.BuildMismatch, MissingReasonCode.UnknownStatus]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Missing.Select(EvaluationCodes.Code).ShouldBe(["build_mismatch", "unknown_status"]);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["build_mismatch", "unknown_status"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_EmptyRequiredScope_FollowsTheAllowance(bool emptySuiteAllowed)
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([], [], 0, suiteDeclaredEmpty: true),
            Scenarios.Policy(requiredCaseKeys: [], emptySuiteAllowed: emptySuiteAllowed),
            Scenarios.NoExceptions);

        result.Outcome.ShouldBe(EvaluationOutcome.NoResults);
        result.Missing.ShouldBeEmpty();

        if (emptySuiteAllowed)
        {
            result.Completeness.ShouldBe(EvaluationCompleteness.Complete);
            result.Decision.ShouldBe(EvaluationDecision.Pass);
            result.Reasons.ShouldBe([EvaluationReason.EmptySuiteAllowed]);
        }
        else
        {
            result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
            result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
            result.Reasons.ShouldBe([EvaluationReason.ExpectedCountZero]);
        }
    }

    [Fact]
    public void Evaluate_EmptyRequiredScope_UndeclaredSuite_StaysInconclusive()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([], [], 0),
            Scenarios.Policy(emptySuiteAllowed: true),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.ExpectedCountZero]);
    }

    [Theory]
    [InlineData(CaseStatus.Skipped)]
    [InlineData(CaseStatus.Blocked)]
    public void Evaluate_CriticalCaseNotExecuted_IsNeverPass(CaseStatus status)
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(
                [Scenarios.Result(status), Scenarios.Result(CaseStatus.Passed, Scenarios.SearchCaseId, Scenarios.SearchKey)],
                expectedExecutionCount: 2),
            Scenarios.Policy(requiredCaseKeys: [], criticalCaseIds: [Scenarios.CheckoutCaseId]),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Complete);
        result.Outcome.ShouldBe(EvaluationOutcome.AllPassed);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.CriticalNotExecuted]);
    }

    [Fact]
    public void Evaluate_RequiredSkipCoveredByAllowedSkips_IsStillNotPass()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Skipped)]),
            Scenarios.Policy(allowedSkipCaseKeys: [Scenarios.CheckoutKey]),
            Scenarios.NoExceptions);

        result.Decision.ShouldNotBe(EvaluationDecision.Pass);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.CriticalNotExecuted]);
    }

    [Theory]
    [InlineData(MissingReasonCode.MissingShard)]
    [InlineData(MissingReasonCode.CountMismatch)]
    [InlineData(MissingReasonCode.ArtifactMissing)]
    [InlineData(MissingReasonCode.BuildMismatch)]
    [InlineData(MissingReasonCode.UnknownStatus)]
    [InlineData(MissingReasonCode.SetupError)]
    [InlineData(MissingReasonCode.StaleEpoch)]
    [InlineData(MissingReasonCode.MissingResult)]
    public void Evaluate_AnyMissingCode_NeverYieldsPass(MissingReasonCode code)
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(missing: [code]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Missing.ShouldBe([code]);
    }

    [Theory]
    [InlineData(CaseStatus.Failed)]
    [InlineData(CaseStatus.TimedOut)]
    [InlineData(CaseStatus.Error)]
    public void Evaluate_ConfirmedRequiredFailure_YieldsFailAndIncomplete(CaseStatus status)
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([Scenarios.Result(status)], [MissingReasonCode.MissingShard]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Outcome.ShouldBe(EvaluationOutcome.HasFailures);
        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed]);
    }

    [Fact]
    public void Evaluate_SetupErrorDependants_BlockAndNeverPass()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(
                [Scenarios.Result(CaseStatus.Blocked), Scenarios.Result(CaseStatus.Blocked, Scenarios.SearchCaseId, Scenarios.SearchKey)],
                [MissingReasonCode.SetupError],
                expectedExecutionCount: 2),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Outcome.ShouldBe(EvaluationOutcome.HasFailures);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.SetupError]);
    }

    [Fact]
    public void Evaluate_RequiredCaseWithoutResult_ReportsMissingResult()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(
                [Scenarios.Result(CaseStatus.Passed)],
                expectedExecutionCount: 2),
            Scenarios.Policy(requiredCaseKeys: [Scenarios.CheckoutKey, Scenarios.SearchKey]),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Missing.ShouldBe([MissingReasonCode.MissingResult]);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.MissingResult]);
    }

    [Fact]
    public void Evaluate_CountMismatch_IsInconclusive()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(missing: [MissingReasonCode.CountMismatch]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.CountMismatch]);
    }

    [Fact]
    public void Evaluate_PolicyRequiresAnUnknownArtifactKind_IsInconclusive()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(),
            Scenarios.Policy(requiredArtifactKinds: ["hologram"]),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Complete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.UnknownRequiredArtifactKind]);
    }

    [Fact]
    public void Evaluate_OptionalCaseOutsideTheRequiredScope_DoesNotDecide()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(
                [Scenarios.Result(CaseStatus.Passed), Scenarios.Result(CaseStatus.Failed, Scenarios.SearchCaseId, Scenarios.SearchKey)],
                expectedExecutionCount: 2),
            Scenarios.Policy(requiredCaseKeys: [Scenarios.CheckoutKey]),
            Scenarios.NoExceptions);

        result.Outcome.ShouldBe(EvaluationOutcome.AllPassed);
        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
    }

    [Fact]
    public void Evaluate_FewerResultsThanPlannedWithoutMissingCode_ReportsMissingResult()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Passed)], [], expectedExecutionCount: 2),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Missing.ShouldBe([MissingReasonCode.MissingResult]);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.MissingResult]);
    }

    [Fact]
    public void Evaluate_EmptyRequiredScopeWithPlannedExecutions_ReportsMissingResult()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Passed, required: false)]),
            Scenarios.Policy(requiredCaseKeys: []),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Missing.ShouldBe([MissingReasonCode.MissingResult]);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
    }

    [Fact]
    public void Evaluate_CriticalCaseWithoutAnyResult_NeverPasses()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Passed)]),
            Scenarios.Policy(requiredCaseKeys: [], criticalCaseIds: [Scenarios.SearchCaseId]),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Missing.ShouldBe([MissingReasonCode.MissingResult]);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
    }

    [Fact]
    public void Evaluate_MissingCodesFollowTheWireTokenOrder()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(missing: [MissingReasonCode.MissingShard, MissingReasonCode.CountMismatch, MissingReasonCode.MissingShard]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Missing.Select(EvaluationCodes.Code).ShouldBe(["count_mismatch", "missing_shard"]);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["count_mismatch", "missing_shard"]);
    }

    [Fact]
    public void Evaluate_MalformedMissingCode_ResolvesToUnknownStatus()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(missing: [(MissingReasonCode)99]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Missing.ShouldBe([MissingReasonCode.UnknownStatus]);
        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.UnknownStatus]);
    }

    [Fact]
    public void Evaluate_MalformedCaseStatus_ResolvesToUnknownStatus()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([Scenarios.Result((CaseStatus)42)]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Missing.ShouldBe([MissingReasonCode.UnknownStatus]);
        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.UnknownStatus]);
    }

    [Fact]
    public void Evaluate_NegativeExpectedCount_ReportsMissingResult()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([], [], expectedExecutionCount: -1),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Missing.ShouldBe([MissingReasonCode.MissingResult]);
        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.MissingResult]);
    }

    [Fact]
    public void Evaluate_EmptySuiteAllowance_DoesNotCoverARequiredCaseTheEvidenceNeverReported()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([], [], 0, suiteDeclaredEmpty: true),
            Scenarios.Policy(requiredCaseKeys: [Scenarios.CheckoutKey], emptySuiteAllowed: true),
            Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Missing.ShouldBe([MissingReasonCode.MissingResult]);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.MissingResult]);
    }

    [Fact]
    public void Evaluate_PolicyCollectionsChangedAfterConstruction_DoNotChangeTheVerdict()
    {
        var requiredCaseKeys = new List<string> { Scenarios.CheckoutKey };
        var policy = Scenarios.Policy(requiredCaseKeys: requiredCaseKeys);
        var evidence = Scenarios.Evidence();
        var evaluator = new PolicyEvaluator();

        var before = evaluator.Evaluate(evidence, policy, Scenarios.NoExceptions);
        before.Decision.ShouldBe(EvaluationDecision.Pass);

        requiredCaseKeys.Clear();
        requiredCaseKeys.Add(Scenarios.SearchKey);
        var after = evaluator.Evaluate(evidence, policy, Scenarios.NoExceptions);

        Fingerprint(after).ShouldBe(Fingerprint(before));
        after.Decision.ShouldBe(EvaluationDecision.Pass);
    }

    [Fact]
    public void Evaluate_LargeResultSet_AllocatesNoPerResultScopeSet()
    {
        var results = new List<CaseResultInput>(50_000);
        for (var index = 0; index < 50_000; index++)
        {
            results.Add(new CaseResultInput(
                Scenarios.SearchCaseId,
                $"case/{index}",
                Scenarios.SmokeSuite,
                CaseStatus.Passed,
                Required: false));
        }

        var evidence = Scenarios.Evidence(results, [], expectedExecutionCount: 50_000);
        var policy = Scenarios.Policy(requiredCaseKeys: [], criticalCaseIds: []);
        var evaluator = new PolicyEvaluator();

        evaluator.Evaluate(evidence, policy, Scenarios.NoExceptions);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = evaluator.Evaluate(evidence, policy, Scenarios.NoExceptions);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        allocated.ShouldBeLessThan(256 * 1024);
    }

    internal static (EvidenceRevisionInput Evidence, PolicyRevisionInput Policy, ExceptionsSnapshotInput Snapshot) Inputs() => (
        Scenarios.Evidence(
            [Scenarios.Result(CaseStatus.Failed), Scenarios.Result(CaseStatus.Passed, Scenarios.SearchCaseId, Scenarios.SearchKey)],
            [MissingReasonCode.ArtifactMissing, MissingReasonCode.MissingShard],
            expectedExecutionCount: 2),
        Scenarios.Policy(criticalCaseIds: [Scenarios.SearchCaseId]),
        Scenarios.NoExceptions);

    internal static string Fingerprint(EvaluationResult result) => string.Join(
        '|',
        result.Completeness,
        result.Outcome,
        result.Decision,
        result.CoreDecision,
        string.Join(',', result.Missing.Select(code => EvaluationCodes.Code(code))),
        string.Join(',', result.Reasons.Select(reason => EvaluationCodes.Code(reason))));
}
