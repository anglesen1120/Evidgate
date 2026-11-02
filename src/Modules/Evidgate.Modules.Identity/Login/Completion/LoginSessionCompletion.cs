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
/// The HTTP half every sign-in seam shares: run the ordered session write, answer a database or audit-chain
/// fault as the contract's 503 (with <c>Retry-After</c>), and write the cookie pair from the ticket it answers.
/// </summary>
/// <remarks>
/// The federated completion and the development-only sign-in differ only in how an account is identified; from
/// the session write onwards they must behave identically, so that order and its outage answer live here once
/// instead of in two catch ladders that can drift apart.
/// </remarks>
/// <param name="options">The bound login options (<see cref="LoginOptions.RetryAfterSeconds"/>).</param>
/// <param name="sessionOptions">The bound session options (the absolute lifetime the browser deadline uses).</param>
/// <param name="antiforgery">The antiforgery service that mints the pair issued with the session.</param>
/// <param name="clock">The clock both the row and the cookie deadline were computed from.</param>
internal sealed class LoginSessionCompletion(
    IOptions<LoginOptions> options,
    IOptions<SessionOptions> sessionOptions,
    IAntiforgery antiforgery,
    TimeProvider clock)
{
    /// <summary>
    /// Runs <paramref name="issue"/> and, when it answers a ticket, writes the cookie pair for it.
    /// </summary>
    /// <typeparam name="T">The seam's own success body (a login result, or the ticket itself).</typeparam>
    /// <param name="issue">The ordered write to run; a database or audit fault may escape it.</param>
    /// <param name="ticket">The session the success body carries, which the cookie is written for.</param>
    /// <param name="context">The request the cookie pair is written on.</param>
    /// <param name="cancellationToken">Request abort, and the one cancellation that is NOT an outage.</param>
    /// <returns>The seam's own result, or the 503 an outage becomes.</returns>
    internal async Task<Result<T>> CompleteAsync<T>(
        Func<CancellationToken, ValueTask<Result<T>>> issue,
        Func<T, SessionTicket> ticket,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(context);

        Result<T> issued;
        try
        {
            issued = await issue(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            return Failure<T>(LoginErrors.ProviderUnavailable, context);
        }
        catch (TimeoutException)
        {
            return Failure<T>(LoginErrors.ProviderUnavailable, context);
        }
        catch (AuditBusyException)
        {
            // The audit chain of an org held its lock too long: no session is issued for a sign-in that was
            // never recorded.
            return Failure<T>(LoginErrors.ProviderUnavailable, context);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A database timeout that surfaced as cancellation is an outage, not the caller leaving.
            return Failure<T>(LoginErrors.ProviderUnavailable, context);
        }

        if (!issued.IsSuccess)
        {
            return Failure<T>(issued.Error!, context);
        }

        // A fresh row's absolute deadline is exactly `now + AbsoluteLifetime`, so the browser keeps the cookie
        // until the row's hard deadline and never past it.
        SessionCookieWriter.Write(
            context,
            ticket(issued.Value),
            clock.GetUtcNow() + sessionOptions.Value.AbsoluteLifetime,
            antiforgery,
            clock);

        return issued;
    }

    /// <summary>
    /// <paramref name="error"/> as a failure, with <c>Retry-After</c> on a 503: the header says when to sign in
    /// again, not when to replay a callback whose one-time state is already spent.
    /// </summary>
    /// <typeparam name="T">The success body the failure replaces.</typeparam>
    /// <param name="error">The refusal to answer.</param>
    /// <param name="context">The response the header is set on.</param>
    internal Result<T> Failure<T>(Error error, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(context);

        SetRetryAfter(error, context);

        return Result.Failure<T>(error);
    }

    /// <summary>The bodiless twin of <see cref="Failure{T}"/>, for a route whose success carries no body.</summary>
    /// <param name="error">The refusal to answer.</param>
    /// <param name="context">The response the header is set on.</param>
    internal Result Failure(Error error, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(context);

        SetRetryAfter(error, context);

        return Result.Failure(error);
    }

    private void SetRetryAfter(Error error, HttpContext context)
    {
        if (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.RetryAfter =
                options.Value.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }
    }
}
