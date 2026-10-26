using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Contracts;

/// <summary>How a presented session cookie resolved (02.3.2 §2, D2).</summary>
/// <remarks>
/// The order is the precedence the request path applies: an unknown value is answered first, then a revoked
/// row, then either expiry, then the account's state. The numbers are stable — nothing may be reordered.
/// </remarks>
public enum SessionAuthStatus
{
    /// <summary>The session is live and the account is active: the request may run.</summary>
    Active = 0,

    /// <summary>No session row carries this cookie (never issued, or deleted by the cleanup sweep).</summary>
    Unknown = 1,

    /// <summary>The row exists but is past its sliding or its absolute deadline.</summary>
    Expired = 2,

    /// <summary>The row exists and <c>revoked_at</c> is set (logout, revoke, member removal, rotation).</summary>
    Revoked = 3,

    /// <summary>The session is live but its account is not <c>active</c>, so it may not be used.</summary>
    Disabled = 4,
}

/// <summary>The outcome of <see cref="ISessionAuthentication.AuthenticateAsync"/>.</summary>
/// <param name="Status">The outcome; only <see cref="SessionAuthStatus.Active"/> admits the request.</param>
/// <param name="SessionId">
/// The <c>session</c> row the cookie belongs to, or <see cref="Guid.Empty"/> for
/// <see cref="SessionAuthStatus.Unknown"/>.
/// </param>
/// <param name="UserId">
/// The owning <c>user</c> row, or <see cref="Guid.Empty"/> for <see cref="SessionAuthStatus.Unknown"/>. For
/// every other status it is a real id, which is what lets telemetry and audit name the account without
/// reading the table.
/// </param>
/// <param name="RotationRequired">
/// True when a privilege change flagged this session (02.3.2 D5b): the caller MUST rotate it before the
/// request is served. Always false unless <paramref name="Status"/> is <see cref="SessionAuthStatus.Active"/>.
/// </param>
public sealed record SessionAuthResult(
    SessionAuthStatus Status,
    Guid SessionId,
    Guid UserId,
    bool RotationRequired);

/// <summary>
/// The cookie-verification half of the session lifecycle (02.3.2 §5, §8; AC2, AC3, AC4, AC8): one call
/// answers "which session is this, and may it be used?", and applies the two writes that belong on the
/// request path (the throttled idle slide and the lazy pepper re-hash).
/// </summary>
/// <remarks>
/// <para>
/// <b>Implemented and registered.</b> <c>SessionAuthenticationStore</c> implements this contract over the
/// <c>SECURITY DEFINER</c> <c>evidgate_resolve.session_authenticate</c> function of migration 0045 — one
/// statement per authenticated request (P1/P3) that does the lookup, the status decision and any lifecycle
/// write in the database, so the cookie's HMAC is never compared in application code and there is no timing
/// side channel to exploit.
/// </para>
/// <para>
/// Time comes from the host's <see cref="TimeProvider"/>, bounded by the configured lifetimes; a test moves
/// it instead of waiting. Nothing here logs or returns a cookie value, an address or a user agent.
/// </para>
/// </remarks>
public interface ISessionAuthentication
{
    /// <summary>
    /// Resolves the presented <c>__Host-</c> cookie value and slides the session when it is due.
    /// </summary>
    /// <param name="cookieValue">
    /// The raw cookie value from the request, or null/empty when the request carried none. A value longer
    /// than any value this server mints is answered <see cref="SessionAuthStatus.Unknown"/> without a
    /// database round trip.
    /// </param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>
    /// The outcome, or a 503 <c>unavailable</c> failure when the deployment has no session pepper or the
    /// database refused the statement. Every session state — including an unknown cookie — is a SUCCESS
    /// value: it is the caller's job to render 401/403 from <see cref="SessionAuthResult.Status"/>.
    /// </returns>
    ValueTask<Result<SessionAuthResult>> AuthenticateAsync(string? cookieValue, CancellationToken cancellationToken);
}
