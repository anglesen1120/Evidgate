using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Contracts;

/// <summary>The inputs of <see cref="ISessions.CreateAsync"/>.</summary>
/// <param name="UserId">The <c>user</c> row the session belongs to (from the verified identity, never input).</param>
/// <param name="IpAddress">The client address, when it is known (02 §6 minimum, never logged).</param>
/// <param name="UserAgent">The client's user agent, when it sent one (never logged).</param>
public sealed record SessionCreation(Guid UserId, string? IpAddress, string? UserAgent);

/// <summary>What <see cref="ISessions.CreateAsync"/> hands back to the login flow.</summary>
/// <param name="SessionId">The new <c>session</c> row's id.</param>
/// <param name="CookieValue">The one-time <c>__Host-</c> cookie value: returned once, stored only as a hash.</param>
/// <param name="ExpiresAt">When the session's sliding lifetime ends (at most the absolute lifetime).</param>
public sealed record SessionTicket(Guid SessionId, string CookieValue, DateTimeOffset ExpiresAt);

/// <summary>
/// The session-creation seam of 02.3.1 §7 / 02.3.2-T1: the login flow verifies who the caller is, and this
/// contract is the only thing that turns that identity into a live session.
/// </summary>
/// <remarks>
/// <para>
/// <b>It replaces nothing yet.</b> There is no implementation and no DI registration in this build:
/// 02.3.2-T1 implements it over the <c>session</c> table (created by 02.3.1-T1.1's migration) and
/// 02.3.1-T1.4's <c>CompleteLoginCommand</c> is its first caller, through
/// <c>Login.ILoginCompletion</c>. Until then the login callback answers 501 <c>login_not_completed</c> and
/// no code resolves this interface.
/// </para>
/// <para>
/// The implementation writes the platform-scoped <c>session</c> row (no <c>org_id</c>, D12) with
/// <c>created_at</c>/<c>last_seen_at</c> defaulted to now, a sliding <c>expires_at</c> and an
/// <c>absolute_expires_at</c> that is never earlier than it (the table's check constraint), and it must
/// run in a scope that is allowed to write that table: the app role holds no privilege on
/// <c>user</c>/<c>user_identity</c>/<c>session</c>, so the write goes through the owner-scoped completion
/// path that 02.3.2-T1 defines, never through an <c>evidgate_app</c> insert.
/// </para>
/// </remarks>
public interface ISessions
{
    /// <summary>Creates one session for <see cref="SessionCreation.UserId"/> and returns its ticket.</summary>
    /// <param name="request">The user and the client metadata to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// The ticket, or a failure the caller renders as the 00 §9.2 envelope (a disabled user is 403
    /// <c>account_disabled</c>; a write that cannot be scoped is 503 <c>unavailable</c>).
    /// </returns>
    ValueTask<Result<SessionTicket>> CreateAsync(SessionCreation request, CancellationToken cancellationToken);
}
