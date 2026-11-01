namespace Evidgate.Evaluation;

/// <summary>
/// The default pure evaluator. Its only collaborators are decision layers, applied in a fixed order after the core
/// rules; the default instance has none, so a caller gets exactly the contract's base computation.
/// </summary>
public sealed class PolicyEvaluator : IPolicyEvaluator
{
    private readonly IReadOnlyList<IDecisionLayer> layers;

    /// <summary>Creates the evaluator with no post-core layers.</summary>
    public PolicyEvaluator()
        : this([])
    {
    }

    /// <summary>Creates the evaluator with post-core layers, ordered by <see cref="IDecisionLayer.Order"/>.</summary>
    public PolicyEvaluator(IReadOnlyList<IDecisionLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var ordered = new List<IDecisionLayer>(layers.Count);
        foreach (var layer in layers)
        {
            ArgumentNullException.ThrowIfNull(layer, nameof(layers));
            ordered.Add(layer);
        }

        this.layers = [.. ordered
            .OrderBy(layer => layer.Order)
            .ThenBy(layer => layer.GetType().FullName, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public EvaluationResult Evaluate(
        EvidenceRevisionInput evidence,
        PolicyRevisionInput policy,
        ExceptionsSnapshotInput exceptions)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(exceptions);

        var core = CoreEvaluation.Compute(evidence, policy);
        var (decision, reasons) = ApplyLayers(evidence, policy, exceptions, core);

        return core with { Decision = decision, Reasons = reasons };
    }

    /// <summary>
    /// Runs every layer in order. A layer may only downgrade the core pass: once the decision is not a pass it is
    /// frozen, and a layer that proposes a pass over anything else is ignored, so no layer can overturn a failure.
    /// </summary>
    private (EvaluationDecision Decision, IReadOnlyList<EvaluationReason> Reasons) ApplyLayers(
        EvidenceRevisionInput evidence,
        PolicyRevisionInput policy,
        ExceptionsSnapshotInput exceptions,
        EvaluationResult core)
    {
        var decision = core.Decision;
        var reasons = new List<EvaluationReason>(core.Reasons);

        foreach (var layer in layers)
        {
            var context = new DecisionLayerContext(
                evidence,
                policy,
                exceptions,
                core.Completeness,
                core.Missing,
                core.Outcome,
                core.CoreDecision,
                decision,
                reasons);

            var result = layer.Apply(context)
                ?? throw new InvalidOperationException($"decision layer {layer.GetType().FullName} returned no result");

            if (result.Decision is { } proposed
                && decision == EvaluationDecision.Pass
                && proposed != EvaluationDecision.Pass)
            {
                decision = proposed;
            }

            reasons.AddRange(result.Reasons
                ?? throw new InvalidOperationException($"decision layer {layer.GetType().FullName} returned no reasons"));
        }

        return (decision, ReasonSet.Normalize(reasons));
    }
}
