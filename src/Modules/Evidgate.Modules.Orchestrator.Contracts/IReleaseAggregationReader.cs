namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The read-only surface the release aggregation uses over the release tables: the declared components, the stored
/// bindings with the bound run's digest and purpose, and the releases a run is bound to. Every member runs inside the
/// caller's tenant transaction and writes nothing.
/// </summary>
public interface IReleaseAggregationReader
{
    /// <summary>The release's project, or null for an unknown or other tenant's release (one answer for both).</summary>
    /// <param name="releaseId">The release to look up.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<Guid?> GetProjectIdAsync(Guid releaseId, CancellationToken cancellationToken);

    /// <summary>The aggregation inputs of one release in a constant number of statements, or null when it is not visible.</summary>
    /// <param name="releaseId">The release to read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<ReleaseAggregationInputs?> GetInputsAsync(Guid releaseId, CancellationToken cancellationToken);

    /// <summary>The distinct releases a run is bound to, oldest first, at most <see cref="MaxReleasesPerRun"/>.</summary>
    /// <param name="runId">The run whose bindings are read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<Guid>> GetReleaseIdsBoundToRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The ceiling of <see cref="GetReleaseIdsBoundToRunAsync"/>, so one event fans out to a bounded set.</summary>
    const int MaxReleasesPerRun = 100;

    /// <summary>The ceiling of bindings one aggregation reads; a release with more is refused rather than truncated.</summary>
    const int MaxBindings = 2000;
}
