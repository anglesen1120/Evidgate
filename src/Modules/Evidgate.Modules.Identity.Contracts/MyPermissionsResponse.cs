using System.Text.Json.Serialization;

namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The answer of <c>GET /v1/me/permissions</c>: the signed-in member's own effective grants in
/// the org. Membership is the only requirement to read it (no <c>[Permission]</c>), because the
/// answer is the caller's own grants and reveals nothing about anyone else.
/// </summary>
/// <param name="OrgId">The org the grants were resolved in.</param>
/// <param name="ProjectId">The project the caller asked about, echoed; absent when none was asked.</param>
/// <param name="Roles">Codes of the caller's active member-kind roles in the org, ordinal-sorted.</param>
/// <param name="Permissions">Catalogue codes the caller holds in the org, ordinal-sorted.</param>
public sealed record MyPermissionsResponse(
    Guid OrgId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ProjectId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
