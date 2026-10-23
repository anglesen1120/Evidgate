namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>The 201 body of <c>POST /v1/projects/{p}/manifests</c> (05.1.1 §5).</summary>
/// <param name="ManifestId">The stored manifest; the existing one when the same canonical content was uploaded before.</param>
/// <param name="ManifestHash"><c>sha256:</c> over the RFC 8785 canonical form of the body (00 §7).</param>
public sealed record ManifestAccepted(Guid ManifestId, string ManifestHash);

/// <summary>The body of <c>POST /v1/projects/{p}/environments/{e}/revisions</c> (05.1.2 §5; <c>contracts/schemas/environment-revision.v1.json</c>).</summary>
/// <param name="Name">Display name of the environment; optional.</param>
/// <param name="RevisionNo">Optional optimistic check: the number the caller expects this revision to get.</param>
/// <param name="BaseUrls">Named base URLs.</param>
/// <param name="DependencyVersions">Result-affecting dependency versions.</param>
/// <param name="SecretRefs"><c>secret:</c> references, never values.</param>
public sealed record CreateEnvironmentRevisionRequest(
    string? Name,
    int? RevisionNo,
    IReadOnlyDictionary<string, string> BaseUrls,
    IReadOnlyDictionary<string, string> DependencyVersions,
    IReadOnlyList<string>? SecretRefs);

/// <summary>The answer of an environment-revision create: 201 for a new revision, 200 for identical content already stored.</summary>
/// <param name="RevisionId">The revision.</param>
/// <param name="RevisionNo">Its number inside the environment.</param>
/// <param name="RevisionHash"><c>sha256:</c> over the RFC 8785 canonical configuration.</param>
/// <param name="Created">True when this call stored the revision.</param>
public sealed record EnvironmentRevisionCreated(Guid RevisionId, int RevisionNo, string RevisionHash, bool Created);

/// <summary>Wire shape of <c>GET /v1/environment-revisions/{id}</c> (05.1.2 §5); secret-free by construction.</summary>
/// <param name="RevisionId">The revision.</param>
/// <param name="EnvironmentId">Its environment.</param>
/// <param name="EnvironmentKey">The environment's key (the <c>{e}</c> of the create route).</param>
/// <param name="ProjectId">The project.</param>
/// <param name="RevisionNo">Its number inside the environment.</param>
/// <param name="RevisionHash">The hash, also the <c>ETag</c>.</param>
/// <param name="BaseUrls">Named base URLs.</param>
/// <param name="DependencyVersions">Result-affecting dependency versions.</param>
/// <param name="SecretRefs">The <c>secret:</c> references, sorted.</param>
/// <param name="CreatedAt">When it was stored, UTC.</param>
public sealed record EnvironmentRevisionDto(
    Guid RevisionId,
    Guid EnvironmentId,
    string EnvironmentKey,
    Guid ProjectId,
    int RevisionNo,
    string RevisionHash,
    IReadOnlyDictionary<string, string> BaseUrls,
    IReadOnlyDictionary<string, string> DependencyVersions,
    IReadOnlyList<string> SecretRefs,
    DateTimeOffset CreatedAt);
