namespace Evidgate.Evaluation;

/// <summary>
/// One post-core decision layer: the seam a waiver, quarantine or flaky rule composes over the base computation.
/// <para>
/// A layer may add reasons and may remove a core <see cref="EvaluationDecision.Pass"/>; it never raises a decision.
/// The evaluator ignores a layer's attempt to turn a failure into anything better, so a waiver or quarantine can
/// never launder a confirmed failure into a pass, and the core verdict stays visible on the result.
/// </para>
/// </summary>
public interface IDecisionLayer
{
    /// <summary>Application order, ascending; a lower value runs first.</summary>
    int Order { get; }

    /// <summary>Computes this layer's contribution from the immutable inputs and the decision so far.</summary>
    DecisionLayerResult Apply(DecisionLayerContext context);
}

/// <summary>The immutable view a layer reads: the three inputs plus the computation before this layer.</summary>
/// <param name="Evidence">The frozen evidence revision.</param>
/// <param name="Policy">The pinned policy revision.</param>
/// <param name="Exceptions">The resolved waiver and quarantine snapshot.</param>
/// <param name="Completeness">The core completeness verdict.</param>
/// <param name="Missing">The core <c>missing[]</c> codes, in contract order.</param>
/// <param name="Outcome">The core outcome over the required scope.</param>
/// <param name="CoreDecision">The decision the core rules produced, before any layer.</param>
/// <param name="Decision">The decision after the layers that already ran.</param>
/// <param name="Reasons">The reasons accumulated so far, including the core's.</param>
public sealed record DecisionLayerContext(
    EvidenceRevisionInput Evidence,
    PolicyRevisionInput Policy,
    ExceptionsSnapshotInput Exceptions,
    EvaluationCompleteness Completeness,
    IReadOnlyList<MissingReasonCode> Missing,
    EvaluationOutcome Outcome,
    EvaluationDecision CoreDecision,
    EvaluationDecision Decision,
    IReadOnlyList<EvaluationReason> Reasons);

/// <summary>What a layer contributes: reasons it adds, and optionally a downgrade of a core pass.</summary>
/// <param name="Reasons">Reason codes to append; the core reasons are always kept.</param>
/// <param name="Decision">
/// A replacement decision. It only takes effect while the decision so far is <see cref="EvaluationDecision.Pass"/>;
/// any other value, including a pass over a non-pass, is ignored.
/// </param>
public sealed record DecisionLayerResult(
    IReadOnlyList<EvaluationReason> Reasons,
    EvaluationDecision? Decision = null);
