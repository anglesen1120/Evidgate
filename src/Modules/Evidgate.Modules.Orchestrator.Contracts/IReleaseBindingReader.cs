namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The read-only surface over the stored bindings: which of a release's components a run is bound for, and which it
/// is not. Every member runs inside the caller's tenant transaction and writes nothing;
/// the mismatch list is derived from the release's components and the run's recorded <c>components</c>, never from a
/// stored verdict.
/// </summary>
public interface IReleaseBindingReader
{
    /// <summary>
    /// The component keys of <paramref name="releaseId"/> the run is bound for, ordered by key. An unknown or
    /// invisible release, or a run that is not bound, answers an empty list: "nothing here" is not an error, and a
    /// foreign pair is indistinguishable from an unknown one.
    /// </summary>
    /// <param name="releaseId">The release to read the binding of.</param>
    /// <param name="runId">The run to read the binding of.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<string>> BoundKeysAsync(Guid releaseId, Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Every release component the run does not match, ordered by component key: the part of the pair that a
    /// decision's <c>build_mismatch</c> reason is built from. Computed from the two stored sides, so a read is exactly
    /// what the bind path computed, with no row of its own; an unknown or invisible release, or a run that is not
    /// visible, answers an empty list.
    /// </summary>
    /// <param name="releaseId">The release whose components are compared.</param>
    /// <param name="runId">The run whose recorded <c>components</c> are compared.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<ReleaseMismatch>> MismatchesAsync(
        Guid releaseId,
        Guid runId,
        CancellationToken cancellationToken);
}
