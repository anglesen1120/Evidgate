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
/// the 00 §9.2 envelope, so a refusal (an unknown user, an inactive membership, a disabled account) is a
/// typed HTTP answer rather than a silently issued session.
/// </para>
/// <para>
/// <b>Hand-over, not a stub.</b> This build registers
/// <see cref="LoginNotCompletedCompletion"/>, the V1 default, which answers 501 <c>login_not_completed</c>.
/// 02.3.1-T1.4 replaces that registration with its <c>CompleteLoginCommand</c> (user upsert on
/// <c>(provider, subject)</c>, the <c>session</c> row through <see cref="Contracts.ISessions"/>, the
/// <c>__Host-</c> session cookie and the redirect). Nothing is registered for this seam anywhere else, so
/// the replacement is one line in <c>AddFederatedLogin</c>'s registration list.
/// </para>
/// <para>
/// The implementation runs AFTER every provider call and touches the database itself: the callback opens
/// no transaction (P5), so an implementation that writes must open its own scope.
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
    /// Success with the redirect target (an app path such as <c>/runs/42</c>), or a failure whose
    /// <see cref="Error"/> is rendered as the 00 §9.2 body — 403 <c>no_membership</c> and 403
    /// <c>account_disabled</c> are the 02.3.1 completion's own answers (AC3).
    /// </returns>
    ValueTask<Result<string>> CompleteAsync(LoginCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>Inputs of <see cref="ILoginCompletion.CompleteAsync"/>.</summary>
/// <param name="Identity">The provider-verified identity; <c>(Provider, Subject)</c> is the only key (D3).</param>
/// <param name="ReturnUrl">The allowlisted app path to land on after a successful login.</param>
/// <param name="ClientIpAddress">The client address, for the session row (02 §6 minimum); never logged.</param>
/// <param name="UserAgent">The client's user agent, for the session row; never logged.</param>
/// <param name="HttpContext">The callback's response, which the session cookie is written on.</param>
internal sealed record LoginCompletionRequest(
    VerifiedIdentity Identity,
    string ReturnUrl,
    string? ClientIpAddress,
    string? UserAgent,
    HttpContext HttpContext);

/// <summary>
/// The V1 default of <see cref="ILoginCompletion"/>: a verified identity is NOT a session yet, so the
/// answer is the typed 501 <c>login_not_completed</c> (02.3.1-T1.4 supplies the real completion). This is
/// deliberate: answering 200 here would report a sign-in that never happened.
/// </summary>
internal sealed class LoginNotCompletedCompletion : ILoginCompletion
{
    /// <inheritdoc />
    public ValueTask<Result<string>> CompleteAsync(LoginCompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The route counts the attempt from this Result's Error.Code (one recording point per callback), so
        // the seam itself records nothing.
        return ValueTask.FromResult(Result.Failure<string>(LoginErrors.LoginNotCompleted));
    }
}
