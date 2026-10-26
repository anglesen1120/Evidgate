using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// The real login completion (02.3.1-T1.4): sends <see cref="CompleteLoginCommand"/>, writes the
/// <c>__Host-evidgate_session</c> cookie and the antiforgery pair from the ticket it answers, and hands the redirect
/// target back to the protocol.
/// </summary>
/// <remarks>
/// <para>
/// This class is the only place the login seam meets HTTP: the command is HTTP-free (it is the business
/// order of the login's own writes), and the protocol route above it stays a browser-flow component. The seam
/// answers a <see cref="Result{T}"/> either way and never a 200, so a verified identity can never be reported
/// as a signed-in one.
/// </para>
/// <para>
/// <b>One cookie pair, one redirect, one refusal (02.3.2-T1 D1/D5a/D6).</b> The cookies are written only after the
/// session row exists — a refusal leaves the browser with the deleted pending-login and staged-invite cookies and
/// nothing else. The session cookie is written through <see cref="SessionCookieWriter"/> with the ABSOLUTE
/// lifetime: a fresh row's absolute deadline is exactly <c>now + SessionOptions.AbsoluteLifetime</c>, so the
/// browser keeps the cookie until the row's hard deadline and never past it. The pair is issued in the same
/// response, bound to the new session id, so the first mutation after the redirect already validates. The value
/// the browser PRESENTED to the callback is handed to the command as
/// <see cref="CompleteLoginCommand.PredecessorCookieValue"/>, which is how a sign-in kills the session the browser
/// was carrying before it (AC7).
/// </para>
/// <para>
/// <b>One cookie, one redirect, one refusal.</b> A database fault (<see cref="NpgsqlException"/>,
/// <see cref="TimeoutException"/>, a busy audit chain
/// (<see cref="AuditBusyException"/>, 02.3.1-T2.2), or a cancellation that is not the client hanging up) is
/// 503 <c>unavailable</c> with <c>Retry-After</c>, the same fixed message and header the provider-outage path
/// uses: no database text, no SQL, no host name ever reaches the body. That 503 ends the attempt — the header
/// says when to sign in again, because the callback it answered is already spent (cookies deleted,
/// <c>state</c> consumed) and replaying it is 401 <c>token_invalid</c>.
/// </para>
/// <para>
/// Telemetry is not recorded here: the callback counts the attempt from the failure's code, so an outcome is
/// counted once, at the single recording point.
/// </para>
/// </remarks>
/// <param name="handler">The command's handler (ordered writes, typed refusals).</param>
/// <param name="options">The bound login options (<see cref="LoginOptions.RetryAfterSeconds"/>).</param>
/// <param name="sessionOptions">The bound session options (the absolute lifetime the browser deadline uses).</param>
/// <param name="antiforgery">The antiforgery service that mints the pair issued with the session.</param>
/// <param name="clock">The clock both the row and the cookie deadline were computed from.</param>
internal sealed class LoginCommandCompletion(
    CompleteLoginCommandHandler handler,
    IOptions<LoginOptions> options,
    IOptions<SessionOptions> sessionOptions,
    IAntiforgery antiforgery,
    TimeProvider clock)
    : ILoginCompletion
{
    /// <inheritdoc />
    public async ValueTask<Result<string>> CompleteAsync(
        LoginCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<LoginResult> completed;
        try
        {
            completed = await handler
                .HandleAsync(
                    new CompleteLoginCommand(
                        request.Identity,
                        request.ReturnUrl,
                        request.ClientIpAddress,
                        request.UserAgent,
                        request.InviteToken,
                        // D5a/AC7: the value the browser already holds, read here (the only HTTP-aware step) and
                        // handed over as the rotation predecessor. Absent for a first sign-in.
                        request.HttpContext.Request.Cookies[SessionCookieNames.Session]),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            return Fail(LoginErrors.ProviderUnavailable, request.HttpContext);
        }
        catch (TimeoutException)
        {
            return Fail(LoginErrors.ProviderUnavailable, request.HttpContext);
        }
        catch (AuditBusyException)
        {
            // The audit chain of one of the user's orgs held its lock too long (02.3.1-T2.2, Q5): fail-closed
            // with the same 503 the database faults get, so no session is issued for an unrecorded login.
            return Fail(LoginErrors.ProviderUnavailable, request.HttpContext);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A database timeout that surfaced as cancellation is an outage, not the caller leaving.
            return Fail(LoginErrors.ProviderUnavailable, request.HttpContext);
        }

        if (!completed.IsSuccess)
        {
            return Fail(completed.Error!, request.HttpContext);
        }

        var login = completed.Value;

        // The row was written with `absolute_expires_at = now + AbsoluteLifetime` (SessionStore), so the browser
        // deadline is exactly that, computed from the same clock (D1: Max-Age is the absolute lifetime).
        SessionCookieWriter.Write(
            request.HttpContext,
            new SessionTicket(login.SessionId, login.SessionCookieValue, login.ExpiresAt),
            clock.GetUtcNow() + sessionOptions.Value.AbsoluteLifetime,
            antiforgery,
            clock);

        return Result.Success(login.RedirectPath);
    }

    /// <summary>
    /// <paramref name="error"/> as the seam's failure, with <c>Retry-After</c> set when the answer is the 503
    /// of 00 §9.2. The header answers when to sign in again, not when to replay: the callback has already
    /// deleted the pending-login cookie and spent its one-time <c>state</c> before this seam is reached, so the
    /// same callback answers 401 <c>token_invalid</c> and the browser restarts the login from <c>start</c>.
    /// </summary>
    private Result<string> Fail(Error error, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(context);

        if (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.RetryAfter =
                options.Value.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return Result.Failure<string>(error);
    }
}
