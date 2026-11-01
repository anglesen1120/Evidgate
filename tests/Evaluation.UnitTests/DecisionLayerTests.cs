namespace Evaluation.UnitTests;

using Evidgate.Evaluation;
using Shouldly;
using Xunit;

/// <summary>
/// The composition seam: a layer may annotate a result and downgrade a pass to an inconclusive verdict, never raise a
/// decision, never invent a failure and never reach the evaluator's own state.
/// </summary>
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
    public void Evaluate_LayerDowngradingPass_DropsThePassOnlyReason()
    {
        var evaluator = new PolicyEvaluator([
            new ScriptedLayer(1, new DecisionLayerResult([EvaluationReason.FlakyBudgetExceeded], EvaluationDecision.Inconclusive)),
        ]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.CoreDecision.ShouldBe(EvaluationDecision.Pass);
        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["flaky_budget_exceeded"]);
    }

    [Fact]
    public void Evaluate_LayerProposingFailOverPass_IsIgnored()
    {
        var evaluator = new PolicyEvaluator([new ScriptedLayer(1, new DecisionLayerResult([], EvaluationDecision.Fail))]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
    }

    [Fact]
    public void Evaluate_LayerProposingPassOverPass_KeepsThePass()
    {
        var evaluator = new PolicyEvaluator([new ScriptedLayer(1, new DecisionLayerResult([], EvaluationDecision.Pass))]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
    }

    [Fact]
    public void Evaluate_LayerProposingUndefinedDecision_IsIgnored()
    {
        var evaluator = new PolicyEvaluator([new ScriptedLayer(1, new DecisionLayerResult([], (EvaluationDecision)7))]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Pass);
        result.Reasons.ShouldBe([EvaluationReason.ScopeBuildPolicyPinned]);
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
        result.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_DowngradeIsNotRaisedByALaterLayer()
    {
        var evaluator = new PolicyEvaluator([
            new ScriptedLayer(1, new DecisionLayerResult([], EvaluationDecision.Inconclusive)),
            new ScriptedLayer(2, new DecisionLayerResult([], EvaluationDecision.Pass)),
        ]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
    }

    [Fact]
    public void Evaluate_DowngradeContributingThePassOnlyReason_LeavesNone()
    {
        var evaluator = new PolicyEvaluator([
            new ScriptedLayer(
                1,
                new DecisionLayerResult([EvaluationReason.ScopeBuildPolicyPinned], EvaluationDecision.Inconclusive)),
        ]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_LaterLayerReAddingThePassOnlyReason_LeavesNone()
    {
        var evaluator = new PolicyEvaluator([
            new ScriptedLayer(1, new DecisionLayerResult([EvaluationReason.FlakyBudgetExceeded], EvaluationDecision.Inconclusive)),
            new ScriptedLayer(2, new DecisionLayerResult([EvaluationReason.ScopeBuildPolicyPinned])),
        ]);

        var result = evaluator.Evaluate(Scenarios.Evidence(), Scenarios.Policy(), Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Inconclusive);
        result.Reasons.Select(EvaluationCodes.Code).ShouldBe(["flaky_budget_exceeded"]);
    }

    [Fact]
    public void Evaluate_LayerContributingThePassOnlyReasonOverAFailure_LeavesNone()
    {
        var evaluator = new PolicyEvaluator([
            new ScriptedLayer(1, new DecisionLayerResult([EvaluationReason.ScopeBuildPolicyPinned])),
        ]);

        var result = evaluator.Evaluate(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Failed)]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed]);
    }

    [Fact]
    public void Evaluate_DuplicateLayerOrder_ThrowsAtConstruction()
    {
        var layers = new IDecisionLayer[]
        {
            new ScriptedLayer(1, new DecisionLayerResult([])),
            new ScriptedLayer(1, new DecisionLayerResult([])),
        };

        Should.Throw<ArgumentException>(() => new PolicyEvaluator(layers));
    }

    [Fact]
    public void Evaluate_LayerMutatingItsReasonView_CannotReachEvaluatorState()
    {
        var mutating = new MutatingLayer();

        var result = new PolicyEvaluator([mutating]).Evaluate(
            Scenarios.Evidence([Scenarios.Result(CaseStatus.Failed)]),
            Scenarios.Policy(),
            Scenarios.NoExceptions);

        mutating.Castable.ShouldBeFalse();
        result.Decision.ShouldBe(EvaluationDecision.Fail);
        result.Reasons.ShouldBe([EvaluationReason.RequiredAssertionFailed]);
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

    private sealed class MutatingLayer : IDecisionLayer
    {
        public int Order => 1;

        public bool Castable { get; private set; }

        public DecisionLayerResult Apply(DecisionLayerContext context)
        {
            if (context.Reasons is IList<EvaluationReason> reasons)
            {
                Castable = true;
                reasons.Clear();
            }

            return new DecisionLayerResult([]);
        }
    }
}
