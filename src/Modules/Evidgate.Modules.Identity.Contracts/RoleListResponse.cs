namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The 00 §9.1 cursor page of <c>GET /v1/roles</c>: keyset-ordered on
/// <c>(created_at, id)</c>, exactly as 00 §9.1 prescribes for a growing list.
/// </summary>
/// <param name="Items">The page's rows, in <c>(created_at, id)</c> order.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null on the last page.</param>
public sealed record RoleListResponse(IReadOnlyList<RoleView> Items, string? NextCursor);
