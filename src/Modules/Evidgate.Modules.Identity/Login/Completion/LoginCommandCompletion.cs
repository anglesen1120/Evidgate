using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// The real login completion (02.3.1-T1.4): sends <see cref="CompleteLoginCommand"/>, writes the
/// <c>__Host-evidgate_session</c> cookie from the ticket it answers and hands the redirect target back to the
/// protocol.
/// </summary>
/// <remarks>
/// <para>
/// This class is the only place the login seam meets HTTP: the command is HTTP-free (it is the business
/// order of the three writes), and the protocol route above it stays a browser-flow component. The seam
/// answers a <see cref="Result{T}"/> either way and never a 200, so a verified identity can never be reported
/// as a signed-in one.
/// </para>
/// <para>
/// <b>One cookie, one redirect, one refusal.</b> The cookie is written only after the session row exists —
/// a refusal leaves the browser with the deleted pending-login cookie and nothing else. A database fault
/// (<see cref="NpgsqlException"/>, <see cref="TimeoutException"/>, or a cancellation that is not the client
/// hanging up) is 503 <c>unavailable</c> with <c>Retry-After</c>, the same fixed message and header the
/// provider-outage path uses: no database text, no SQL, no host name ever reaches the body. That 503 ends
/// the attempt — the header says when to sign in again, because the callback it answered is already spent
/// (cookie deleted, <c>state</c> consumed) and replaying it is 401 <c>token_invalid</c>.
/// </para>
/// <para>
/// Telemetry is not recorded here: the callback counts the attempt from the failure's code, so an outcome is
/// counted once, at the single recording point.
/// </para>
/// </remarks>
/// <param name="handler">The command's handler (three ordered writes, typed refusals).</param>
/// <param name="options">The bound login options (<see cref="LoginOptions.RetryAfterSeconds"/>).</param>
internal sealed class LoginCommandCompletion(
    CompleteLoginCommandHandler handler,
    IOptions<LoginOptions> options)
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
                        request.UserAgent),
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
        SessionCookie.Write(
            request.HttpContext,
            new SessionTicket(login.SessionId, login.SessionCookieValue, login.ExpiresAt));

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
