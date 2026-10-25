namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// How long a pending invite stays bindable (02.1.2-T2 §4/§6): seven days from the mint, written
/// as <c>invite_expires_at = now() + 7 d</c> by create and again by every resend.
/// </summary>
/// <remarks>
/// A [DECISION] value recorded in the feature plan, not a contract figure: 02.1.2 §4 fixes the
/// column and the sweeper, not a duration. A past-due row stays <c>pending</c> until
/// <c>invite-expiry</c> sweeps it, but <c>evidgate_resolve.invite_by_secret</c> already refuses to
/// resolve it, so an expired invite can never bind even before the sweep runs.
/// </remarks>
internal static class InviteLifetime
{
    /// <summary>The lifetime of one invite token, from mint to expiry.</summary>
    internal static TimeSpan Default => TimeSpan.FromDays(7);
}
