namespace Evidgate.BuildingBlocks.Pipeline.Authorization;

/// <summary>
/// A caller a credential authenticator vouches for (02.6.2 §5). The principal says WHO;
/// the bound org and project are what a token's claims fix (service tokens), and stay
/// null for a member, whose org is chosen per request and checked against membership.
/// </summary>
/// <param name="Principal">The authenticated principal.</param>
/// <param name="BoundOrgId">The org a service token is minted for.</param>
/// <param name="BoundProjectId">The project a service token is minted for.</param>
public sealed record AuthenticatedCaller(IRequestPrincipal Principal, Guid? BoundOrgId = null, Guid? BoundProjectId = null);

/// <summary>The parts of an HTTP request an authenticator may read, without tying it to ASP.NET types.</summary>
public interface ICallerRequest
{
    /// <summary>The first value of a request header, or null.</summary>
    /// <param name="name">Header name.</param>
    string? Header(string name);
}

/// <summary>
/// Turns the request's credential (session cookie 02.3.2, <c>ci_producer</c> bearer 02.4.x)
/// into an <see cref="AuthenticatedCaller"/>. The host asks every registered authenticator in
/// turn; a host with none authenticates nobody and every tenant route answers 401.
/// </summary>
public interface ICallerAuthenticator
{
    /// <summary>Returns the caller, or null when this authenticator does not recognise the credential.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<AuthenticatedCaller?> AuthenticateAsync(ICallerRequest request, CancellationToken cancellationToken);
}
