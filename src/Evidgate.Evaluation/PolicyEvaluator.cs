namespace Evidgate.Evaluation;

using System.Collections;

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
    /// <exception cref="ArgumentException">
    /// Two layers declare one order, so their sequence would depend on the caller's list order.
    /// </exception>
    public PolicyEvaluator(IReadOnlyList<IDecisionLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var ordered = new List<IDecisionLayer>(layers.Count);
        foreach (var layer in layers)
        {
            ArgumentNullException.ThrowIfNull(layer, nameof(layers));
            ordered.Add(layer);
        }

        ordered.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        for (var index = 1; index < ordered.Count; index++)
        {
            if (ordered[index - 1].Order == ordered[index].Order)
            {
                throw new ArgumentException(
                    $"decision layers {ordered[index - 1].GetType().FullName} and {ordered[index].GetType().FullName} " +
                    $"share order {ordered[index].Order}; a layer order must be total",
                    nameof(layers));
            }
        }

        this.layers = ordered;
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

        // The input records copy their collections at construction, so a re-evaluation sees the same evidence whatever
        // the caller does to its own lists. These checks refuse a record whose property was set to null afterwards,
        // instead of failing deeper inside the computation.
        ArgumentNullException.ThrowIfNull(evidence.Missing);
        ArgumentNullException.ThrowIfNull(evidence.Results);
        ArgumentNullException.ThrowIfNull(policy.RequiredCaseKeys);
        ArgumentNullException.ThrowIfNull(policy.RequiredSuiteKeys);
        ArgumentNullException.ThrowIfNull(policy.CriticalCaseIds);
        ArgumentNullException.ThrowIfNull(policy.AllowedSkipCaseKeys);
        ArgumentNullException.ThrowIfNull(policy.RequiredArtifactKinds);
        ArgumentNullException.ThrowIfNull(exceptions.WaiverIds);
        ArgumentNullException.ThrowIfNull(exceptions.QuarantineIds);

        var core = CoreEvaluation.Compute(evidence, policy);
        var (decision, reasons) = ApplyLayers(evidence, policy, exceptions, core);

        return core with { Decision = decision, Reasons = reasons };
    }

    /// <summary>
    /// Runs every layer in order. A layer may only downgrade the core pass to an inconclusive verdict: once the
    /// decision is not a pass it is frozen, a proposal to raise it is ignored, and so is a proposal outside the
    /// decision enum. A downgrade drops the pass-only reason, which no longer describes the result.
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
                new ReasonView(reasons));

            var result = layer.Apply(context)
                ?? throw new InvalidOperationException($"decision layer {layer.GetType().FullName} returned no result");

            reasons.AddRange(result.Reasons
                ?? throw new InvalidOperationException($"decision layer {layer.GetType().FullName} returned no reasons"));

            if (result.Decision is not { } proposed
                || !Enum.IsDefined(proposed)
                || decision != EvaluationDecision.Pass
                || proposed != EvaluationDecision.Inconclusive)
            {
                continue;
            }

            decision = EvaluationDecision.Inconclusive;
            reasons.RemoveAll(reason => reason == EvaluationReason.ScopeBuildPolicyPinned);
        }

        if (decision != EvaluationDecision.Pass)
        {
            // The pass-only marker says scope, build and policy all passed, so no non-pass result may carry it, even
            // when a downgrade or a later layer contributed it again.
            reasons.RemoveAll(reason => reason == EvaluationReason.ScopeBuildPolicyPinned);
        }

        return (decision, ReasonSet.Normalize(reasons));
    }

    /// <summary>
    /// The read-only snapshot of <c>reasons[]</c> a layer reads. It is a copy, and it is not an
    /// <see cref="IList{T}"/>, so a layer that casts it cannot reach the evaluator's own accumulated list.
    /// </summary>
    private sealed class ReasonView : IReadOnlyList<EvaluationReason>
    {
        private readonly EvaluationReason[] reasons;

        public ReasonView(IReadOnlyList<EvaluationReason> reasons) => this.reasons = [.. reasons];

        public EvaluationReason this[int index] => reasons[index];

        public int Count => reasons.Length;

        public IEnumerator<EvaluationReason> GetEnumerator() =>
            ((IEnumerable<EvaluationReason>)reasons).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
