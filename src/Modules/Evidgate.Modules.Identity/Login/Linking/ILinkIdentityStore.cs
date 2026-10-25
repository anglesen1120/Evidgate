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

    /// <summary>The confirming user does not exist (its account was removed between the session check and this call).</summary>
    UnknownUser,

    /// <summary>The confirming user is <c>disabled</c>: <c>user.status</c> changed after the session check.</summary>
    Disabled,

    /// <summary>
    /// An outcome — or a shape — this build does not know. Fails closed as 503 <c>unavailable</c> rather than
    /// guessed at, exactly like the session store's unknown status.
    /// </summary>
    Unrecognised,
}

/// <summary>The answer of one link attempt.</summary>
/// <param name="Result">The outcome.</param>
/// <param name="MovedFromUserId">The user the identity was taken from, set exactly when <see cref="Result"/> is
/// <see cref="LinkResult.Moved"/>; null otherwise. It is the <c>previous_id</c> of the <c>auth.link_move</c> audit row.</param>
internal sealed record LinkOutcome(LinkResult Result, Guid? MovedFromUserId);

/// <summary>
/// The only writer of a second-provider link (02.3.1-T2.2, Q4): the app role's way into
/// <c>evidgate_resolve.login_link_identity</c> (migration 0044), which is the one statement that decides
/// <c>linked</c> / <c>already_linked</c> / <c>moved</c> / <c>conflict</c> and refuses to steal an identity from
/// an account that is still in use.
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
/// One call, no transaction of its own, on the app-role data source, and never inside an open tenant scope:
/// the confirm route runs pre-tenant and the function is owner-scoped. The two ids are app-generated UUIDv7
/// (00 §7).
/// </para>
/// </remarks>
internal interface ILinkIdentityStore
{
    /// <summary>Attaches the provider identity to <paramref name="userId"/>, or answers why it cannot.</summary>
    /// <param name="userId">The confirming user (resolved from the session cookie, never from the body).</param>
    /// <param name="provider">The provider's registered name.</param>
    /// <param name="subject">The provider's verified subject for this identity.</param>
    /// <param name="email">The verified address the provider reported (stored as <c>email_at_provider</c>).</param>
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
        Guid userId,
        string provider,
        string subject,
        string email,
        CancellationToken cancellationToken);
}
