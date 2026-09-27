namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// One row of the platform permission catalogue as <c>GET /v1/permissions</c>
/// exposes it (00 §2, 02 §5). The catalogue holds no secrets: every field here is
/// public role-model data (02 §8 "Privacy/PDPL"), and <see cref="Sensitive"/> is
/// metadata about the permission, never a value.
/// </summary>
/// <param name="Code">The <c>resource:action</c> string — the catalogue's identity.</param>
/// <param name="Resource">The code's resource half.</param>
/// <param name="Action">The code's action half.</param>
/// <param name="Description">What the permission grants, secret-free.</param>
/// <param name="Sensitive">True when the permission gates secret-adjacent data (AC5).</param>
/// <param name="Reserved">True when the string has no V1 consumer yet (AC2).</param>
/// <param name="TenantAssignable">False for a code a member principal must never hold (T79).</param>
public sealed record PermissionView(
    string Code,
    string Resource,
    string Action,
    string Description,
    bool Sensitive,
    bool Reserved,
    bool TenantAssignable);
