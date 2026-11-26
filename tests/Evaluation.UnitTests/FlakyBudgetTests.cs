namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

public sealed class FlakyBudgetTests
{
    [Theory]
    [InlineData(0, 1, EvaluationDecision.Inconclusive, EvaluationReason.FlakyBudgetExceeded)]
    [InlineData(1, 1, EvaluationDecision.Pass, EvaluationReason.FlakyWithinBudget)]
    [InlineData(1, 2, EvaluationDecision.Inconclusive, EvaluationReason.FlakyBudgetExceeded)]
    [InlineData(2, 2, EvaluationDecision.Pass, EvaluationReason.FlakyWithinBudget)]
    [InlineData(2, 3, EvaluationDecision.Inconclusive, EvaluationReason.FlakyBudgetExceeded)]
    [InlineData(3, 3, EvaluationDecision.Pass, EvaluationReason.FlakyWithinBudget)]
    [InlineData(3, 4, EvaluationDecision.Inconclusive, EvaluationReason.FlakyBudgetExceeded)]
    public void Evaluate_BudgetBoundary_DecidesWithinOrExceeded(
        int budget,
        int flakyCount,
        EvaluationDecision decision,
        EvaluationReason reason)
    {
        var result = Run(budget, flakyCount);

        result.Decision.ShouldBe(decision);
        result.CoreDecision.ShouldBe(EvaluationDecision.Pass);
        result.FlakyFacts.Count.ShouldBe(flakyCount);
        EvaluationReason[] expected = decision == EvaluationDecision.Pass
            ? [reason, EvaluationReason.ScopeBuildPolicyPinned]
            : [reason];
        result.Reasons.ShouldBe(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Evaluate_OneMoreFlakyCaseThanTheBudget_NeverPasses(int budget)
    {
        var within = Run(budget, budget);
        var over = Run(budget, budget + 1);

        over.Decision.ShouldNotBe(EvaluationDecision.Pass);
        over.Reasons.ShouldContain(EvaluationReason.FlakyBudgetExceeded);
        over.Reasons.ShouldNotContain(EvaluationReason.ScopeBuildPolicyPinned);
        within.Decision.ShouldBe(EvaluationDecision.Pass);
    }

    [Fact]
    public void Evaluate_CriticalFlakyCaseOnIncompleteEvidence_StillFails()
    {
        var policy = Scenarios.Policy(criticalCaseIds: [Scenarios.CheckoutCaseId]);
        var evidence = Scenarios.Evidence(missing: [MissingReasonCode.MissingShard])
            with
        { Attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed) };

        var result = new PolicyEvaluator().Evaluate(evidence, policy, Scenarios.NoExceptions);

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.CoreDecision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.FlakyCriticalRetryPass, EvaluationReason.MissingShard]);
    }

    [Fact]
    public void Evaluate_CriticalAndNonCriticalFlaky_FailsAndKeepsBothReasons()
    {
        var policy = Scenarios.Policy(
            requiredCaseKeys: [Scenarios.CheckoutKey, Scenarios.SearchKey],
            criticalCaseIds: [Scenarios.CheckoutCaseId]);
        var evidence = Scenarios.Evidence(
            [Scenarios.Result(CaseStatus.Passed), Scenarios.Result(CaseStatus.Passed, Scenarios.SearchCaseId, Scenarios.SearchKey)],
            expectedExecutionCount: 2) with
        {
            Attempts =
            [
                .. Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed),
                .. Scenarios.Attempts(Scenarios.SearchKey, Scenarios.SearchCaseId, CaseStatus.Failed, CaseStatus.Passed),
            ],
        };

        var result = new PolicyEvaluator().Evaluate(evidence, policy, Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.FlakyCriticalRetryPass, EvaluationReason.FlakyWithinBudget]);
    }

    [Fact]
    public void Evaluate_OverBudgetFlakyOnExistingFail_StaysFail()
    {
        var policy = Scenarios.Policy(requiredCaseKeys: [Scenarios.CheckoutKey, Scenarios.SearchKey], flakyBudget: 0);
        var evidence = Scenarios.Evidence(
            [Scenarios.Result(CaseStatus.Failed), Scenarios.Result(CaseStatus.Passed, Scenarios.SearchCaseId, Scenarios.SearchKey)],
            expectedExecutionCount: 2) with
        {
            Attempts = Scenarios.Attempts(Scenarios.SearchKey, Scenarios.SearchCaseId, CaseStatus.Failed, CaseStatus.Passed),
        };

        var result = new PolicyEvaluator().Evaluate(evidence, policy, Scenarios.NoExceptions);

        result.CoreDecision.ShouldBe(EvaluationDecision.Fail);
        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.FlakyBudgetExceeded, EvaluationReason.RequiredAssertionFailed]);
    }

    [Fact]
    public void Evaluate_OverBudgetFlakyOnInconclusive_StaysInconclusiveWithBothReasons()
    {
        var evidence = Scenarios.Evidence(missing: [MissingReasonCode.MissingShard])
            with
        { Attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed) };

        var result = new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(flakyBudget: 0), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.FlakyBudgetExceeded, EvaluationReason.MissingShard]);
    }

    [Fact]
    public void Evaluate_QuarantinedNonCriticalFlakyCase_IsExcludedFromTheBudget()
    {
        var quarantined = Scenarios.NoExceptions with { QuarantinedCaseIds = [Scenarios.CheckoutCaseId] };

        var evidence = Scenarios.Evidence() with { Attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed) };
        var policy = Scenarios.Policy(flakyBudget: 0);
        var excluded = new PolicyEvaluator().Evaluate(evidence, policy, quarantined);
        var counted = new PolicyEvaluator().Evaluate(evidence, policy, Scenarios.NoExceptions);

        excluded.Decision.ShouldBe(EvaluationDecision.Pass);
        excluded.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
        excluded.FlakyFacts.Count.ShouldBe(1);
        counted.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        counted.Reasons.ShouldBe([EvaluationReason.FlakyBudgetExceeded]);
    }

    [Fact]
    public void Evaluate_QuarantinedCriticalFlakyCase_StillFails()
    {
        var policy = Scenarios.Policy(criticalCaseIds: [Scenarios.CheckoutCaseId]);
        var evidence = Scenarios.Evidence() with { Attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed) };

        var result = new PolicyEvaluator().Evaluate(evidence, policy, Scenarios.NoExceptions with
        {
            QuarantinedCaseIds = [Scenarios.CheckoutCaseId],
        });

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.FlakyCriticalRetryPass]);
    }

    [Fact]
    public void Evaluate_WaivedCriticalFlakyCase_StaysFailAndIsAnnotated()
    {
        var policy = Scenarios.Policy(criticalCaseIds: [Scenarios.CheckoutCaseId]);
        var evidence = Scenarios.Evidence() with { Attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed) };

        var result = new PolicyEvaluator().Evaluate(evidence, policy, Scenarios.NoExceptions with
        {
            WaivedCaseIds = [Scenarios.CheckoutCaseId],
        });

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.FlakyCriticalRetryPass, EvaluationReason.WaiverApproved]);
    }

    [Fact]
    public void Evaluate_WaiverOnAnotherCase_DoesNotAnnotateACriticalFlakyFail()
    {
        var policy = Scenarios.Policy(criticalCaseIds: [Scenarios.CheckoutCaseId]);
        var evidence = Scenarios.Evidence() with { Attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed) };

        var result = new PolicyEvaluator().Evaluate(evidence, policy, Scenarios.NoExceptions with
        {
            WaivedCaseIds = [Guid.CreateVersion7()],
        });

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.FlakyCriticalRetryPass]);
    }

    [Fact]
    public void Evaluate_SinglePassAttempt_AddsNoFlakyReason()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence() with { Attempts = Scenarios.Attempts(CaseStatus.Passed) },
            Scenarios.Policy(flakyBudget: 0),
            Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
        result.FlakyFacts.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_NeverRecoveringAttempts_AreAPlainFailureNotFlaky()
    {
        var result = new PolicyEvaluator().Evaluate(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Failed)])
                with
            { Attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Failed) },
            Scenarios.Policy(criticalCaseIds: [Scenarios.CheckoutCaseId]),
            Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed]);
        result.FlakyFacts.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_RecoveryBeyondTheSuiteRetryLimit_IsNotFlaky()
    {
        var evidence = Scenarios.Evidence() with
        {
            Attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed),
            MaxRetries = 0,
        };

        var result = new PolicyEvaluator().Evaluate(
            evidence,
            Scenarios.Policy(criticalCaseIds: [Scenarios.CheckoutCaseId], flakyBudget: 0),
            Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.FlakyFacts.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_WithoutAttemptHistory_BehavesAsBefore()
    {
        var result = new PolicyEvaluator().Evaluate(Scenarios.Evidence(), Scenarios.Policy(flakyBudget: 0), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
        result.FlakyFacts.ShouldBeEmpty();
    }

    // Cases 1..max(flakyCount, 1) all pass; the first flakyCount of them passed only on a retry.
    private static EvaluationResult Run(int budget, int flakyCount)
    {
        var total = Math.Max(flakyCount, 1);
        var results = Enumerable.Range(1, total)
            .Select(index => Scenarios.Result(CaseStatus.Passed, CaseId(index), Key(index)))
            .ToArray();
        var attempts = Enumerable.Range(1, flakyCount)
            .SelectMany(index => Scenarios.Attempts(Key(index), CaseId(index), CaseStatus.Failed, CaseStatus.Passed))
            .ToArray();
        var policy = Scenarios.Policy(requiredCaseKeys: [.. results.Select(result => result.CaseKey)], flakyBudget: budget);

        return new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(results, expectedExecutionCount: total) with { Attempts = attempts },
            policy,
            Scenarios.NoExceptions);
    }

    private static Guid CaseId(int index) => Guid.Parse($"0190f0a0-0000-7000-8000-{index:D12}");

    private static string Key(int index) => $"case/{index}";
}
