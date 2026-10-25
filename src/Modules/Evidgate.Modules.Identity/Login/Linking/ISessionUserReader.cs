using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>
/// Which user a cookie-authenticated request belongs to (02.3.1-T2.2, Q4): the read-only half of the
/// session path that the pre-tenant linking routes need, because they run before any tenant exists and hold
/// no principal yet.
/// </summary>
/// <remarks>
/// <para>
/// The implementation hashes the presented <c>__Host-evidgate_session</c> value with the session pepper
/// exactly as the session store does and asks <c>evidgate_resolve.session_user</c> (migration 0044), which is
/// <c>stable</c> and writes nothing: sliding expiry, rotation and revocation stay 02.3.2-T1's writes on its
/// own path. <b>Provisional:</b> 02.3.2-T1 replaces this seam with the deployment's real authenticator
/// (the session principal of every other route), and the linking routes then resolve the same user from it
/// instead.
/// </para>
/// <para>
/// <b>Fail closed.</b> An absent, unknown, tampered, expired or revoked cookie resolves to nothing, and so
/// does a session whose account was disabled after it was minted — the resolver filters
/// <c>user.status = 'active'</c>, so <em>disabled</em> and <em>no session</em> are indistinguishable here and
/// both answer 401 <c>unauthenticated</c>. Nothing in this seam reveals whether a value ever was a session.
/// A missing pepper or a database fault is 503 <c>unavailable</c>: an unverifiable credential is never
/// treated as an anonymous caller.
/// </para>
/// </remarks>
internal interface ISessionUserReader
{
    /// <summary>Resolves the user <paramref name="cookieValue"/> belongs to.</summary>
    /// <param name="cookieValue">The raw <c>__Host-evidgate_session</c> value the request carried, or null.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// Success with the <c>user.id</c>, or a failure rendered as the 00 §9.2 body: 401
    /// <c>unauthenticated</c> for every unusable cookie, 503 <c>unavailable</c> for a deployment with no
    /// pepper and for a database fault.
    /// </returns>
    ValueTask<Result<Guid>> ResolveAsync(string? cookieValue, CancellationToken cancellationToken);
}
