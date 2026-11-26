namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>One component a release declares: its key and the build digest the release requires for it.</summary>
/// <param name="ComponentKey">The component's key inside its release.</param>
/// <param name="Digest">The <c>sha256:</c> digest the release requires.</param>
public sealed record ReleaseAggregationComponent(string ComponentKey, string Digest);

/// <summary>
/// One stored binding of a run to a release component, with the facts the aggregation needs from the run row.
/// </summary>
/// <param name="RunId">The bound run.</param>
/// <param name="ComponentKey">The release component the run is bound for.</param>
/// <param name="Purpose">The run's purpose, <c>regular</c> or <c>replay</c>.</param>
/// <param name="RunDigest">The digest the run recorded for that key, or null when it recorded none.</param>
public sealed record ReleaseAggregationBinding(Guid RunId, string ComponentKey, string Purpose, string? RunDigest);

/// <summary>
/// Everything the release aggregation reads from the release side, in the order the aggregation depends on:
/// components by key, bindings by <c>(created_at, id)</c> so the newest binding is last.
/// </summary>
/// <param name="ReleaseId">The release.</param>
/// <param name="ProjectId">The release's project.</param>
/// <param name="Components">The declared components, ordered by component key.</param>
/// <param name="Bindings">The stored bindings, oldest first; empty when <paramref name="BindingsExceeded"/>.</param>
/// <param name="BindingsExceeded">True when the release holds more bindings than the aggregation reads, so none are returned.</param>
public sealed record ReleaseAggregationInputs(
    Guid ReleaseId,
    Guid ProjectId,
    IReadOnlyList<ReleaseAggregationComponent> Components,
    IReadOnlyList<ReleaseAggregationBinding> Bindings,
    bool BindingsExceeded = false);
