namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

/// <summary>The composition seam: a layer may annotate and downgrade a pass, never upgrade or overturn a failure.</summary>
public sealed class DecisionLayerTests
{
    [Fact]
    public void Evaluate_LayerProposingPassOverFail_IsIgnored()
    {
        var evaluator = new PolicyEvaluator([new ScriptedLayer(1, new DecisionLayerResult([], EvaluationDecision.Pass))]);

        var result = evaluator.Evaluate(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Failed)], [MissingReasonCode.MissingShard]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.CoreDecision.ShouldBe(EvaluationDecision.Fail);
        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed]);
    }

    [Fact]
    public void Evaluate_LayerDowngradingPass_AppliesAndKeepsTheCoreDecision()
    {
        var evaluator = new PolicyEvaluator([
            new ScriptedLayer(1, new DecisionLayerResult([EvaluationReason.FlakyBudgetExceeded], EvaluationDecision.Inconclusive)),
        ]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.CoreDecision.ShouldBe(EvaluationDecision.Pass);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["flaky_budget_exceeded", "scope_build_policy_pinned"]);
    }

    [Fact]
    public void Evaluate_LayerAddingOnlyReasons_KeepsThePass()
    {
        var evaluator = new PolicyEvaluator([
            new ScriptedLayer(1, new DecisionLayerResult([EvaluationReason.FlakyBudgetExceeded])),
        ]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["flaky_budget_exceeded", "scope_build_policy_pinned"]);
    }

    [Fact]
    public void Evaluate_LayersRunInOrderAndSeeTheEarlierDowngrade()
    {
        var late = new RecordingLayer(2, new DecisionLayerResult([]));
        var early = new RecordingLayer(1, new DecisionLayerResult([], EvaluationDecision.Inconclusive));

        var result = new PolicyEvaluator([late, early])
            .Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        early.Calls.Count.ShouldBe(1);
        late.Calls.Count.ShouldBe(1);
        early.Calls[0].Decision.ShouldBe(EvaluationDecision.Pass);
        late.Calls[0].Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
    }

    [Fact]
    public void Evaluate_NoLayers_MatchesTheCore()
    {
        var result = new PolicyEvaluator([]).Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(result.CoreDecision);
    }

    private sealed class ScriptedLayer(int order, DecisionLayerResult contribution) : IDecisionLayer
    {
        public int Order => order;

        public DecisionLayerResult Apply(DecisionLayerContext context) => contribution;
    }

    private sealed class RecordingLayer(int order, DecisionLayerResult contribution) : IDecisionLayer
    {
        public int Order => order;

        public List<DecisionLayerContext> Calls { get; } = [];

        public DecisionLayerResult Apply(DecisionLayerContext context)
        {
            Calls.Add(context);
            return contribution;
        }
    }
}
