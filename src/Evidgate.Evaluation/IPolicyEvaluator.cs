namespace Evidgate.Evaluation;

/// <summary>
/// The pure decision function: it takes a frozen evidence revision, the pinned policy revision and the resolved
/// exceptions snapshot, performs no I/O, reads no clock and generates no identifier, and returns the same result for
/// structurally equal inputs in any process.
/// </summary>
public interface IPolicyEvaluator
{
    /// <summary>
    /// Bumped only when the observable behaviour of <see cref="Evaluate"/> changes; stored verbatim on the persisted
    /// evaluation so a past decision stays explainable.
    /// </summary>
    public const string EvaluatorVersion = "1.0.0";

    /// <summary>Computes completeness, outcome and decision for one evidence and policy revision pair.</summary>
    EvaluationResult Evaluate(
        EvidenceRevisionInput evidence,
        PolicyRevisionInput policy,
        ExceptionsSnapshotInput exceptions);
}
