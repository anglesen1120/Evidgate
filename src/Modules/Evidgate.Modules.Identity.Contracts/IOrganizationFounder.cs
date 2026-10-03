namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// Makes the creator of a new organization its first member (02.1.1 AC8, TL T98): one
/// <c>active</c> membership bound to the creator's user id and verified email, holding
/// the built-in <c>tenant_admin</c> <b>and</b> <c>release_approver</c> roles, because a
/// pilot org may be one person. Called by Tenancy's <c>CreateOrganization</c> inside the
/// same tenant transaction as the <c>organization</c> insert, so either both commit or
/// neither does. The membership tables stay Identity's; Tenancy never writes them.
/// </summary>
public interface IOrganizationFounder
{
    /// <summary>
    /// Inserts the founder membership and its two role grants in the ambient tenant
    /// scope, which must already be the new org's.
    /// </summary>
    /// <param name="orgId">The org just inserted; must equal the ambient tenant.</param>
    /// <param name="userId">The creator's user id (from the request principal, never input).</param>
    /// <param name="verifiedEmail">The creator's provider-verified email (from the principal).</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The new membership's id.</returns>
    ValueTask<Guid> GrantFounderAsync(
        Guid orgId,
        Guid userId,
        string verifiedEmail,
        CancellationToken cancellationToken);
}
