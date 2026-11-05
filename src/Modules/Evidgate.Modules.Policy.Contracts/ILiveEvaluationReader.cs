namespace Evidgate.Modules.Policy.Contracts;

/// <summary>
/// The one read another module needs to know whether a run already has a stored <c>live</c> verdict. It runs inside the
/// caller's tenant transaction, takes no lock and writes nothing; the caller decides what the answer means for its own
/// state.
/// </summary>
public interface ILiveEvaluationReader
{
    /// <summary>
    /// True when at least one <c>live</c> evaluation row exists for <paramref name="runId"/> in the caller's org. A
    /// <c>shadow</c> row is a second opinion about the same evidence and never counts; an unknown or invisible run
    /// answers false, the same "nothing there" shape as the module's other reads.
    /// </summary>
    /// <param name="runId">The run whose verdicts are asked about.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<bool> HasLiveAsync(Guid runId, CancellationToken cancellationToken);
}
