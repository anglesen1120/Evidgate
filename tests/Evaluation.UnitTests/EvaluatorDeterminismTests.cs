namespace Evaluation.UnitTests;

using System.Text;
using Evidgate.Evaluation;
using Shouldly;
using Xunit;

public sealed class EvaluatorDeterminismTests
{
    [Fact]
    public void SameInputsTwice_ByteIdenticalFacts()
    {
        var ids = Enumerable.Range(1, 4).Select(index => Guid.Parse($"0190f0a0-0000-7000-8000-{index:D12}")).ToArray();
        string[] keys = ["Zeta", "alpha", "beta", "Gamma"];
        var results = ids.Select((id, index) => Scenarios.Result(CaseStatus.Passed, id, keys[index])).ToArray();
        CaseAttemptInput[] attempts =
        [
            .. Scenarios.Attempts(keys[0], ids[0], CaseStatus.Failed, CaseStatus.Passed),
            .. Scenarios.Attempts(keys[1], ids[1], CaseStatus.Error, CaseStatus.TimedOut, CaseStatus.Passed),
            .. Scenarios.Attempts(keys[2], ids[2], CaseStatus.Passed),
            .. Scenarios.Attempts(keys[3], ids[3], CaseStatus.Failed, CaseStatus.Passed),
        ];
        var policy = Scenarios.Policy(requiredCaseKeys: keys, criticalCaseIds: [ids[3]], flakyBudget: 1);

        var first = Render(Evaluate(results, attempts, policy));
        var again = Render(Evaluate(results, attempts, policy));
        var reversed = Render(Evaluate([.. Enumerable.Reverse(results)], [.. Enumerable.Reverse(attempts)], policy));
        var rotated = Render(Evaluate(
            [.. results.Skip(2), .. results.Take(2)],
            [.. attempts.Skip(3), .. attempts.Take(3)],
            policy));

        first.FactCount.ShouldBe(3);
        Encoding.UTF8.GetBytes(again.Text).ShouldBe(Encoding.UTF8.GetBytes(first.Text));
        Encoding.UTF8.GetBytes(reversed.Text).ShouldBe(Encoding.UTF8.GetBytes(first.Text));
        Encoding.UTF8.GetBytes(rotated.Text).ShouldBe(Encoding.UTF8.GetBytes(first.Text));
    }

    private static EvaluationResult Evaluate(
        CaseResultInput[] results,
        CaseAttemptInput[] attempts,
        PolicyRevisionInput policy) =>
        new PolicyEvaluator().Evaluate(
            Scenarios.Evidence(results, expectedExecutionCount: results.Length) with { Attempts = attempts },
            policy,
            Scenarios.NoExceptions);

    private static (string Text, int FactCount) Render(EvaluationResult result) => (
        string.Join(
            '|',
            result.FlakyFacts.Select(fact => $"{fact.ExecutionKey}:{fact.CaseId}:{fact.RetryCount}:{fact.HadRetryFailToPass}"))
        + "#" + string.Join(',', result.Reasons.Select(EvaluationCodes.Code))
        + "#" + EvaluationCodes.Code(result.Decision),
        result.FlakyFacts.Count);
}
