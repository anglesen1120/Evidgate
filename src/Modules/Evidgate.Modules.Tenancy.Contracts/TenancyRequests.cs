namespace Evidgate.Modules.Tenancy.Contracts;

/// <summary>
/// Body of <c>POST /v1/orgs</c> (02.1.1 §5). The caller becomes the org's first
/// member with <c>tenant_admin</c> and <c>release_approver</c> (T98).
/// </summary>
/// <param name="Name">Display name, 1–128 characters.</param>
/// <param name="Slug">Globally unique slug, D-A1 grammar; taken → 409 <c>slug_taken</c>.</param>
public sealed record CreateOrganizationRequest(string Name, string Slug);

/// <summary>
/// Body of <c>PATCH /v1/orgs/{o}</c> (02.1.1 §5). An omitted (null) field is left
/// unchanged; <c>If-Match</c> carries the row's <c>version</c> (stale → 412). Status
/// transitions are not part of this route (02.1.1 §6).
/// </summary>
/// <param name="Name">New display name, or null to keep it.</param>
/// <param name="Slug">New slug, or null to keep it.</param>
/// <param name="RetentionDefaultDays">New default retention in days (≥ 1), or null to keep it.</param>
/// <param name="AllowedEmailDomains">New Google Workspace domain allowlist (≤ 64 entries), or null to keep it.</param>
public sealed record UpdateOrganizationRequest(
    string? Name,
    string? Slug,
    int? RetentionDefaultDays,
    IReadOnlyList<string>? AllowedEmailDomains);

/// <summary>Body of <c>POST /v1/orgs/{o}/projects</c> (02.1.1 §5).</summary>
/// <param name="Name">Display name, 1–128 characters.</param>
/// <param name="Slug">Slug unique within the org, D-A1 grammar; taken → 409 <c>slug_taken</c>.</param>
public sealed record CreateProjectRequest(string Name, string Slug);

/// <summary>
/// Body of <c>PATCH /v1/projects/{p}</c> (02.1.1 §5). An omitted (null) field is
/// left unchanged; <c>If-Match</c> carries the row's <c>version</c>.
/// </summary>
/// <param name="Name">New display name, or null to keep it.</param>
/// <param name="Slug">New slug, or null to keep it.</param>
/// <param name="RetentionDays">New retention override in days (≥ 1), or null to keep it.</param>
public sealed record UpdateProjectRequest(string? Name, string? Slug, int? RetentionDays);

/// <summary>Cursor page of <c>GET /v1/orgs/{o}/projects</c> (00 §9.1 keyset paging).</summary>
/// <param name="Items">The page, ordered by <c>(created_at, id)</c>.</param>
/// <param name="NextCursor">Opaque cursor of the next page, or null on the last page.</param>
public sealed record ProjectListResponse(IReadOnlyList<ProjectDto> Items, string? NextCursor);
