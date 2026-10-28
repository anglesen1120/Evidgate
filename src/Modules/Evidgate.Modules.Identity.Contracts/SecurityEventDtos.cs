using System.Text.Json;

namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// One security event of <c>GET /v1/orgs/{o}/security-events</c> (11.5.1 §5, AC3). The type name is the
/// served OpenAPI schema name, so it must stay <c>SecurityEventItem</c> — the name
/// <c>contracts/openapi.yaml</c> declares (as <see cref="AuditEntryDto"/> is for the audit read).
/// </summary>
/// <param name="Id">Row id.</param>
/// <param name="ProjectId">The project the event belongs to, if it belongs to one.</param>
/// <param name="Type">auth_failure | cross_tenant_probe | secret_scanner_alert | kill_switch_used | support_access.</param>
/// <param name="Severity">info | warning | critical.</param>
/// <param name="Actor"><c>{kind, id}</c> of whoever caused the event.</param>
/// <param name="Details">Ids, hashes and counts only — never a raw payload, a secret or a reason text.</param>
/// <param name="RequestId">The 00 §9.2 request id, when the event came from a request.</param>
/// <param name="CreatedAt">When the row was written (UTC, microseconds).</param>
public sealed record SecurityEventItem(
    Guid Id,
    Guid? ProjectId,
    string Type,
    string Severity,
    JsonElement Actor,
    JsonElement Details,
    string? RequestId,
    DateTimeOffset CreatedAt);

/// <summary>Cursor page of security events, newest first (00 §9.1).</summary>
/// <param name="Items">The page ordered by <c>(created_at, id)</c> descending.</param>
/// <param name="NextCursor">Opaque cursor of the next page, or null on the last page.</param>
public sealed record SecurityEventList(IReadOnlyList<SecurityEventItem> Items, string? NextCursor);
