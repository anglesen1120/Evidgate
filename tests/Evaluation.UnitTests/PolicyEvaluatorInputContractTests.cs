namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

public sealed class PolicyEvaluatorInputContractTests
{
    [Theory]
    [InlineData(CaseStatus.Failed)]
    [InlineData(CaseStatus.Skipped)]
    [InlineData(CaseStatus.Blocked)]
    public void Evaluate_CriticalCaseOutsideManifestRequiredFlags_NeverPasses(CaseStatus status)
    {
        var policy = Scenarios.Policy(requiredCaseKeys: [], criticalCaseIds: [Scenarios.CheckoutCaseId]);
        var evaluator = new PolicyEvaluator();
        var passing = Scenarios.Evidence([Scenarios.Result(CaseStatus.Passed, required: false)]);

        evaluator.Evaluate(passing, policy, Scenarios.NoExceptions).Decision.ShouldBe(EvaluationDecision.Pass);

        var evidence = Scenarios.Evidence([Scenarios.Result(status, required: false)]);
        var result = evaluator.Evaluate(evidence, policy, Scenarios.NoExceptions);

        result.Decision.ShouldBe(status == CaseStatus.Failed
            ? EvaluationDecision.Fail
            : EvaluationDecision.Inconclusive);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Evaluate_UndefinedRequiredCaseStatus_NeverPasses(int status)
    {
        var evaluator = new PolicyEvaluator();
        evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions)
            .Decision.ShouldBe(EvaluationDecision.Pass);

        var evidence = Scenarios.Evidence([Scenarios.Result((CaseStatus)status)]);
        var result = evaluator.Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldNotBe(EvaluationDecision.Pass);
    }

    [Fact]
    public void Evaluate_AllMissingCodesUnsortedWithDuplicates_SortsMissingAndReasonsByToken()
    {
        var codes = Enum.GetValues<MissingReasonCode>().Reverse().ToArray();
        var duplicateCodes = codes.Concat(codes).ToArray();
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(missing: duplicateCodes), Scenarios.Policy(), Scenarios.NoExceptions);

