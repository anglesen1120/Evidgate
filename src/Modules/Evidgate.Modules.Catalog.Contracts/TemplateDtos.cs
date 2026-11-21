using System.Text.Json;

namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// Summary of one registered template (<c>GET /v1/templates</c>): the identity a client selects and the content
/// reference it fetches, never the body or the rules.
/// </summary>
/// <param name="Id">Template id.</param>
/// <param name="Kind"><c>web</c>, <c>api</c>, <c>unit</c> or <c>mobile</c>.</param>
/// <param name="Name">Registry name inside the kind, e.g. <c>playwright-ts</c>.</param>
/// <param name="Version">Template release; a newer release is a new row.</param>
/// <param name="BodyRef"><c>sha256:</c> plus 64 lower-case hex characters addressing the packaged body.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
public sealed record TemplateDto(
    Guid Id,
    string Kind,
    string Name,
    int Version,
    string BodyRef,
    DateTimeOffset CreatedAt);

/// <summary>
/// One template (<c>GET /v1/templates/{t}</c>) with the conformance rules <c>evidgate init</c> checks a generated
/// project against. The rules are either a named set or an ordered list, as stored.
/// </summary>
/// <param name="Id">Template id.</param>
/// <param name="Kind"><c>web</c>, <c>api</c>, <c>unit</c> or <c>mobile</c>.</param>
/// <param name="Name">Registry name inside the kind.</param>
/// <param name="Version">Template release.</param>
/// <param name="BodyRef">Content reference of the packaged body.</param>
/// <param name="ConformanceRules">The rules document: a JSON object or array, at most 64 KiB.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
public sealed record TemplateDetailDto(
    Guid Id,
    string Kind,
    string Name,
    int Version,
    string BodyRef,
    JsonElement ConformanceRules,
    DateTimeOffset CreatedAt);

/// <summary>Cursor page of <c>GET /v1/templates</c>, oldest first on <c>(created_at, id)</c>.</summary>
/// <param name="Items">The page: active built-ins plus the caller's own org rows.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
public sealed record TemplateListResponse(IReadOnlyList<TemplateDto> Items, string? NextCursor);
