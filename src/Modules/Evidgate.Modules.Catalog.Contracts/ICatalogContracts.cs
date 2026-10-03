namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// The catalog surface other modules call in-process (00 §5.5): run creation (05.1.1) locks a suite revision and reads its
/// isolation mode (05.7.3-T4). Must run inside the caller's tenant transaction.
/// </summary>
public interface ICatalogContracts
{
    /// <summary>
    /// Marks the revision <c>published</c> — the one-way flip a run makes when it references the revision (00 §8.10).
    /// Idempotent; returns false when no such revision is visible to the tenant.
    /// </summary>
    /// <param name="suiteRevisionId">The revision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<bool> PublishSuiteRevisionAsync(Guid suiteRevisionId, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>data_isolation</c> the revision's environment requirements declare (<c>isolated</c> when omitted), or null
    /// when the revision does not exist for this tenant. The single isolation source (00 §15).
    /// </summary>
    /// <param name="suiteRevisionId">The revision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<string?> GetDataIsolationAsync(Guid suiteRevisionId, CancellationToken cancellationToken);
}
