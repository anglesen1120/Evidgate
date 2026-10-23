namespace Evidgate.Modules.Identity.Domain;

/// <summary>
/// The <c>role.kind</c> values migration 0010 constrains (W7-SEC-03, 02 §4.1): who may
/// hold the role. A <c>membership_role</c> may reference a <see cref="Member"/> role
/// only — the five member roles of 00 §2 and every custom role — while
/// <see cref="Service"/> (<c>ci_producer</c>, <c>agent</c>) belongs to a service token
/// and <see cref="Staff"/> (<c>platform_operator</c>, <c>billing_operator</c>) to an
/// Evidgate staff account with no standing membership. The type is a constant holder,
/// not an enum, exactly like <c>MembershipStatus</c>: the column is <c>text</c> with a
/// check constraint, so the strings are the contract (00 §7).
/// </summary>
internal static class RoleKind
{
    /// <summary>A role a member may hold; the only kind <c>GrantRole</c>/<c>InviteMember</c> resolve.</summary>
    internal const string Member = "member";

    /// <summary>A service identity's role (<c>ci_producer</c>, <c>agent</c>) — never a membership's.</summary>
    internal const string Service = "service";

    /// <summary>An Evidgate staff role (<c>platform_operator</c>, <c>billing_operator</c>) — never a tenant member's.</summary>
    internal const string Staff = "staff";

    /// <summary>Every value the <c>role_kind_check</c> of 0010 accepts.</summary>
    internal static readonly IReadOnlyList<string> All = [Member, Service, Staff];

    /// <summary>True when <paramref name="kind"/> is one of <see cref="All"/>.</summary>
    internal static bool IsDefined(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.Ordinal);
}
