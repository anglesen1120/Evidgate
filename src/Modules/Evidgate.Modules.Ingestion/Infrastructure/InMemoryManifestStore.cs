using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;

namespace Evidgate.Modules.Ingestion.Infrastructure;

/// <summary>
/// In-memory manifest store of the sprint-1 slice (01.1.2-T3.1: fakes only, no
/// persistence, no RLS). 01.4.2-T1 replaces it with the tenant-scoped Npgsql/EF
/// implementation behind the same <see cref="IIngestion"/> interface.
/// </summary>
internal sealed class InMemoryManifestStore : IIngestion
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Manifest> _manifests =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<ManifestSnapshot> StoreManifestAsync(
        ManifestSubmission submission,
        CancellationToken cancellationToken)
    {
        var manifest = Manifest.Accept(submission.ProjectRef, submission.SourceRef, submission.Content);
        _manifests[manifest.ManifestId] = manifest;

        return ValueTask.FromResult(ToSnapshot(manifest));
    }

    /// <inheritdoc />
    public ValueTask<ManifestSnapshot?> GetManifestAsync(string manifestId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_manifests.TryGetValue(manifestId, out var manifest) ? ToSnapshot(manifest) : null);

    private static ManifestSnapshot ToSnapshot(Manifest manifest) =>
        new(manifest.ManifestId, manifest.ProjectRef, manifest.Hash);
}
