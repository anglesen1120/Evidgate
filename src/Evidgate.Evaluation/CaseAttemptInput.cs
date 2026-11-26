namespace Evidgate.Evaluation;

/// <summary>One admitted attempt of one planned execution, as frozen with the evidence revision.</summary>
/// <param name="ExecutionKey">The planned execution the attempt belongs to.</param>
/// <param name="CaseId">The catalog case the execution runs.</param>
/// <param name="AttemptNo">The one-based attempt number within the execution.</param>
/// <param name="Status">The normalized terminal status of the attempt.</param>
public sealed record CaseAttemptInput(string ExecutionKey, Guid CaseId, int AttemptNo, CaseStatus Status);
