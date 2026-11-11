using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>How <c>evidgate_resolve.login_link_identity</c> answered one explicit confirm (02.3.1-T2.2, Q4).</summary>
internal enum LinkResult
{
    /// <summary>The identity row was inserted on the confirming user.</summary>
    Linked,

    /// <summary>The identity was already on the confirming user: nothing was written, and this is the retry's answer.</summary>
    AlreadyLinked,

    /// <summary>The identity sat on a user with no membership and no live session and was moved to the confirming user.</summary>
    Moved,

    /// <summary>The identity belongs to another user that still holds a membership or a live session: never taken (AC6).</summary>
    Conflict,

    /// <summary>The confirming user is <c>disabled</c>: <c>user.status</c> changed after the session check.</summary>
    Disabled,

    /// <summary>
    /// No live session of an active account matches the presented cookie (0045, D2/D8): the cookie is unknown,
    /// expired or revoked, or the session died between the route's own check and this call. The function does
    /// not distinguish the three, so neither does this outcome; the route answers the same 401
    /// <c>unauthenticated</c> the request authenticator would have.
    /// </summary>
    Unauthenticated,

    /// <summary>
    /// An outcome — or a shape — this build does not know. Fails closed as 503 <c>unavailable</c> rather than
    /// guessed at, exactly like the session store's unknown status.
    /// </summary>
    Unrecognised,
}

/// <summary>The answer of one link attempt.</summary>
/// <param name="Result">The outcome.</param>
/// <param name="MovedFromUserId">The user the identity was taken from, set exactly when <see cref="Result"/> is
/// <see cref="LinkResult.Moved"/>; null otherwise. The <c>auth.link_move</c> row's own <c>previous_id</c> is
/// written by the definer that performs the move, in the same transaction, so this value is the outcome's
/// description rather than something a caller audits.</param>
internal sealed record LinkOutcome(LinkResult Result, Guid? MovedFromUserId);

/// <summary>
/// The only writer of a second-provider link (02.3.1-T2.2, Q4): the app role's way into
/// <c>evidgate_resolve.login_link_identity</c> (migration 0045; 0044 introduced it), which is the one statement
/// that decides <c>linked</c> / <c>already_linked</c> / <c>moved</c> / <c>conflict</c> and refuses to steal an
/// identity from an account that is still in use.
/// </summary>
/// <remarks>
/// <para>
/// The decision lives in the database on purpose (AC6): the function locks the target user and the identity
/// row, so two concurrent confirms of the same <c>(provider, subject)</c> end with one row and one
/// <c>already_linked</c>, and the "never auto-merge" rule holds even for a caller that skips this class.
/// This seam therefore adds no rule of its own and never writes <c>user</c>, <c>user.email</c> or
/// <c>membership</c>.
/// </para>
/// <para>
/// <b>The target account is the database's to resolve (02.3.2-T1, D8 / residual SEC2).</b> The function takes
/// the presented cookie's HMAC candidates and resolves the live session and its active user itself, so a caller
/// running as <c>evidgate_app</c> can no longer name an arbitrary user id to attach an identity to. This seam
/// therefore passes the cookie value, never a user id, and a cookie no live session matches is the
/// <see cref="LinkResult.Unauthenticated"/> business answer.
/// </para>
/// <para>
/// One call, no transaction of its own, on the app-role data source, and never inside an open tenant scope:
/// the confirm route runs pre-tenant and the function is owner-scoped. The identity id is app-generated UUIDv7
/// (00 §7), and the time is the deployment's <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
internal interface ILinkIdentityStore
{
    /// <summary>
    /// Attaches the provider identity to the account the authenticated session belongs to, or answers why it
    /// cannot. A first-time link or move also appends <c>auth.link</c> (and <c>auth.link_move</c>) to the chain of
    /// every active org of the account in the same transaction: either all of it commits or none.
    /// </summary>
    /// <param name="sessionProof">
    /// The HMAC the deployment's session authenticator computed for the LIVE session's cookie value
    /// (<c>SessionRequestOutcome.GetSessionProof()</c>) — a hash, never the raw cookie. After an in-request lazy
    /// rotation it is the hash of the newly issued cookie, which is the only value the function can still match;
    /// null or empty is <see cref="LinkResult.Unauthenticated"/> without a database call.
    /// </param>
    /// <param name="provider">The provider's registered name.</param>
    /// <param name="subject">The provider's verified subject for this identity.</param>
    /// <param name="email">The verified address the provider reported (stored as <c>email_at_provider</c>).</param>
    /// <param name="requestId">The caller's correlation request id, or null; the definer keeps it only when it is
    /// 1–128 visible ASCII characters, so the audit row never carries unvalidated text.</param>
    /// <param name="traceId">The W3C trace id of the call, or null; the definer nulls an invalid value.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// Success with the <see cref="LinkOutcome"/> — the business answers, including the refusals — or a failure
    /// rendered as the 00 §9.2 body: 503 <c>unavailable</c> for a database fault or an unrecognised outcome.
    /// </returns>
    /// <remarks>
    /// There is no profile-name parameter: a second provider's display name must not rename an account whose name
    /// the login upsert of 0042 owns, so <c>login_link_identity</c> has no argument for it and this seam does not
    /// pretend to pass one.
    /// </remarks>
    ValueTask<Result<LinkOutcome>> LinkAsync(
        byte[]? sessionProof,
        string provider,
        string subject,
        string email,
        string? requestId,
        string? traceId,
        CancellationToken cancellationToken);
}
