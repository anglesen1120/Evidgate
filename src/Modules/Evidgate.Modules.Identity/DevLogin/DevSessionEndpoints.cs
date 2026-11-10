using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Dev;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Endpoints;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Completion;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.DevLogin;

/// <summary>
/// <c>POST /v1/dev/session</c>: the development-only sign-in that gives the seeded dev user a real session,
/// through the same audit and session machinery a federated login uses.
/// </summary>
/// <remarks>
/// <para>
/// The route exists only when the host environment is Development, <c>EVIDGATE__DEVLOGIN__ENABLED</c> is true
/// and <c>EVIDGATE__DEVLOGIN__SECRET</c> is set (<see cref="DevSessionGate"/>); otherwise nothing is mapped and
/// the answer is the host's ordinary 404, never a 401 on a credential that does not exist.
/// </para>
/// <para>
/// The body carries the secret and nothing else, so the session always belongs to
/// <see cref="KnownDevUser.UserId"/> and no identity row is written. Both sides are reduced to SHA-256 digests
/// and compared with <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>
/// (<see cref="DevLoginSecret"/>), so neither the timing nor the branch depends on the presented value; a wrong,
/// absent or blank secret is the login's 401 <c>token_invalid</c>, with no session row and no cookie.
/// </para>
/// <para>
/// Plaintext is refused before the secret is judged: the cookie pair this route must issue is Secure-only, so a
/// plaintext sign-in can never complete, and answering the outage first keeps the status code independent of
/// whether the presented secret is right. The session itself is an ordinary row, so logout and revoke end it.
/// </para>
/// </remarks>
internal static class DevSessionEndpoints
{
    /// <summary>Largest body accepted: the secret plus envelope slack; nothing else is read.</summary>
    internal const int MaxBodyBytes = 4096;

    /// <summary>
    /// The limiter key prefix of this route: the CI exchange limiter's per-client key under a prefix of its own,
    /// so a flood here cannot spend the budget of <c>start</c>, <c>callback</c> or the invite staging route.
    /// </summary>
    private const string LimiterPrefix = "dev-session:";

    /// <summary>This route's answer is never cacheable: it sets a credential-bearing cookie.</summary>
    private const string NoStore = "no-store";

    /// <summary>The audit row's provider name for a development sign-in.</summary>
    private const string Provider = "dev";

    /// <summary>The wire member name of the secret (<c>contracts/openapi.yaml</c>, <c>DevSessionRequest</c>).</summary>
    private const string SecretField = "secret";

    /// <summary>
    /// Maps the route when <see cref="DevSessionGate"/> allows it; otherwise maps nothing, so the path answers
    /// the host's 404.
    /// </summary>
    /// <param name="routes">The route builder.</param>
    /// <returns>The group the host attaches its filters to; empty when the gate is closed.</returns>
    internal static RouteGroupBuilder MapDevSession(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // The group is declared either way so the host can attach its filters unconditionally; with the gate
        // closed it holds no route, so no path answers and the host's ordinary 404 stands.
        var group = routes.MapGroup("/v1/dev");
        var environment = routes.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var options = routes.ServiceProvider.GetRequiredService<IOptions<DevLoginOptions>>().Value;

        if (!DevSessionGate.IsOpen(environment, options))
        {
            return group;
        }

        // Same reason as the other pre-tenant routes: with a declared non-200 status ASP.NET infers a 200
        // response from the delegate's `Result` return type, which would put the wrapper's schema in the served
        // document. The bodiless `.Produces(200)` replaces that inference; the 204 is the real answer.
        //
        // XSRF-exempt like the invite staging route: this route SETS a cookie rather than authenticating with
        // one, so there is no session for a token to be bound to. Its defence is the secret and the JSON-only
        // body, and the web app calls it before any session exists.
        group.MapPost("/session", HandleAsync)
            .WithMetadata(XsrfExemptMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithTags("identity")
            .WithSummary("Development only: sign in as the seeded dev user (mapped only in Development with EVIDGATE__DEVLOGIN__ENABLED)");

        return group;
    }

    /// <summary>
    /// <c>sign in</c>: a 429 once the client's window is spent, a 413/422 for a body that is not the contract's,
    /// 503 for a plaintext request or a fault on the audit or session write, 401 for a wrong or absent secret,
    /// otherwise 204 plus the session cookie and the antiforgery pair.
    /// </summary>
    private static async Task<Result> HandleAsync(
        HttpContext context,
        ExchangeRateLimiter limiter,
        IOptions<DevLoginOptions> options,
        LoginSessionCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(completion);

        context.Response.Headers.CacheControl = NoStore;

        // First, before the body is touched: a wrong guess must not be free, and the route is anonymous.
        var throttled = LoginEndpoints.Throttle(
            context,
            limiter,
            LimiterPrefix + ClientIp.LimiterKey(context),
            cost: 1);
        if (throttled is not null)
        {
            return Result.Failure(throttled);
        }

        var (body, error) = await BoundedJsonBody.ReadAsync(context, MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return Result.Failure(error);
        }

        string? presented;
        using (body)
        {
            if (body!.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure(BoundedJsonBody.Invalid("the request body must be a JSON object"));
            }

            (presented, error) = ReadSecret(body.RootElement);
            if (error is not null)
            {
                return Result.Failure(error);
            }
        }

        // Refused before the secret is judged, so the status code does not tell a plaintext caller whether its
        // guess was right. The pair this route must issue is Secure-only, so a plaintext sign-in can never
        // complete; the honest answer is the same 503 a composition gap gets — never a weaker cookie, never a row.
        if (!context.Request.IsHttps)
        {
            return completion.Failure(LoginErrors.ProviderUnavailable, context);
        }

        // One answer for a wrong secret, an absent member and a blank value: the route is not an oracle for the
        // deployment's secret, and no session row or cookie is written on this path.
        if (!DevLoginSecret.Matches(options.Value.Secret, presented))
        {
            return Result.Failure(LoginErrors.TokenInvalid);
        }

        var handler = context.RequestServices.GetRequiredService<CompleteLoginCommandHandler>();

        // The login's own ordered write — audited before the session row exists — and the cookie pair that
        // answers it, exactly as the federated completion runs both.
        var issued = await completion
            .CompleteAsync<SessionTicket>(
                token => handler.IssueKnownUserAsync(
                    KnownDevUser.UserId,
                    Provider,
                    ClientIp.Text(context),
                    context.Request.Headers.UserAgent.ToString(),
                    // The value the browser already holds, handed over as the rotation predecessor: signing in
                    // again replaces that session instead of leaving a second live row behind.
                    context.Request.Cookies[SessionCookieNames.Session],
                    token),
                session => session,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return issued.IsSuccess
            ? Result.Success(StatusCodes.Status204NoContent)
            : Result.Failure(issued.Error!);
    }

    /// <summary>
    /// <c>secret</c> out of a JSON object body, or the refusal a value of the wrong type earns. An absent member
    /// or an explicit <c>null</c> is null here and is refused by the comparison as the 401 it is.
    /// </summary>
    /// <param name="body">The request body, already known to be a JSON object.</param>
    private static (string? Secret, Error? Error) ReadSecret(JsonElement body)
    {
        string? secret = null;

        foreach (var property in body.EnumerateObject())
        {
            if (!string.Equals(property.Name, SecretField, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return (null, Error.Invalid(
                    "the dev-login secret must be a string",
                    [new ErrorDetail(SecretField, "invalid")]));
            }

            secret = property.Value.GetString();
        }

        return (secret, null);
    }
}
