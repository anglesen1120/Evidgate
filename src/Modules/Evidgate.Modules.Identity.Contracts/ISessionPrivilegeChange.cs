namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The privilege-change hook of 02.3.2-T1 (D5b, AC7): a change to what a user may do must not leave the
/// sessions that were opened under the old rights running with them. The role-change command calls this
/// inside its own transaction, so a rolled-back change also rolls the marking back.
/// </summary>
/// <remarks>
/// The marking is not the rotation: the next request with each flagged session rotates it (a new session id,
/// the old row revoked, <c>rotated_from</c> set) through <see cref="ISessions.RotateAsync"/>, which keeps the
/// browser's cookie fresh without a re-login and without a second credential.
/// </remarks>
public interface ISessionPrivilegeChange
{
    /// <summary>Flags every live session of <paramref name="userId"/> for rotation at its next request.</summary>
    /// <param name="userId">The user whose roles or permissions changed.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>
    /// The number of sessions flagged — every unrevoked, unexpired session of the user, an already-flagged
    /// one included (the flag carries the latest privilege change). Zero is a normal answer: a user who is
    /// not signed in has none.
    /// </returns>
    ValueTask<int> RequireRotationForUserAsync(Guid userId, CancellationToken cancellationToken);
}
