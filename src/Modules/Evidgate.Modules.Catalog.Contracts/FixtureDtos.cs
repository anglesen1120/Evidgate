namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// Wire shape of a fixture: the project-scoped identity that versions point at.
/// </summary>
/// <param name="Id">Fixture id (UUIDv7).</param>
/// <param name="OrgId">Owning organization.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="FixtureKey">Key unique inside the project; a suite revision names the fixture by it.</param>
/// <param name="HeadRevisionId">The newest revision.</param>
/// <param name="HeadRevisionNo">Its number.</param>
/// <param name="HeadRevisionHash">Its content identity.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
public sealed record FixtureDto(
    Guid Id,
    Guid OrgId,
    Guid ProjectId,
    string FixtureKey,
    Guid? HeadRevisionId,
    int? HeadRevisionNo,
    string? HeadRevisionHash,
    DateTimeOffset CreatedAt);

/// <summary>Cursor page of <c>GET /v1/projects/{p}/fixtures</c>.</summary>
/// <param name="Items">Fixtures in creation order.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
public sealed record FixtureListResponse(IReadOnlyList<FixtureDto> Items, string? NextCursor);

/// <summary>One stored revision of a fixture.</summary>
/// <param name="Id">Revision id.</param>
/// <param name="FixtureId">The fixture it belongs to.</param>
/// <param name="RevisionNo">Its number, from 1.</param>
/// <param name="RevisionHash"><c>sha256:</c> identity of the content.</param>
/// <param name="SeedVersion">The version of the data the body seeds.</param>
/// <param name="TimeoutS">How long the executor may take, in seconds.</param>
/// <param name="CleanupPolicy"><c>drop</c>, <c>truncate</c> or <c>none</c>.</param>
/// <param name="ResourcePrefix">The prefix every object the executor creates carries; server-derived.</param>
/// <param name="BodyRef">A repository path or artifact id — never the body itself.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
public sealed record FixtureRevisionDto(
    Guid Id,
    Guid FixtureId,
    int RevisionNo,
    string RevisionHash,
    string SeedVersion,
    int TimeoutS,
    string CleanupPolicy,
    string ResourcePrefix,
    string BodyRef,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>GET /v1/fixtures/{f}</c>: the fixture with its head revision. The revisions of a fixture are unbounded, so the
/// detail answers the head it holds today and not a list that would grow without limit.
/// </summary>
/// <param name="Id">Fixture id.</param>
/// <param name="OrgId">Owning organization.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="FixtureKey">Key unique inside the project.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="Head">The newest revision. Nullable because only the create path guarantees one; a fixture with no
/// revision is answered as it is, never as a fabricated head.</param>
public sealed record FixtureDetailDto(
    Guid Id,
    Guid OrgId,
    Guid ProjectId,
    string FixtureKey,
    DateTimeOffset CreatedAt,
    FixtureRevisionDto? Head);

/// <summary>Body of <c>POST /v1/projects/{p}/fixtures</c>: the identity of a new fixture plus its first revision.</summary>
/// <param name="FixtureKey">Key, 1–64 lower-case letters, digits and dashes, unique in the project.</param>
/// <param name="SeedVersion">Version of the data the body seeds, 1–64 characters.</param>
/// <param name="TimeoutS">How long the executor may take; default 60, at most 86400.</param>
/// <param name="CleanupPolicy"><c>drop</c> (default), <c>truncate</c> or <c>none</c>.</param>
/// <param name="BodyRef">A repository-relative path or artifact id; never the body itself.</param>
public sealed record CreateFixtureRequest(
    string FixtureKey,
    string SeedVersion,
    string BodyRef,
    int? TimeoutS = null,
    string? CleanupPolicy = null);

/// <summary>Body of <c>POST /v1/fixtures/{f}/revisions</c>: one new version of an existing fixture.</summary>
/// <param name="SeedVersion">Version of the data the body seeds, 1–64 characters.</param>
/// <param name="BodyRef">A repository-relative path or artifact id; never the body itself.</param>
/// <param name="TimeoutS">How long the executor may take; default 60, at most 86400.</param>
/// <param name="CleanupPolicy"><c>drop</c> (default), <c>truncate</c> or <c>none</c>.</param>
public sealed record CreateFixtureRevisionRequest(
    string SeedVersion,
    string BodyRef,
    int? TimeoutS = null,
    string? CleanupPolicy = null);

/// <summary>Result of creating a fixture or appending a revision.</summary>
/// <param name="FixtureId">The fixture.</param>
/// <param name="FixtureKey">Its key.</param>
/// <param name="RevisionId">The revision this call wrote, or the existing one that carries the same content.</param>
/// <param name="RevisionNo">Its number.</param>
/// <param name="RevisionHash">Its content identity.</param>
/// <param name="Created">True only when this call inserted the revision; identical content answers the existing one.</param>
public sealed record FixtureRevisionHead(
    Guid FixtureId,
    string FixtureKey,
    Guid RevisionId,
    int RevisionNo,
    string RevisionHash,
    bool Created);
