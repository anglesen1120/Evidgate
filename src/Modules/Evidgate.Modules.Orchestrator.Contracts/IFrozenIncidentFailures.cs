namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>Required executions that were failing at the named freeze and their failing attempts up to that instant.</summary>
public interface IFrozenIncidentFailures
{
    /// <summary>A missing revision is null; a replay or a freeze without eligible failures is an empty batch.</summary>
    /// <param name="runId">The run of the frozen revision.</param>
    /// <param name="revisionNo">The immutable revision number.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<FrozenIncidentFailures?> ReadAsync(Guid runId, int revisionNo, CancellationToken cancellationToken);
}

/// <summary>The immutable case revision, rather than an adapter's optional producer hint, supplies each framework.</summary>
/// <param name="ProjectId">The project of the run.</param>
/// <param name="Attempts">Failures in execution key and ascending attempt order.</param>
/// <param name="Frameworks">Framework keyed by pinned case revision id.</param>
public sealed record FrozenIncidentFailures(Guid ProjectId, IReadOnlyList<FrozenFailureAttempt> Attempts,
    IReadOnlyDictionary<Guid, string> Frameworks);

/// <summary>One admitted failure on a required planned execution.</summary>
/// <param name="ExecutionKey">The immutable execution identity.</param>
/// <param name="AttemptId">The failing attempt.</param>
/// <param name="AttemptNo">Its retry number.</param>
/// <param name="Result">The normalized failure status.</param>
/// <param name="CaseId">The executed case.</param>
/// <param name="CaseRevisionId">The revision pinned by the run's manifest.</param>
public sealed record FrozenFailureAttempt(string ExecutionKey, Guid AttemptId, int AttemptNo, string Result,
    Guid CaseId, Guid CaseRevisionId);
