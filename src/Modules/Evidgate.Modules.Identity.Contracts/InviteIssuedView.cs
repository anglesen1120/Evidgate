namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The answer of <c>POST /v1/orgs/{o}/memberships</c> (201) and of
/// <c>POST /v1/orgs/{o}/memberships/{m}/resend</c> (202) when a fresh invite token was minted
/// (02.1.2-T2 §5, T75): every field of <see cref="MembershipView"/> plus the one-time
/// <see cref="InviteToken"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shown once.</b> The token is the plaintext <c>evgi_&lt;prefix&gt;_&lt;secret&gt;</c> the invite
/// email carries; the database keeps only <c>HMAC-SHA256(pepper_vN, secret)</c> beside the public
/// prefix, so this response is the only moment a human can read it. Nothing logs it.
/// </para>
/// <para>
/// <b>Absent on a replay.</b> The token travels outside the command's result
/// (<c>InviteSecretCarrier</c>), so the <c>Idempotency-Key</c> response body persisted by
/// <c>IdempotencyBehaviour</c> never contains it: a replay answers the same status, <c>ETag</c> and
/// membership with <see cref="InviteToken"/> null. Minting a new token is what
/// <c>…/{m}/resend</c> is for. <c>invite_token</c> is therefore <c>null</c>-able in the schema and
/// <c>x-sensitive</c>.
/// </para>
/// </remarks>
/// <param name="Id">The membership's UUID (application-generated, 00 §7).</param>
/// <param name="OrgId">The owning org; always the tenant scope the write ran in.</param>
/// <param name="UserId">Null while the invite is <c>pending</c>; set when a login binds it (02.3.1-T2).</param>
/// <param name="InvitedEmail">The normalised invite address (lowercased, trimmed).</param>
/// <param name="Status">One of <see cref="MembershipStatus"/>; <c>pending</c> on both routes.</param>
/// <param name="Roles">The membership's roles, in <c>(created_at, id)</c> order.</param>
/// <param name="CreatedAt">Creation instant — the keyset's leading column (00 §9.1).</param>
/// <param name="UpdatedAt">Last write instant (00 §7).</param>
/// <param name="Version">Optimistic-concurrency token, the <c>ETag</c> of this row (00 §5.5 C4, §9.1).</param>
/// <param name="InviteToken">
/// The plaintext invite link token, present only on the response that minted it (JSON
/// <c>invite_token</c>); null on an <c>Idempotency-Key</c> replay.
/// </param>
public sealed record InviteIssuedView(
    Guid Id,
    Guid OrgId,
    Guid? UserId,
    string InvitedEmail,
    string Status,
    IReadOnlyList<RoleView> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    string? InviteToken)
{
    /// <summary>Projects a read row and the freshly minted token into the wire shape.</summary>
    /// <param name="membership">The membership as <c>MembershipReader</c> returned it inside the transaction.</param>
    /// <param name="inviteToken">The plaintext token this response mints, or null on a replay.</param>
    public static InviteIssuedView From(MembershipView membership, string? inviteToken)
    {
        ArgumentNullException.ThrowIfNull(membership);

        return new InviteIssuedView(
            membership.Id,
            membership.OrgId,
            membership.UserId,
            membership.InvitedEmail,
            membership.Status,
            membership.Roles,
            membership.CreatedAt,
            membership.UpdatedAt,
            membership.Version,
            inviteToken);
    }
}
