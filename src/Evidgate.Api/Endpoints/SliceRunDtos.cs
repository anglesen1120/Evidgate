namespace Evidgate.Api.Endpoints;

/// <summary>
/// Body of the slice run route (01.1.2-T3.2): a manifest submission plus how many
/// events the batch carries. snake_case on the wire.
/// </summary>
public sealed record SliceRunRequest(
    string? ProjectRef,
    string? SourceRef = null,
    string? ManifestContent = null,
    int? EventCount = null);

/// <summary>Result of the slice chain: ids, hashes and the two stubs, per 01.1.2 §5.</summary>
public sealed record SliceRunResponse(
    string ManifestId,
    string ManifestHash,
    string RunId,
    IReadOnlyList<string> ExecutionIds,
    int AcceptedCount,
    string Completeness,
    string Decision);
