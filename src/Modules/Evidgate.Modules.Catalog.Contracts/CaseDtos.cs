namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// Wire shape of a test case in lists (03.1.1 §5), read into by the Dapper queries and
/// serialized snake_case by the API. Published as the <c>TestCase</c> schema of
/// <c>contracts/openapi.yaml</c>.
/// </summary>
/// <param name="Id">Case id (UUIDv7), addressed by <c>/v1/cases/{c}</c>.</param>
/// <param name="OrgId">Owning organization (composite tenant key).</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="AnnotationId">The durable <c>evidgate.id</c>; the case's identity inside the project, never changes.</param>
/// <param name="Name">Display name; not unique (D2).</param>
/// <param name="Framework">Test framework of the file (<c>playwright</c>, <c>jest</c>, …).</param>
/// <param name="Language">Source language, when known.</param>
/// <param name="FilePath">Current repository path; moves with a rename, null when unknown.</param>
/// <param name="Status"><c>active</c>, <c>missing</c> (file vanished from the synced tree) or <c>archived</c>.</param>
/// <param name="OwnerMembershipId">The responsible member, or null.</param>
/// <param name="Labels">Free labels, at most 32.</param>
/// <param name="FirstSeenCommit">Commit that first registered the case; never changes.</param>
/// <param name="LastSeenCommit">Commit of the last registration change (path, status), or null.</param>
/// <param name="CreatedAt">Row creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
/// <param name="Version">Optimistic concurrency token (ETag source, 00 §9.1).</param>
public sealed record CaseDto(
    Guid Id,
    Guid OrgId,
    Guid ProjectId,
    Guid AnnotationId,
    string Name,
    string Framework,
    string? Language,
    string? FilePath,
    string Status,
    Guid? OwnerMembershipId,
    IReadOnlyList<string> Labels,
    string FirstSeenCommit,
    string? LastSeenCommit,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>One recorded origin of a case's id (03.1.1 <c>case_identity_source</c>): why it is the same case.</summary>
/// <param name="Source"><c>annotation</c>, <c>sidecar</c> or <c>imported</c>.</param>
/// <param name="RawKey">What was read: the annotation text or the sidecar entry key.</param>
/// <param name="CommitSha">Commit that produced it.</param>
/// <param name="CreatedAt">When it was recorded.</param>
public sealed record CaseIdentitySourceDto(string Source, string RawKey, string CommitSha, DateTimeOffset CreatedAt);

/// <summary>One detected move of a case's file (03.1.1 <c>case_rename_history</c>).</summary>
/// <param name="FromPath">Previous path.</param>
/// <param name="ToPath">New path.</param>
/// <param name="CommitSha">Commit that moved it.</param>
/// <param name="DetectedAt">When the move was detected.</param>
public sealed record CaseRenameDto(string FromPath, string ToPath, string CommitSha, DateTimeOffset DetectedAt);

/// <summary>
/// <c>GET /v1/cases/{c}</c>: the case plus its identity trail (newest first, at most 50 of each),
/// so a duplicate-id mistake is diagnosable (03.1.1 user story 2).
/// </summary>
/// <param name="Case">The case.</param>
/// <param name="IdentitySources">Where its id came from.</param>
/// <param name="Renames">Its moves.</param>
public sealed record CaseDetailDto(
    CaseDto Case,
    IReadOnlyList<CaseIdentitySourceDto> IdentitySources,
    IReadOnlyList<CaseRenameDto> Renames);

/// <summary>Cursor page of <c>GET /v1/projects/{p}/cases</c> (00 §9.1 keyset paging).</summary>
/// <param name="Items">The page, ordered by <c>(created_at, id)</c>.</param>
/// <param name="NextCursor">Opaque cursor of the next page, or null on the last page.</param>
public sealed record CaseListResponse(IReadOnlyList<CaseDto> Items, string? NextCursor);

/// <summary>Body of <c>PATCH /v1/cases/{c}</c> (03.1.1 §5). An omitted member is left unchanged; <c>If-Match</c> carries the version.</summary>
/// <param name="Name">New display name.</param>
/// <param name="OwnerMembershipId">New owner membership.</param>
/// <param name="Status"><c>active</c> or <c>archived</c>.</param>
/// <param name="Labels">Replacement labels, at most 32.</param>
public sealed record UpdateCaseRequest(string? Name, Guid? OwnerMembershipId, string? Status, IReadOnlyList<string>? Labels);

/// <summary>One stored version of a test (03.1.3), newest first in <c>GET /v1/cases/{c}/revisions</c>.</summary>
/// <param name="Id">Revision id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="RevisionHash"><c>sha256:</c> over the canonical content descriptor (00 §7).</param>
/// <param name="CommitSha">Commit at which this content was first synced.</param>
/// <param name="FilePath">Path at first sync of this content.</param>
/// <param name="Framework">Framework.</param>
/// <param name="Language">Language, when known.</param>
/// <param name="ContentSha256">Hash of the file content.</param>
/// <param name="AssertionHash">Hash of the extracted assertion descriptors.</param>
/// <param name="CreatedAt">When the revision was first stored.</param>
public sealed record CaseRevisionDto(
    Guid Id,
    Guid CaseId,
    string RevisionHash,
    string CommitSha,
    string FilePath,
    string Framework,
    string? Language,
    string ContentSha256,
    string AssertionHash,
    DateTimeOffset CreatedAt);

/// <summary>Cursor page of <c>GET /v1/cases/{c}/revisions</c>.</summary>
/// <param name="Items">Revisions, newest first.</param>
/// <param name="NextCursor">Opaque cursor of the next page, or null.</param>
public sealed record CaseRevisionListResponse(IReadOnlyList<CaseRevisionDto> Items, string? NextCursor);
