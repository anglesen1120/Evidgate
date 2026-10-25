namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// A pending membership of one org, as the identity directory reports it
/// (02.1.2-T1.3). The binding path of 02.3.1-T2 uses it after the invite secret has
/// resolved <c>(org_id, membership_id)</c> pre-tenant through
/// <c>evidgate_resolve.invite_by_secret</c>: the login then opens the tenant scope and
/// re-reads the row here, under RLS, before comparing the provider-verified email with
/// <see cref="InvitedEmail"/> and verifying the presented secret against the stored
/// hash (T75).
/// </summary>
/// <param name="MembershipId">The membership's UUID.</param>
/// <param name="OrgId">The org the invite belongs to.</param>
/// <param name="InvitedEmail">The normalised invite address the verified email must equal.</param>
/// <param name="Status">The row's status; only <see cref="MembershipStatus.Pending"/> rows can bind.</param>
/// <param name="InviteExpiresAt">
/// When the invite stops being bindable — set when 02.1.2-T2's create/resend mints the
/// token (migration 0043 makes it non-null exactly then), null on a pending row that
/// carries no token.
/// </param>
public sealed record PendingInvite(
    Guid MembershipId,
    Guid OrgId,
    string InvitedEmail,
    string Status,
    DateTimeOffset? InviteExpiresAt);
