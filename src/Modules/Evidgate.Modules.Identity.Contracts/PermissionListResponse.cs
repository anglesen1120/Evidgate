namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The 00 §9.1 cursor page of <c>GET /v1/permissions</c>: the catalogue ordered by
/// <c>code</c> — the catalogue's primary key, so the page order is total without a
/// surrogate <c>(created_at, id)</c> pair (a platform catalogue has no
/// <c>created_at</c>; the deviation is deliberate and documented in 02.2.1-T1).
/// </summary>
/// <param name="Items">The page's rows, in <c>code</c> order.</param>
/// <param name="NextCursor">The last row's code when a further page exists, else null.</param>
public sealed record PermissionListResponse(IReadOnlyList<PermissionView> Items, string? NextCursor);
