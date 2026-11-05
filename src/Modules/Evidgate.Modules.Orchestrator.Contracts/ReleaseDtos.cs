namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>The 201 body of <c>POST /v1/projects/{p}/releases</c>: the id of the declared release.</summary>
/// <param name="ReleaseId">The new release's id.</param>
public sealed record ReleaseCreated(Guid ReleaseId);

/// <summary>
/// The 200 body of <c>GET /v1/releases/{id}</c>: the header, the component rows in <c>component_key</c> order, and the
/// stored bindings in <c>created_at, id</c> order. Three statements and no join across modules.
/// </summary>
/// <param name="ReleaseId">The release's id.</param>
/// <param name="ProjectId">The project the release belongs to.</param>
/// <param name="Name">The declaration's name.</param>
/// <param name="State"><c>draft</c> or <c>active</c>.</param>
/// <param name="CreatedBy">The audit actor that declared it (<c>&lt;type&gt;:&lt;id&gt;</c>).</param>
/// <param name="CreatedAt">When the declaration was stored.</param>
/// <param name="Version">The optimistic-concurrency token of the row.</param>
/// <param name="Components">Every component, ordered by <c>component_key</c>.</param>
/// <param name="BoundRuns">Every stored binding of this release, ordered by <c>created_at</c> then id — insertion order, never recency.</param>
public sealed record ReleaseDetail(
    Guid ReleaseId,
    Guid ProjectId,
    string Name,
    string State,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    int Version,
    IReadOnlyList<ReleaseComponentDetail> Components,
    IReadOnlyList<ReleaseBoundRun> BoundRuns);

/// <summary>
/// One component of a release: the key, the digest it was built at and the environment revision it was built for.
/// </summary>
/// <param name="ComponentKey">The component's key inside its release.</param>
/// <param name="Digest">The build digest, <c>sha256:</c> + 64 lower-case hex. A build identifier, never a secret.</param>
/// <param name="EnvironmentRevisionId">The environment revision the digest was built for.</param>
public sealed record ReleaseComponentDetail(string ComponentKey, string Digest, Guid EnvironmentRevisionId);
