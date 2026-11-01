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
    EvaluationDecision CoreDecision);
