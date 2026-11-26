using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// <c>GET /v1/me/permissions</c>: the signed-in member's effective grants in the org named by the
/// tenant resolution. Unlike <c>GET /v1/me</c> it is tenant-bound, so the <c>/v1</c> tenant filter
/// resolves the org (and refuses a non-member 403) before this runs.
/// </summary>
internal static class MyPermissionsEndpoint
{
    /// <summary>The answer is one caller's grants at one instant: a shared cache must never reuse it.</summary>
    private const string NoStore = "no-store";

    /// <summary>Maps the route onto the <c>/v1</c> group.</summary>
    /// <param name="group">The <c>/v1</c> group the module's endpoints are mapped on.</param>
    internal static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/me/permissions", ReadAsync)
            .Produces<MyPermissionsResponse>(StatusCodes.Status200OK)
            .WithSummary("The signed-in member's effective roles and permission codes in the org");
    }

    private static async Task<Result<MyPermissionsResponse>> ReadAsync(
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = NoStore;

        // A malformed project rides on the query so the pipeline's 401/403 still come first.
        var error = EdgeInputs.ProjectError(context, out var project);

        return await mediator.Send(new GetMyPermissionsQuery(project, error), cancellationToken).ConfigureAwait(false);
    }
}
