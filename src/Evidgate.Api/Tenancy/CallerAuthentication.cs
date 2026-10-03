using Evidgate.BuildingBlocks.Pipeline.Authorization;

namespace Evidgate.Api.Tenancy;

/// <summary>
/// A caller a credential authenticator vouches for (02.6.2 §5). The principal says WHO;
/// the bound org and project are what a token's claims fix (service tokens), and stay
/// null for a member, whose org is chosen per request and checked against membership.
/// </summary>
/// <param name="Principal">The authenticated principal.</param>
/// <param name="BoundOrgId">The org a service token is minted for.</param>
/// <param name="BoundProjectId">The project a service token is minted for.</param>
public sealed record AuthenticatedCaller(IRequestPrincipal Principal, Guid? BoundOrgId = null, Guid? BoundProjectId = null);

/// <summary>
/// Turns the request's credential (session cookie 02.3.2, bearer or ingest token 02.4.x)
/// into an <see cref="AuthenticatedCaller"/>. No production authenticator exists before
/// those stories land, so a deployed host authenticates nobody and every tenant route
/// answers 401; the seam is what they plug into.
/// </summary>
public interface ICallerAuthenticator
{
    /// <summary>Returns the caller, or null when this authenticator does not recognise the credential.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AuthenticatedCaller?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken);
}