        var missingTokens = result.Missing.Select(EvaluationCodes.Code).ToArray();
        var reasonTokens = result.Reasons.Select(EvaluationCodes.Code).ToArray();
        reasonTokens.ShouldBe(reasonTokens.Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray());
        missingTokens.ShouldBe(missingTokens.Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Evaluate_RequiredCaseAbsentFromDeclaredEmptySuite_NeverPasses()
    {
        var evidence = Scenarios.Evidence([], [], 0, suiteDeclaredEmpty: true);
        var evaluator = new PolicyEvaluator();
        var emptyPolicy = Scenarios.Policy(requiredCaseKeys: [], emptySuiteAllowed: true);
        evaluator.Evaluate(evidence, emptyPolicy, Scenarios.NoExceptions).Decision.ShouldBe(EvaluationDecision.Pass);

        var requiredPolicy = Scenarios.Policy(requiredCaseKeys: [Scenarios.CheckoutKey], emptySuiteAllowed: true);
        var result = evaluator.Evaluate(evidence, requiredPolicy, Scenarios.NoExceptions);

        result.Decision.ShouldNotBe(EvaluationDecision.Pass);
    }

    [Fact]
    public void Evaluate_LayerClearingItsReasonView_PreservesCoreFailureReason()
    {
        var evidence = Scenarios.Evidence([Scenarios.Result(CaseStatus.Failed)]);
        new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions)
            .Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed]);

        var result = new PolicyEvaluator([new ReasonClearingLayer()])
            .Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed]);
    }

    [Fact]
    public void Evaluate_InputBackingListChanges_DoNotChangeFrozenEvidence()
    {
        List<CaseResultInput> results = [Scenarios.Result(CaseStatus.Failed)];
        var evidence = Scenarios.Evidence(results);
        var evaluator = new PolicyEvaluator();
        var before = evaluator.Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions);
        before.Decision.ShouldBe(EvaluationDecision.Fail);

        results[0] = Scenarios.Result(CaseStatus.Passed);
        var after = evaluator.Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions);

        after.Decision.ShouldBe(before.Decision);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Evaluate_NegativeExpectedCountWithNoResults_NeverPasses(int expectedCount)
    {
        var evaluator = new PolicyEvaluator();
        var policy = Scenarios.Policy(requiredCaseKeys: []);
        evaluator.Evaluate(Scenarios.Evidence([], [], 0), policy, Scenarios.NoExceptions)
            .Decision.ShouldBe(EvaluationDecision.Inconclusive);

        var result = evaluator.Evaluate(
            Scenarios.Evidence([], [], expectedCount), policy, Scenarios.NoExceptions);

        result.Decision.ShouldNotBe(EvaluationDecision.Pass);
    }

    [Fact]
    public void Evaluate_LayerProposingUndefinedDecision_PreservesClosedDecisionVocabulary()
    {
        new PolicyEvaluator().Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions)
            .Decision.ShouldBe(EvaluationDecision.Pass);

        var result = new PolicyEvaluator([new UndefinedDecisionLayer()])
            .Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        Enum.IsDefined(result.Decision).ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_EveryNonemptyMissingSubset_PreservesFailureAndNeverPasses()
    {
        var evaluator = new PolicyEvaluator();
        var codes = Enum.GetValues<MissingReasonCode>();
        foreach (var mask in Enumerable.Range(1, (1 << codes.Length) - 1))
        {
            var missing = codes.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            var passing = evaluator.Evaluate(Scenarios.Evidence(missing: missing), Scenarios.Policy(), Scenarios.NoExceptions);
            passing.Decision.ShouldBe(EvaluationDecision.Inconclusive);
            passing.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);

            foreach (var status in new[] { CaseStatus.Failed, CaseStatus.TimedOut, CaseStatus.Error })
            {
                var failing = evaluator.Evaluate(
                    Scenarios.Evidence([Scenarios.Result(status)], missing), Scenarios.Policy(), Scenarios.NoExceptions);
                failing.Decision.ShouldBe(EvaluationDecision.Fail);
                failing.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
                failing.Outcome.ShouldBe(EvaluationOutcome.HasFailures);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Evaluate_DeclaredEmptySuiteWithEveryMissingCode_NeverPasses(bool declaredEmpty, bool allowed)
    {
        foreach (var missing in Enum.GetValues<MissingReasonCode>())
        {
            var result = new PolicyEvaluator().Evaluate(
                Scenarios.Evidence([], [missing], 0, declaredEmpty),
                Scenarios.Policy(requiredCaseKeys: [], emptySuiteAllowed: allowed),
                Scenarios.NoExceptions);

            result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
            result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        }
    }

    [Fact]
    public void Evaluate_DuplicateUnsortedResultsAndPolicyItems_KeepTheSameVerdict()
    {
        var failed = Scenarios.Result(CaseStatus.Failed);
        var passed = Scenarios.Result(CaseStatus.Passed, Scenarios.SearchCaseId, Scenarios.SearchKey);
        var first = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([failed, passed], [MissingReasonCode.MissingShard], 2),
            Scenarios.Policy(requiredCaseKeys: [Scenarios.CheckoutKey, Scenarios.SearchKey]),
            Scenarios.NoExceptions);
        var second = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([passed, failed, failed, passed], [MissingReasonCode.MissingShard, MissingReasonCode.MissingShard], 2),
            Scenarios.Policy(requiredCaseKeys: [Scenarios.SearchKey, Scenarios.CheckoutKey, Scenarios.SearchKey]),
            Scenarios.NoExceptions);

        PolicyEvaluatorTests.Fingerprint(second).ShouldBe(PolicyEvaluatorTests.Fingerprint(first));
        second.Decision.ShouldBe(EvaluationDecision.Fail);
    }

    [Theory]
    [InlineData("Results")]
    [InlineData("Missing")]
    public void Evaluate_NullEvidenceCollection_DoesNotReturnPass(string member)
    {
        var evidence = Scenarios.Evidence();
        var property = typeof(EvidenceRevisionInput).GetProperty(member);
        property.ShouldNotBeNull();
        property.SetValue(evidence, null);

        Should.Throw<Exception>(() => new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions));
    }

    [Theory]
    [InlineData("RequiredCaseKeys")]
    [InlineData("RequiredSuiteKeys")]
    [InlineData("CriticalCaseIds")]
    [InlineData("RequiredArtifactKinds")]
    public void Evaluate_NullRequiredPolicyCollection_DoesNotReturnPass(string member)
    {
        var policy = Scenarios.Policy();
        var property = typeof(PolicyRevisionInput).GetProperty(member);
        property.ShouldNotBeNull();
        property.SetValue(policy, null);

        Should.Throw<Exception>(() => new PolicyEvaluator().Evaluate(Scenarios.Evidence(), policy, Scenarios.NoExceptions));
    }

    private sealed class UndefinedDecisionLayer : IDecisionLayer
    {
        public int Order => 0;

        public DecisionLayerResult Apply(DecisionLayerContext context) =>
            new([], (EvaluationDecision)7);
    }

    private sealed class ReasonClearingLayer : IDecisionLayer
    {
        public int Order => 0;

        public DecisionLayerResult Apply(DecisionLayerContext context)
        {
            if (context.Reasons is IList<EvaluationReason> reasons)
            {
                reasons.Clear();
            }

            return new DecisionLayerResult([]);
        }
    }
}
