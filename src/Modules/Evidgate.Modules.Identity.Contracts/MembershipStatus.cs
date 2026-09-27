namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The <c>membership.status</c> values of 02.1.2 §6, as the strings the wire and the
/// database carry. <c>pending → active</c> (invite secret plus a provider-verified
/// matching email, T75), <c>pending → revoked</c> (cancel), <c>pending → expired</c>
/// (the invite sweeper), <c>active → removed</c> (admin delete, terminal). The type
/// is a constant holder, not an enum: the column is <c>text</c> with a check
/// constraint, so the strings are the contract (00 §7, "typed columns … lower_snake").
/// </summary>
public static class MembershipStatus
{
    /// <summary>An invite that has not been bound to a user yet.</summary>
    public const string Pending = "pending";

    /// <summary>A member whose login bound the invite (or who was created active).</summary>
    public const string Active = "active";

    /// <summary>Removed by an admin; terminal (the row is kept for audit history).</summary>
    public const string Removed = "removed";

    /// <summary>The pending invite was cancelled; it can never bind afterwards.</summary>
    public const string Revoked = "revoked";

    /// <summary>The pending invite outlived <c>invite_expires_at</c>.</summary>
    public const string Expired = "expired";

    /// <summary>Every legal value, in the order 02.1.2 §6 introduces them.</summary>
    public static readonly IReadOnlyList<string> All = [Pending, Active, Removed, Revoked, Expired];

    /// <summary>True when <paramref name="status"/> is one of <see cref="All"/>.</summary>
    public static bool IsDefined(string? status) =>
        status is not null && All.Contains(status, StringComparer.Ordinal);
}
