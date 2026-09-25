namespace Evidgate.Modules.Ingestion.Contracts;

/// <summary>
/// Ingestion's exported service interface (00 §5.5: another module reaches
/// Ingestion only through this project). 01.1.2-T2.1 placeholder for the
/// sprint-1 skeleton: a manifest store and nothing else; 04 replaces it with the
/// real manifest/event pipeline (shards, publisher slots, dedupe).
/// </summary>
public interface IIngestion
{
    /// <summary>Registers a manifest submission and returns its identity and content hash.</summary>
    ValueTask<ManifestSnapshot> StoreManifestAsync(ManifestSubmission submission, CancellationToken cancellationToken);

    /// <summary>Reads a registered manifest; null when it is unknown.</summary>
    ValueTask<ManifestSnapshot?> GetManifestAsync(string manifestId, CancellationToken cancellationToken);
}

/// <summary>A manifest submission as the skeleton accepts it (04 defines the real wire shape).</summary>
public sealed record ManifestSubmission(string ProjectRef, string SourceRef, string Content);

/// <summary>A registered manifest: stable id plus its <c>sha256:&lt;hex&gt;</c> content hash (00 §7).</summary>
public sealed record ManifestSnapshot(string ManifestId, string ProjectRef, string Hash);
