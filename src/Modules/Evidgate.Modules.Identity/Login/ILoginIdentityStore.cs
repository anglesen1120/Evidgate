namespace Evidgate.Modules.Identity.Login;

/// <summary>The platform <c>user</c> a verified identity resolved to (02.3.1-T1.4).</summary>
/// <param name="UserId">The <c>user.id</c> the identity is bound to.</param>
/// <param name="IsActive">False when <c>user.status</c> is <c>disabled</c> (login is then refused: 403 <c>account_disabled</c>).</param>
internal sealed record LoginUser(Guid UserId, bool IsActive);

/// <summary>
/// The only writer of <c>user</c> and <c>user_identity</c> (02.3.1 §5): upserts on <c>(provider, subject)</c>
/// through the owner-scoped <c>evidgate_resolve</c> definer functions of migration 0042 (the app role holds no
/// privilege on the platform tables, 0041). Runs on the app-role data source, outside any tenant scope and
/// never inside a transaction that spans an outbound call (P5).
/// </summary>
internal interface ILoginIdentityStore
{
    /// <summary>
    /// Finds the user bound to <c>(identity.Provider, identity.Subject)</c> or creates the <c>user</c> and its
    /// <c>user_identity</c> in one statement, replay-safe and race-safe by the unique key. A later login of an
    /// active user refreshes <c>last_login_at</c>, <c>email_at_provider</c> and <c>display_name</c>; it never
    /// rewrites <c>user.email</c> and never merges on email (D3, AC6).
    /// </summary>
    /// <param name="identity">A provider-verified identity (<see cref="VerifiedIdentity.EmailVerified"/> is true).</param>
    /// <param name="cancellationToken">Request abort.</param>
    ValueTask<LoginUser> UpsertAsync(VerifiedIdentity identity, CancellationToken cancellationToken);
}
