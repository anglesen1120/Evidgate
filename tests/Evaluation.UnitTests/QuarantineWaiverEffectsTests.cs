namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

public sealed class QuarantineWaiverEffectsTests
{
    [Theory]
    [InlineData(CaseStatus.Failed)]
    [InlineData(CaseStatus.Error)]
    [InlineData(CaseStatus.TimedOut)]
    public void M14_QuarantinedFailure_PassesWithoutLosingTheOutcome(CaseStatus status)
    {
        var evidence = Scenarios.Evidence([Scenarios.Result(status)]);
        var result = new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions with
        {
            QuarantinedCaseIds = [Scenarios.CheckoutCaseId],
        });

        result.Completeness.ShouldBe(EvaluationCompleteness.Complete);
        result.Outcome.ShouldBe(EvaluationOutcome.HasFailures);
        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.CoreDecision.ShouldBe(EvaluationDecision.Pass);
        result.QuarantinedCases.ShouldBe([Scenarios.CheckoutCaseId]);
        result.Reasons.ShouldBe([EvaluationReason.QuarantinedCaseFailed, EvaluationReason.ScopeBuildPolicyPinned]);
    }

    [Theory]
    [InlineData(CaseStatus.Failed)]
    [InlineData(CaseStatus.Error)]
    [InlineData(CaseStatus.TimedOut)]
    public void M15_WaivedRequiredFailure_StillFails(CaseStatus status)
    {
        var evidence = Scenarios.Evidence([Scenarios.Result(status)]);
        var result = new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions with
        {
            WaivedCaseIds = [Scenarios.CheckoutCaseId, Scenarios.CheckoutCaseId],
        });

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.CoreDecision.ShouldBe(EvaluationDecision.Fail);
        result.Outcome.ShouldBe(EvaluationOutcome.HasFailures);
        result.QuarantinedCases.ShouldBeEmpty();
        result.Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed, EvaluationReason.WaiverApproved]);
    }

    [Theory]
    [InlineData(CaseStatus.Failed)]
    [InlineData(CaseStatus.Error)]
    [InlineData(CaseStatus.TimedOut)]
    public void M18_CriticalFailure_IgnoresQuarantine(CaseStatus status)
    {
        var policy = Scenarios.Policy(requiredCaseKeys: [], criticalCaseIds: [Scenarios.CheckoutCaseId]);
        var evidence = Scenarios.Evidence([Scenarios.Result(status, required: false)]);
        var result = new PolicyEvaluator().Evaluate(evidence, policy, Scenarios.NoExceptions with
        {
            QuarantinedCaseIds = [Scenarios.CheckoutCaseId],
        });

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.QuarantinedCases.ShouldBeEmpty();
        result.Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed]);
    }

    [Theory]
    [InlineData(CaseStatus.Skipped)]
    [InlineData(CaseStatus.Blocked)]
    public void Quarantine_UnexecutedCase_RemainsInconclusive(CaseStatus status)
    {
        var evidence = Scenarios.Evidence([Scenarios.Result(status)]);
        var result = new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions with
        {
            QuarantinedCaseIds = [Scenarios.CheckoutCaseId],
            WaivedCaseIds = [Scenarios.CheckoutCaseId],
        });

        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.QuarantinedCases.ShouldBeEmpty();
        result.Reasons.ShouldBe([EvaluationReason.CriticalNotExecuted]);
    }

    [Fact]
    public void Quarantine_MissingExecution_RemainsInconclusive()
    {
        var evidence = Scenarios.Evidence([Scenarios.Result(CaseStatus.Failed)], expectedExecutionCount: 2);
        var result = new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions with
        {
            QuarantinedCaseIds = [Scenarios.CheckoutCaseId, Scenarios.SearchCaseId],
        });

        result.Completeness.ShouldBe(EvaluationCompleteness.Incomplete);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Missing.ShouldBe([MissingReasonCode.MissingResult]);
        result.QuarantinedCases.ShouldBe([Scenarios.CheckoutCaseId]);
        result.Reasons.ShouldBe([EvaluationReason.MissingResult, EvaluationReason.QuarantinedCaseFailed]);
    }

    [Fact]
    public void Quarantine_OnlyExcludedFailureWaived_DoesNotAnnotateUnrelatedFailure()
    {
        var evidence = Scenarios.Evidence([
            Scenarios.Result(CaseStatus.Error),
            Scenarios.Result(CaseStatus.Failed, Scenarios.SearchCaseId, Scenarios.SearchKey),
        ], expectedExecutionCount: 2);
        var result = new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(requiredCaseKeys: []), Scenarios.NoExceptions with
        {
            QuarantinedCaseIds = [Scenarios.CheckoutCaseId],
            WaivedCaseIds = [Scenarios.CheckoutCaseId],
        });

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.QuarantinedCases.ShouldBe([Scenarios.CheckoutCaseId]);
        result.Reasons.ShouldBe([EvaluationReason.QuarantinedCaseFailed, EvaluationReason.RequiredAssertionFailed]);
    }

    [Fact]
    public void Waiver_WithoutRequiredFailure_DoesNotAnnotatePass()
    {
        var evidence = Scenarios.Evidence([
            Scenarios.Result(CaseStatus.Passed),
            Scenarios.Result(CaseStatus.Failed, Scenarios.SearchCaseId, Scenarios.SearchKey),
        ], expectedExecutionCount: 2);
        var result = new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(), Scenarios.NoExceptions with
        {
            WaivedCaseIds = [Scenarios.CheckoutCaseId, Scenarios.SearchCaseId],
        });

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
    }

    [Fact]
    public void Quarantine_RepeatedUnsortedFailures_ReportsEachCaseOnceInOrder()
    {
        var search = Scenarios.Result(CaseStatus.Failed, Scenarios.SearchCaseId, Scenarios.SearchKey);
        var evidence = Scenarios.Evidence([search, Scenarios.Result(CaseStatus.TimedOut), search], expectedExecutionCount: 3);
        var cases = new List<Guid> { Scenarios.SearchCaseId, Scenarios.CheckoutCaseId, Scenarios.SearchCaseId };
        var snapshot = new ExceptionsSnapshotInput(DateTimeOffset.UnixEpoch, [], [], cases, cases);
        cases.Clear();

        var result = new PolicyEvaluator().Evaluate(evidence, Scenarios.Policy(requiredCaseKeys: []), snapshot);

        snapshot.WaivedCaseIds.ShouldBe([Scenarios.SearchCaseId, Scenarios.CheckoutCaseId, Scenarios.SearchCaseId]);
        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.QuarantinedCases.ShouldBe([Scenarios.CheckoutCaseId, Scenarios.SearchCaseId]);
        result.Reasons.ShouldBe([EvaluationReason.QuarantinedCaseFailed, EvaluationReason.ScopeBuildPolicyPinned]);
    }
}
