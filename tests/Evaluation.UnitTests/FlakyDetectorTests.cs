namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

public sealed class FlakyDetectorTests
{
    private static readonly FlakyDetector Detector = new();

    [Fact]
    public void Detect_FailThenPass_ReturnsFact()
    {
        var facts = Detector.Detect(Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed), null);

        facts.ShouldBe([new FlakyFact(Scenarios.CheckoutKey, Scenarios.CheckoutCaseId, 1, true)]);
    }

    [Fact]
    public void Detect_TimedOutThenPass_ReturnsFact()
    {
        var facts = Detector.Detect(Scenarios.Attempts(CaseStatus.TimedOut, CaseStatus.Passed), null);

        facts.ShouldBe([new FlakyFact(Scenarios.CheckoutKey, Scenarios.CheckoutCaseId, 1, true)]);
    }

    [Fact]
    public void Detect_ErrorThenPass_ReturnsFact()
    {
        var facts = Detector.Detect(Scenarios.Attempts(CaseStatus.Error, CaseStatus.Passed), null);

        facts.ShouldBe([new FlakyFact(Scenarios.CheckoutKey, Scenarios.CheckoutCaseId, 1, true)]);
    }

    [Fact]
    public void Detect_FailFailPass_CountsEveryRetry()
    {
        var facts = Detector.Detect(Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Failed, CaseStatus.Passed), null);

        facts.ShouldBe([new FlakyFact(Scenarios.CheckoutKey, Scenarios.CheckoutCaseId, 2, true)]);
    }

    [Fact]
    public void Detect_FailSkipPass_StillReturnsFact()
    {
        var facts = Detector.Detect(Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Skipped, CaseStatus.Passed), null);

        facts.Count.ShouldBe(1);
    }

    [Fact]
    public void Detect_SinglePass_NoFact() =>
        Detector.Detect(Scenarios.Attempts(CaseStatus.Passed), null).ShouldBeEmpty();

    [Fact]
    public void Detect_NoAttempts_NoFact() => Detector.Detect([], null).ShouldBeEmpty();

    [Theory]
    [InlineData(CaseStatus.Failed)]
    [InlineData(CaseStatus.TimedOut)]
    [InlineData(CaseStatus.Error)]
    public void Detect_AlwaysFails_NoRetryPassFact(CaseStatus failing) =>
        Detector.Detect(Scenarios.Attempts(failing, failing), null).ShouldBeEmpty();

    [Fact]
    public void Detect_SetupBlockedThenPass_NoFact() =>
        Detector.Detect(Scenarios.Attempts(CaseStatus.Blocked, CaseStatus.Passed), null).ShouldBeEmpty();

    [Fact]
    public void Detect_SkippedThenPass_NoFact() =>
        Detector.Detect(Scenarios.Attempts(CaseStatus.Skipped, CaseStatus.Passed), null).ShouldBeEmpty();

    [Fact]
    public void Detect_PassThenFail_NoFact() =>
        Detector.Detect(Scenarios.Attempts(CaseStatus.Passed, CaseStatus.Failed), null).ShouldBeEmpty();

    [Theory]
    [InlineData(CaseStatus.Blocked)]
    [InlineData(CaseStatus.Skipped)]
    public void Detect_FailThenUnexecutedLast_NoFact(CaseStatus last) =>
        Detector.Detect(Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed, last), null).ShouldBeEmpty();

    [Fact]
    public void Detect_PassAfterRetryLimit_IsNotConsidered()
    {
        var attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed);

        Detector.Detect(attempts, 0).ShouldBeEmpty();
        Detector.Detect(attempts, 1).Count.ShouldBe(1);
    }

    [Fact]
    public void Detect_UnboundedRetries_ConsidersEveryAttempt()
    {
        var attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Failed, CaseStatus.Failed, CaseStatus.Passed);

        Detector.Detect(attempts, null).ShouldBe([new FlakyFact(Scenarios.CheckoutKey, Scenarios.CheckoutCaseId, 3, true)]);
        Detector.Detect(attempts, 2).ShouldBeEmpty();
        Detector.Detect(attempts, 3).Count.ShouldBe(1);
        Detector.Detect(attempts, int.MaxValue).Count.ShouldBe(1);
    }

    [Fact]
    public void Detect_RetryLimitDropsLateAttempts_BeforeCountingRetries()
    {
        var attempts = Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed, CaseStatus.Failed);

        Detector.Detect(attempts, 1).ShouldBe([new FlakyFact(Scenarios.CheckoutKey, Scenarios.CheckoutCaseId, 1, true)]);
        Detector.Detect(attempts, null).ShouldBeEmpty();
    }

    [Fact]
    public void Detect_UnorderedInput_UsesAttemptNumberNotPosition()
    {
        CaseAttemptInput[] attempts = [.. Enumerable.Reverse(Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed))];

        Detector.Detect(attempts, null).Count.ShouldBe(1);
    }

    [Fact]
    public void Detect_ManyKeys_OrdersByOrdinalExecutionKeyWhateverTheInputOrder()
    {
        var upper = Guid.Parse("0190f0a0-0000-7000-8000-0000000000aa");
        var lower = Guid.Parse("0190f0a0-0000-7000-8000-0000000000bb");
        CaseAttemptInput[] ordered =
        [
            .. Scenarios.Attempts("Zeta", upper, CaseStatus.Failed, CaseStatus.Passed),
            .. Scenarios.Attempts("alpha", lower, CaseStatus.Failed, CaseStatus.Passed),
        ];
        FlakyFact[] expected = [new("Zeta", upper, 1, true), new("alpha", lower, 1, true)];

        Detector.Detect(ordered, null).ShouldBe(expected);
        Detector.Detect([.. Enumerable.Reverse(ordered)], null).ShouldBe(expected);
        Detector.Detect([ordered[1], ordered[3], ordered[0], ordered[2]], null).ShouldBe(expected);
    }

    [Fact]
    public void Detect_OneRecoveringKeyAmongStableOnes_ReturnsOnlyThatKey()
    {
        CaseAttemptInput[] attempts =
        [
            .. Scenarios.Attempts(Scenarios.SearchKey, Scenarios.SearchCaseId, CaseStatus.Passed),
            .. Scenarios.Attempts(CaseStatus.Failed, CaseStatus.Passed),
        ];

        Detector.Detect(attempts, null).Select(fact => fact.ExecutionKey).ShouldBe([Scenarios.CheckoutKey]);
    }
}
