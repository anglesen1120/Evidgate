namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// What a federated login hands the invite binder (02.1.2-T2, T75; called by
/// 02.3.1-T2 after the provider callback has been validated). Everything here is
/// already authenticated: the user row exists, and <paramref name="EmailVerified"/>
/// reports the provider's own verdict on <paramref name="VerifiedEmail"/> — a
/// provider assertion never binds anything by itself.
/// </summary>
/// <param name="UserId">The signed-in user, as <c>user.id</c>.</param>
/// <param name="Provider">The identity provider that authenticated the user (<c>google</c>, <c>github</c>) — only an opt-in auto-bind reads it.</param>
/// <param name="VerifiedEmail">The address the provider reported, in the provider's case; it is normalised before any comparison.</param>
/// <param name="EmailVerified">The provider's verified flag. False means nothing is queried at all.</param>
/// <param name="InviteToken">
/// The invite link token the user presented, or null. It is the secret half that
/// makes an invite single-use: the binder never stores or logs it, and an
/// unparseable value is skipped like an absent one.
/// </param>
/// <param name="HostedDomain">
/// For a Google Workspace account, the <c>hd</c> claim, or null. Only the org's
/// opt-in auto-bind reads it, and only when its domain part matches the verified
/// address.
/// </param>
public sealed record BindPendingMembershipsRequest(
    Guid UserId,
    string Provider,
    string VerifiedEmail,
    bool EmailVerified,
    string? InviteToken,
    string? HostedDomain);
