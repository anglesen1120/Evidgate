using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// The real login completion: sends <see cref="CompleteLoginCommand"/>, writes the
/// <c>__Host-evidgate_session</c> cookie and the antiforgery pair from the ticket it answers, and hands the redirect
/// target back to the protocol.
/// </summary>
/// <remarks>
/// <para>
/// This class is the only place the login seam meets HTTP: the command is HTTP-free (it is the business order of
/// the login's own writes), and the protocol route above it stays a browser-flow component. The seam answers a
/// <see cref="Result{T}"/> either way and never a 200, so a verified identity can never be reported as a
/// signed-in one.
/// </para>
/// <para>
/// The cookie pair is written only after the session row exists, and the value the browser PRESENTED to the
/// callback travels as <see cref="CompleteLoginCommand.PredecessorCookieValue"/>, which is how a sign-in replaces
/// the session the browser was carrying before it. A database or audit fault is the shared 503
/// <c>unavailable</c> with <c>Retry-After</c> (<see cref="LoginSessionCompletion"/>): no database text, no SQL,
/// no host name ever reaches the body.
/// </para>
/// </remarks>
/// <param name="handler">The command's handler (ordered writes, typed refusals).</param>
/// <param name="completion">The HTTP half both sign-in seams share (cookie pair, outage answer).</param>
internal sealed class LoginCommandCompletion(
    CompleteLoginCommandHandler handler,
    LoginSessionCompletion completion)
    : ILoginCompletion
{
    /// <inheritdoc />
    public async ValueTask<Result<string>> CompleteAsync(
        LoginCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issued = await completion
            .CompleteAsync(
                token => handler.HandleAsync(
                    new CompleteLoginCommand(
                        request.Identity,
                        request.ReturnUrl,
                        request.ClientIpAddress,
                        request.UserAgent,
                        request.InviteToken,
                        // The value the browser already holds, read here (the only HTTP-aware step) and handed
                        // over as the rotation predecessor. Absent for a first sign-in.
                        request.HttpContext.Request.Cookies[SessionCookieNames.Session]),
                    token),
                login => new SessionTicket(login.SessionId, login.SessionCookieValue, login.ExpiresAt),
                request.HttpContext,
                cancellationToken)
            .ConfigureAwait(false);

        return issued.IsSuccess
            ? Result.Success(issued.Value.RedirectPath)
            : Result.Failure<string>(issued.Error!);
    }
}
