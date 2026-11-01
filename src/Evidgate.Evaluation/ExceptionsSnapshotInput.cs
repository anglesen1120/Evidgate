namespace Evidgate.Evaluation;

/// <summary>The waiver and quarantine snapshot resolved at trigger time: ids and their instant, never live rows.</summary>
/// <param name="AsOf">The instant the resolver read the rows; the evaluator never reads a clock to get it.</param>
/// <param name="WaiverIds">The approved, unexpired waiver ids in scope.</param>
/// <param name="QuarantineIds">The active quarantine entry ids in scope.</param>
public sealed record ExceptionsSnapshotInput(
    DateTimeOffset AsOf,
    IReadOnlyList<Guid> WaiverIds,
    IReadOnlyList<Guid> QuarantineIds);
