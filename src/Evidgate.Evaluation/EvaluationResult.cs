namespace Evidgate.Evaluation;

/// <summary>The computed evaluation of one evidence revision against one policy revision.</summary>
/// <param name="Completeness">Whether the evidence can decide the required scope on its own.</param>
/// <param name="Missing">The closed <c>missing[]</c> codes behind an incomplete verdict, in contract order.</param>
/// <param name="Outcome">What the required scope's results say, computed independently of the decision.</param>
/// <param name="Decision">The final decision, after every decision layer ran.</param>
/// <param name="Reasons">The closed <c>reasons[]</c> codes, sorted by token; never free text.</param>
/// <param name="CoreDecision">
/// The decision the core rules produced before any layer ran. It stays visible so a layer that downgraded a pass, or
/// one whose upgrade attempt was ignored, can be told from the core verdict.
/// </param>
public sealed record EvaluationResult(
    EvaluationCompleteness Completeness,
    IReadOnlyList<MissingReasonCode> Missing,
    EvaluationOutcome Outcome,
    EvaluationDecision Decision,
    IReadOnlyList<EvaluationReason> Reasons,
    EvaluationDecision CoreDecision)
{
    /// <summary>Creates a result that also names the quarantined cases whose failure was excluded.</summary>
    /// <param name="completeness">Whether the evidence can decide the required scope on its own.</param>
    /// <param name="missing">The closed <c>missing[]</c> codes behind an incomplete verdict.</param>
    /// <param name="outcome">What the required scope's results say.</param>
    /// <param name="decision">The final decision.</param>
    /// <param name="reasons">The closed <c>reasons[]</c> codes.</param>
    /// <param name="coreDecision">The decision before any layer ran.</param>
    /// <param name="quarantinedCases">The excluded quarantined case ids, ascending and without duplicates.</param>
    public EvaluationResult(
        EvaluationCompleteness completeness,
        IReadOnlyList<MissingReasonCode> missing,
        EvaluationOutcome outcome,
        EvaluationDecision decision,
        IReadOnlyList<EvaluationReason> reasons,
        EvaluationDecision coreDecision,
        IReadOnlyList<Guid> quarantinedCases)
        : this(completeness, missing, outcome, decision, reasons, coreDecision) =>
        QuarantinedCases = quarantinedCases ?? throw new ArgumentNullException(nameof(quarantinedCases));

    /// <summary>
    /// The ids of non-critical quarantined cases whose confirmed failure was excluded from the gate, ascending and
    /// without duplicates; empty when none was.
    /// </summary>
    public IReadOnlyList<Guid> QuarantinedCases { get; init; } = [];

    /// <summary>
    /// The executions that passed only on a retry, ordered by execution key (ordinal); empty when there were none. They
    /// are reported for inspection and are not persisted.
    /// </summary>
    public IReadOnlyList<FlakyFact> FlakyFacts { get; init; } = [];
}
