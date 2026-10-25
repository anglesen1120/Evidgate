using Evidgate.Api.Endpoints;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Antiforgery;

namespace Evidgate.Api.Middleware;

/// <summary>
/// 02.3.2-T1 (D6, AC5): the host-wide antiforgery gate. A request whose authority is its
/// <c>__Host-evidgate_session</c> cookie and whose method mutates must carry an <c>X-XSRF-TOKEN</c> header that
/// validates against the pair bound to that session; otherwise it answers 403 <c>csrf_failed</c> and no handler
/// runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Middleware, not an attribute, so coverage is the default.</b> A filter or an annotation would have to be
/// added to every route group — and the one group somebody forgets is the one that needed it. Here a new mutating
/// route is covered the moment it is served, and the exemptions are the explicit, tested exception
/// (<see cref="SessionXsrfPolicy"/>).
/// </para>
/// <para>
/// <b>Order of answers.</b> The credential is judged first: a presented cookie that is expired, revoked or unknown
/// answers ITS OWN 401 (through <c>ICallerRefusalAuthenticator</c>'s path) rather than a 403 that would report a
/// CSRF failure for a request that never had a valid session. Only a request the cookie authenticated reaches the
/// pair check.
/// </para>
/// <para>
/// <b>Rotation.</b> Auth here is the request authenticator's memoized call, so a mutation that triggers a
/// privilege-change rotation rotates, re-issues the pair and still validates the pair the client sent (the
/// presented-session slot of <see cref="SessionAntiforgeryDataProvider"/>) — and the tenant filter and the endpoint
/// then see the same outcome with no second statement (P1/P3).
/// </para>
/// </remarks>
/// <param name="next">The rest of the pipeline.</param>
internal sealed class SessionXsrfMiddleware(RequestDelegate next)
{
    /// <inheritdoc cref="RequestDelegate" />
    public async Task InvokeAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(antiforgery);

        if (!SessionXsrfPolicy.IsInScope(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var outcome = await authenticator.AuthenticateAsync(context, context.RequestAborted).ConfigureAwait(false);

        if (outcome.Refusal is { } refusal)
        {
            await Result.Failure(refusal).ToHttpResult(context).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!await SessionXsrfPolicy.ValidatesAsync(context, antiforgery, context.RequestAborted).ConfigureAwait(false))
        {
            EvidgateTelemetry.RecordSessionAuthOutcome(ApiErrorCode.CsrfFailed);
            await Result.Failure(SessionErrors.CsrfFailed).ToHttpResult(context).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
