namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>The framework of each immutable test revision, scoped to the project that owns its case.</summary>
public interface IIncidentRevisionFrameworks
{
    /// <summary>Reads all requested revisions in the caller's tenant transaction. Foreign revisions are absent.</summary>
    /// <param name="projectId">The project of the frozen run.</param>
    /// <param name="revisionIds">The revisions pinned by its planned executions.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyDictionary<Guid, string>> ReadAsync(Guid projectId, Guid[] revisionIds, CancellationToken cancellationToken);
}
