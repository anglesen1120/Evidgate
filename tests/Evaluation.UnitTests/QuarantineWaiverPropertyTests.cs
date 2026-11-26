namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

public sealed class QuarantineWaiverPropertyTests
{
    private const int Seed = 20261126;
    private const int Trials = 2000;

    [Fact]
    public void Evaluate_GeneratedExceptionSets_NeverWaiveAGatingFailure()
    {
        var random = new Random(Seed);
        var evaluator = new PolicyEvaluator();
        var statuses = Enum.GetValues<CaseStatus>();
        Guid[] cases = [Scenarios.CheckoutCaseId, Scenarios.SearchCaseId];
        var passedWithQuarantine = 0;
        var failedWithWaiver = 0;

        for (var trial = 0; trial < Trials; trial++)
        {
            var results = new[]
            {
                Scenarios.Result(statuses[random.Next(statuses.Length)], required: random.Next(2) == 0),
                Scenarios.Result(statuses[random.Next(statuses.Length)], Scenarios.SearchCaseId, Scenarios.SearchKey,
                    required: random.Next(2) == 0),
            };
            var critical = Subset(cases, random);
            var quarantine = Subset(cases, random);
            var policy = Scenarios.Policy(requiredCaseKeys: [], criticalCaseIds: critical);
            var evidence = Scenarios.Evidence(results, expectedExecutionCount: results.Length);
            var snapshot = Scenarios.NoExceptions with { QuarantinedCaseIds = quarantine };
            var withoutWaivers = evaluator.Evaluate(evidence, policy, snapshot);
            var withWaivers = evaluator.Evaluate(evidence, policy, snapshot with { WaivedCaseIds = Subset(cases, random) });
            var context = $"seed {Seed}, trial {trial + 1}/{Trials}";

            withWaivers.Decision.ShouldBe(withoutWaivers.Decision, context);
            withWaivers.CoreDecision.ShouldBe(withoutWaivers.CoreDecision, context);
            withWaivers.Outcome.ShouldBe(withoutWaivers.Outcome, context);
            passedWithQuarantine += withWaivers.Decision == EvaluationDecision.Pass && withWaivers.QuarantinedCases.Count > 0 ? 1 : 0;
            failedWithWaiver += withWaivers.Reasons.Contains(EvaluationReason.WaiverApproved) ? 1 : 0;

            foreach (var failure in results.Where(r => r.Status is CaseStatus.Failed or CaseStatus.TimedOut or CaseStatus.Error))
            {
                var isCritical = critical.Contains(failure.CaseId);
                if (isCritical)
                {
                    withWaivers.Decision.ShouldBe(EvaluationDecision.Fail, context);
                }

                if (withWaivers.Decision == EvaluationDecision.Pass && (isCritical || failure.Required))
                {
                    quarantine.ShouldContain(failure.CaseId, context);
                    isCritical.ShouldBeFalse(context);
                }
            }
        }

        passedWithQuarantine.ShouldBeGreaterThan(0, $"seed {Seed}: no generated case exercised a quarantined pass");
        failedWithWaiver.ShouldBeGreaterThan(0, $"seed {Seed}: no generated case exercised a waived failure");
    }

    private static Guid[] Subset(Guid[] cases, Random random) => [.. cases.Where(_ => random.Next(2) == 0)];
}
