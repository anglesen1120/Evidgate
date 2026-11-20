using System.Text.Json;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>One row of a version page (13.1.1 §5 route 22).</summary>
/// <param name="VersionId">The <c>authoring_version.id</c>.</param>
/// <param name="EntityKind"><c>feature_node</c>, <c>checklist_item</c> or <c>manual_test_case</c>.</param>
/// <param name="EntityId">The entity the version describes.</param>
/// <param name="VersionNo">The entity's version number at the time of the change.</param>
/// <param name="BatchId">The change the row belongs to: the UI groups one batch into one version.</param>
/// <param name="ChangeKind"><c>create</c>, <c>update</c>, <c>delete</c>, <c>restore</c> or <c>import</c>.</param>
/// <param name="Source"><c>manual</c>, <c>ai</c> or <c>import</c>.</param>
/// <param name="ContentHash">The <c>sha256:&lt;hex&gt;</c> digest of the stored content.</param>
/// <param name="Author">The authoring membership that wrote the change, or null.</param>
/// <param name="Reason">The caller's reason, or null.</param>
/// <param name="AiRunId">The generation run of an AI change, or null.</param>
/// <param name="CreatedAt">When the row was appended.</param>
internal sealed record AuthoringVersionListItemDto(
    Guid VersionId,
    string EntityKind,
    Guid EntityId,
    int VersionNo,
    Guid BatchId,
    string ChangeKind,
    string Source,
    string ContentHash,
    Guid? Author,
    string? Reason,
    Guid? AiRunId,
    DateTimeOffset CreatedAt);

/// <summary>One keyset page of versions (route 22); <paramref name="NextCursor"/> is null on the last page.</summary>
/// <param name="Items">The page, in the fixed <c>(created_at desc, id desc)</c> order.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null when this page is the last.</param>
internal sealed record AuthoringVersionListResponse(IReadOnlyList<AuthoringVersionListItemDto> Items, string? NextCursor);

/// <summary>
/// One stored version (route 23): the row members of route 22 and the stored <c>content</c>, passed through as the JSON
/// the writer hashed. The response's <c>ETag</c> is <see cref="ContentHash"/> quoted, and the content is never
/// re-serialised through a typed model.
/// </summary>
/// <param name="VersionId">The <c>authoring_version.id</c>.</param>
/// <param name="EntityKind">The entity kind.</param>
/// <param name="EntityId">The entity the version describes.</param>
/// <param name="VersionNo">The entity's version number at the time of the change.</param>
/// <param name="BatchId">The change the row belongs to.</param>
/// <param name="ChangeKind">The change kind.</param>
/// <param name="Source">Where the change came from.</param>
/// <param name="ContentHash">The <c>sha256:&lt;hex&gt;</c> digest of <paramref name="Content"/>.</param>
/// <param name="Author">The authoring membership that wrote the change, or null.</param>
/// <param name="Reason">The caller's reason, or null.</param>
/// <param name="AiRunId">The generation run of an AI change, or null.</param>
/// <param name="CreatedAt">When the row was appended.</param>
/// <param name="Content">The stored document, verbatim.</param>
internal sealed record AuthoringVersionDetailDto(
    Guid VersionId,
    string EntityKind,
    Guid EntityId,
    int VersionNo,
    Guid BatchId,
    string ChangeKind,
    string Source,
    string ContentHash,
    Guid? Author,
    string? Reason,
    Guid? AiRunId,
    DateTimeOffset CreatedAt,
    JsonElement Content);
