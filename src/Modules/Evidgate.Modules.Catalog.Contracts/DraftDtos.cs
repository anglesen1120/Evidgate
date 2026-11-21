using System.Text.Json;

namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>One file of a draft's content set: a repository path and the body it points at.</summary>
/// <param name="Path">Repository-relative path, 1–512 characters, no leading slash and no <c>..</c> segment.</param>
/// <param name="BodySha"><c>sha256:</c> plus 64 lower-case hex characters: the identity of the body.</param>
/// <param name="BodyRef">Where the body lives (a repository path or artifact id), never the body itself.</param>
public sealed record DraftFileDto(string Path, string BodySha, string BodyRef);

/// <summary>The content set of a draft; the body text is referenced, never inlined.</summary>
/// <param name="Files">The files, 1–200; paths are unique inside the set.</param>
public sealed record DraftContentDto(IReadOnlyList<DraftFileDto> Files);

/// <summary>
/// Wire shape of a draft (<c>POST /v1/projects/{p}/drafts</c>, <c>PATCH|GET /v1/drafts/{d}</c>). The ETag is
/// <see cref="Version"/>, so a later PATCH sends it back as <c>If-Match</c>.
/// </summary>
/// <param name="Id">Draft id.</param>
/// <param name="ProjectId">Owning project; a credential scoped elsewhere learns nothing about the draft.</param>
/// <param name="AuthorMembershipId">The active membership that created the draft.</param>
/// <param name="Kind"><c>template</c>, <c>ai</c> or <c>regression</c>.</param>
/// <param name="BaseCommit">The commit the draft was cut from.</param>
/// <param name="Status">The draft state; only <c>draft</c> accepts content edits.</param>
/// <param name="Version">Monotonic version: the ETag source and the <c>If-Match</c> precondition.</param>
/// <param name="Content">The file set.</param>
/// <param name="Provenance">Free-form origin metadata (template, AI run, incident), or null.</param>
/// <param name="AiRunId">The AI run that authored the draft, when one did.</param>
/// <param name="DryRunId">The newest dry run of the draft, when one ran.</param>
/// <param name="AllowUnverified">Whether a dry-failed draft may be opened as a PR after a recorded reason.</param>
/// <param name="UnverifiedReason">The reason recorded with <paramref name="AllowUnverified"/>.</param>
/// <param name="PrBranch">The branch a PR was opened from, when one was.</param>
/// <param name="PrCommitSha">The commit the PR holds.</param>
/// <param name="PrUrl">The PR's URL.</param>
/// <param name="PrNumber">The PR's provider number.</param>
/// <param name="LinkedCaseId">The catalog case a merged PR created or updated.</param>
/// <param name="LinkedRevisionId">The revision that merge produced.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="UpdatedAt">The last write, UTC.</param>
public sealed record DraftDto(
    Guid Id,
    Guid ProjectId,
    Guid AuthorMembershipId,
    string Kind,
    string BaseCommit,
    string Status,
    int Version,
    DraftContentDto Content,
    JsonElement? Provenance,
    Guid? AiRunId,
    Guid? DryRunId,
    bool AllowUnverified,
    string? UnverifiedReason,
    string? PrBranch,
    string? PrCommitSha,
    string? PrUrl,
    int? PrNumber,
    Guid? LinkedCaseId,
    Guid? LinkedRevisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Body of <c>POST /v1/projects/{p}/drafts</c>.</summary>
/// <param name="Kind"><c>template</c>, <c>ai</c> or <c>regression</c>.</param>
/// <param name="BaseCommit">7–64 lower-case hex characters of the commit the draft is cut from.</param>
/// <param name="Content">The file set, 1–200 files.</param>
/// <param name="Provenance">Origin metadata, a JSON object of at most 4096 bytes, or null.</param>
public sealed record CreateDraftRequest(
    string Kind,
    string BaseCommit,
    DraftContentDto Content,
    JsonElement? Provenance = null);

/// <summary>Body of <c>PATCH /v1/drafts/{d}</c>: the replacement content set; identity members never change.</summary>
/// <param name="Content">The replacement file set, 1–200 files.</param>
public sealed record UpdateDraftRequest(DraftContentDto Content);

/// <summary>Cursor page of <c>GET /v1/projects/{p}/drafts</c>, oldest first on <c>(created_at, id)</c>.</summary>
/// <param name="Items">The page.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
public sealed record DraftListResponse(IReadOnlyList<DraftDto> Items, string? NextCursor);
