using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The GitHub App installation routes. Delegates only bind, send one message and map the <see cref="Result"/>:
/// authorization is each handler's <c>[Permission]</c>, the tenant is the pipeline's, and neither a nonce nor a
/// OAuth code is ever echoed back.
/// </summary>
public static class GitHubInstallationEndpoints
{
    /// <summary>The callback route, whose query carries the one-time state.</summary>
    internal const string CallbackPath = "/v1/github/installations/callback";

    /// <summary>Maps the routes and returns the group so the host applies its filters.</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapGitHubInstallationEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/github/installations").WithTags("identity");

        group.MapPost("/start", StartAsync)
            .Produces<GitHubInstallStartResponse>(StatusCodes.Status200OK)
            .WithSummary("Start the GitHub App install flow: a single-use state and the install URL");
        group.MapGet("/callback", CallbackAsync)
            .Produces<GitHubInstallationDto>(StatusCodes.Status200OK)
            .WithSummary("Complete the install: consume the state, verify the installation and bind it");
        group.MapGet("/", GetAsync)
            .Produces<GitHubInstallationDto>(StatusCodes.Status200OK)
            .WithSummary("The org's GitHub installation");
        group.MapDelete("/{i}", DisconnectAsync)
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Disconnect the installation from this org (the App stays installed at GitHub)");

        return group;
    }

    /// <summary>
    /// Reads the organization a callback state names. The prefix is what the start route wrote; the caller's
    /// membership of it is proven by the tenant resolution that follows, never by this parse.
    /// </summary>
    /// <param name="state">The untrusted <c>state</c> query value.</param>
    /// <returns>The org it names, or null when it names none.</returns>
    public static Guid? CallbackOrg(string? state) =>
        state is { Length: > 37 and <= 256 }
        && state[36] == '.'
        && Guid.TryParseExact(state[..36], "D", out var org)
            ? org
            : null;

    private static async Task<Result<GitHubInstallStartResponse>> StartAsync(
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        return await mediator.Send(new StartGitHubInstallCommand(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<GitHubInstallationDto>> CallbackAsync(
        HttpContext context,
        GitHubInstallCompletion completion,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        var query = context.Request.Query;
        var installationId = long.TryParse(query["installation_id"], out var parsed) ? parsed : 0;

        return await completion.CompleteAsync(
            new GitHubInstallCallback(installationId, query["code"], query["state"]),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<GitHubInstallationDto>> GetAsync(
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new GetGitHubInstallationQuery(), cancellationToken).ConfigureAwait(false);

    private static async Task<Result> DisconnectAsync(
        Guid i,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new DisconnectGitHubInstallCommand(i, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
}
