using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The hand-over from the login PROTOCOL to the login's BUSINESS completion (02.3.1-T1.1 routes,
/// 02.3.1-T1.4 completion): the callback has verified the identity, allowlisted the <c>returnUrl</c> and
/// consumed the one-time <c>state</c>; what happens next — user upsert, session insert, the
/// <c>__Host-</c> session cookie and the redirect — belongs to this seam and never to a provider or to the
/// protocol code.
/// </summary>
/// <remarks>
/// <para>
/// The protocol calls this seam exactly once per verified callback and maps its <see cref="Result{T}"/> to
/// the 00 §9.2 envelope, so a refusal (an inactive membership, a disabled account, a database outage) is a
/// typed HTTP answer rather than a silently issued session.
/// </para>
/// <para>
/// <b>The implementation.</b> <c>AddFederatedLogin</c> registers
/// <see cref="Completion.LoginCommandCompletion"/>, which sends
/// <see cref="Completion.CompleteLoginCommand"/> (user upsert on <c>(provider, subject)</c>, the active-membership
/// check through <c>evidgate_resolve.member_orgs</c>, the <c>session</c> row through
/// <see cref="Contracts.ISessions"/>) and writes the <c>__Host-evidgate_session</c> cookie from the ticket.
/// Nothing else registers this seam, so a host composes it with one line and a test substitutes it with one.
/// </para>
/// <para>
/// The implementation runs AFTER every provider call and touches the database itself: the callback opens
/// no transaction (P5), so an implementation that writes owns its own statements.
/// </para>
/// </remarks>
internal interface ILoginCompletion
{
    /// <summary>
    /// Completes the login for a verified identity and answers where the browser goes next.
    /// </summary>
    /// <param name="request">The verified identity, the allowlisted <c>returnUrl</c>, the client's address
    /// and user agent (for the session row) and the response the session cookie is written on.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// Success with the redirect target (an app path such as <c>/runs/42</c>, <c>Location</c>-safe), or a
    /// failure whose <see cref="Error"/> is rendered as the 00 §9.2 body — 403 <c>no_membership</c> and 403
    /// <c>account_disabled</c> are the 02.3.1 completion's own answers (AC3), and 503 <c>unavailable</c> with
    /// <c>Retry-After</c> is a database outage on this path.
    /// </returns>
    ValueTask<Result<string>> CompleteAsync(LoginCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>Inputs of <see cref="ILoginCompletion.CompleteAsync"/>.</summary>
/// <param name="Identity">The provider-verified identity; <c>(Provider, Subject)</c> is the only key (D3).</param>
/// <param name="ReturnUrl">The allowlisted app path to land on after a successful login.</param>
/// <param name="ClientIpAddress">The client address as the request reported it, for the session row (02 §6
/// minimum); never logged, and its IPv6 zone index is stripped before the write.</param>
/// <param name="UserAgent">The client's user agent, already bounded by the callback, for the session row;
/// never logged.</param>
/// <param name="HttpContext">The callback's response, which the session cookie is written on.</param>
/// <param name="InviteToken">
/// The invite token the browser staged before the login (<c>POST /v1/auth/invite-token</c>, 02.3.1-T2 Q1), or
/// null when it staged none. It reaches this record once, from the integrity-protected
/// <c>__Host-evidgate_invite</c> cookie the callback deletes on every outcome, and it must never be stored on
/// the session row, written to a log or a metric, put in an error body or copied into the <c>returnUrl</c>.
/// </param>
/// <param name="LinkUserId">
/// The account an explicit second-provider link was started for (02.3.1-T2 Q4), or null for a sign-in. It is
/// only ever set on a state cookie whose purpose is <c>link</c>, and the callback refuses such a state
/// unless the link seam is composed.
/// </param>
internal sealed record LoginCompletionRequest(
    VerifiedIdentity Identity,
    string ReturnUrl,
    string? ClientIpAddress,
    string? UserAgent,
    HttpContext HttpContext,
    string? InviteToken = null,
    Guid? LinkUserId = null);
