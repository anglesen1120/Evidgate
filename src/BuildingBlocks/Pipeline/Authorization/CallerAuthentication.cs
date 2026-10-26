using Evidgate.BuildingBlocks.SharedKernel.Results;

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

/// <summary>
/// An authenticator that can also say WHY it refused a credential (02.3.2-T1, D2). Authentication alone
/// answers one question ("is there a caller?") and the pipeline's fail-closed default renders every
/// absence as the same generic 401, so an expired, a revoked and an unknown session would be
/// indistinguishable to the client — three states the contract distinguishes (<c>session_expired</c>,
/// <c>session_revoked</c>, <c>unauthenticated</c>).
/// </summary>
/// <remarks>
/// <para>
/// Additive on purpose: an authenticator implements this next to <see cref="ICallerAuthenticator"/>, and
/// the host asks every registered implementation for a refusal BEFORE it authenticates. A null answer means
/// "this credential is absent, or present and usable" — never "no opinion", so an implementation that holds
/// a credential it cannot accept must name the error.
/// </para>
/// <para>
/// A refusal is final: the host answers it through the 00 §9.2 envelope and runs no handler. Typed refusals
/// therefore carry the credential's own statuses (401 for a bad session, 403 <c>account_disabled</c> for a
/// disabled account, 503 <c>unavailable</c> for a store that cannot answer) and never a detail that would
/// make the route an oracle for the credential's internals.
/// </para>
/// </remarks>
public interface ICallerRefusalAuthenticator
{
    /// <summary>The typed refusal of the request's credential, or null when it is absent or usable.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<Error?> RefuseAsync(ICallerRequest request, CancellationToken cancellationToken);
}
