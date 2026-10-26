using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Contracts;

/// <summary>The inputs of <see cref="ISessions.CreateAsync"/>.</summary>
/// <param name="UserId">The <c>user</c> row the session belongs to (from the verified identity, never input).</param>
/// <param name="IpAddress">The client address, when it is known (02 §6 minimum, never logged).</param>
/// <param name="UserAgent">The client's user agent, when it sent one (never logged).</param>
/// <param name="PredecessorCookieValue">
/// The <c>__Host-</c> cookie value the browser presented to the login callback, when it presented one: its
/// session is revoked in the same statement as the insert and becomes the new row's <c>rotated_from</c>
/// when it belongs to the same user (02.3.2 §5, D5a). A client-supplied SESSION ID is never accepted —
/// only the value of a cookie its holder could present, which is what makes session fixation impossible.
/// </param>
public sealed record SessionCreation(
    Guid UserId,
    string? IpAddress,
    string? UserAgent,
    string? PredecessorCookieValue = null);

/// <summary>The inputs of <see cref="ISessions.RotateAsync"/> (02.3.2 D5b, AC7).</summary>
/// <param name="SessionId">
/// The live session to rotate — the one the request authenticated with, never a value from the request
/// body. The rotation is single-winner: of two rotations of one session exactly one inserts a successor.
/// </param>
/// <param name="IpAddress">The new row's client address; <c>null</c> inherits the predecessor's.</param>
/// <param name="UserAgent">The new row's user agent; <c>null</c> or empty inherits the predecessor's.</param>
public sealed record SessionRotation(Guid SessionId, string? IpAddress = null, string? UserAgent = null);

/// <summary>What <see cref="ISessions.CreateAsync"/> hands back to the login flow.</summary>
/// <param name="SessionId">The new <c>session</c> row's id.</param>
/// <param name="CookieValue">The one-time <c>__Host-</c> cookie value: returned once, stored only as a hash.</param>
/// <param name="ExpiresAt">When the session's sliding lifetime ends (at most the absolute lifetime).</param>
/// <param name="AbsoluteExpiresAt">
/// The session's hard deadline: on creation it is the creation instant plus the absolute lifetime, and a
/// ROTATED session inherits the predecessor's (a rotation never extends it). The cookie writer takes the
/// <c>Max-Age</c> of a rotated cookie from here, so the browser keeps the credential exactly as long as the row
/// can live; <c>default</c> means the caller has no better value than <paramref name="ExpiresAt"/>.
/// </param>
public sealed record SessionTicket(
    Guid SessionId,
    string CookieValue,
    DateTimeOffset ExpiresAt,
    DateTimeOffset AbsoluteExpiresAt = default);

/// <summary>
/// The session-lifecycle seam of 02.3.1 §7 / 02.3.2-T1: the login flow verifies who the caller is, and this
/// contract is the only thing that turns that identity into a live session, and a live session into a new
/// one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implemented and registered.</b> <c>SessionStore</c> (module <c>Evidgate.Modules.Identity.Sessions</c>)
/// implements this contract and <c>AddSessions</c> registers it, so a host that composes federated login has
/// exactly one implementation; 02.3.1-T1.4's <c>CompleteLoginCommand</c> is its first caller, reached by the
/// login callback through <c>Login.ILoginCompletion</c>, and 02.3.2-T1.2's request authenticator is the
/// caller of <see cref="RotateAsync"/>.
/// </para>
/// <para>
/// The implementation writes the platform-scoped <c>session</c> row (no <c>org_id</c>, D12) with
/// <c>created_at</c>/<c>last_seen_at</c> defaulted to now, a sliding <c>expires_at</c> and an
/// <c>absolute_expires_at</c> that is never earlier than it (the table's check constraint). Because the app
/// role holds no privilege on <c>user</c>/<c>user_identity</c>/<c>session</c>, the write is an
/// <c>evidgate_app</c> EXECUTE of the <c>SECURITY DEFINER</c> <c>evidgate_resolve.session_create</c> /
/// <c>evidgate_resolve.session_rotate</c> functions of migration 0045 — never a direct <c>evidgate_app</c>
/// insert and never an owner connection. <see cref="SessionTicket.CookieValue"/> is returned once and stored
/// only as HMAC-SHA256 of the configured pepper, beside that pepper's version (<c>session.cookie_hmac</c>,
/// <c>session.pepper_version</c>) — the raw value never reaches the database.
/// </para>
/// <para>
/// 02.3.2-T1 adds the request path on top of these two writers (cookie verification, idle and absolute
/// expiry, revocation) through <see cref="ISessionAuthentication"/>; the write side is this contract.
/// </para>
/// </remarks>
public interface ISessions
{
    /// <summary>Creates one session for <see cref="SessionCreation.UserId"/> and returns its ticket.</summary>
    /// <param name="request">The user, the client metadata to store and the forwarded cookie, if any.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// The ticket, or a failure the caller renders as the 00 §9.2 envelope (a disabled user is 403
    /// <c>account_disabled</c>; a write that cannot be scoped is 503 <c>unavailable</c>).
    /// </returns>
    ValueTask<Result<SessionTicket>> CreateAsync(SessionCreation request, CancellationToken cancellationToken);

    /// <summary>
    /// Rotates <see cref="SessionRotation.SessionId"/> (02.3.2 D5b, AC7): revokes it and inserts its
    /// successor with <c>rotated_from</c> set, in one atomic statement. The predecessor keeps its
    /// <c>absolute_expires_at</c> — a rotation never extends the hard deadline — and the successor's sliding
    /// deadline restarts from now.
    /// </summary>
    /// <param name="request">The session to rotate and the client metadata of the successor.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// The new ticket (its <see cref="SessionTicket.CookieValue"/> must be set as the browser's cookie), or a
    /// failure the caller renders: 401 <c>session_revoked</c> when the session is not live any more
    /// (revoked, expired, or the loser of two concurrent rotations — the old cookie is dead either way),
    /// 403 <c>account_disabled</c>, 503 <c>unavailable</c> when the pepper is missing or the write failed.
    /// </returns>
    ValueTask<Result<SessionTicket>> RotateAsync(SessionRotation request, CancellationToken cancellationToken);
}
