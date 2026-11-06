using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Usage.Endpoints;

/// <summary>
/// The tenant routes of the usage area. Like the catalog and retention routes the delegate only binds 00 §9.1's
/// parameters and answers in <see cref="Result"/> terms: authorization is the C2 pipeline (<c>usage:read</c>), the
/// tenant is the request's, and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
public static class UsageEndpoints
{
    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    /// <param name="routes">The route builder of the host.</param>
    public static RouteGroupBuilder MapUsageEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("usage");

        group.MapGet("/orgs/{o}/usage/summary", GetUsageSummaryAsync)
            .Produces<UsageSummaryDto>(StatusCodes.Status200OK)
            .WithSummary("The period's counters against quota, org-wide and per project when asked (12)");

        return group;
    }

    /// <summary>
    /// <c>GET /v1/orgs/{o}/usage/summary?project_id=</c>: the org-wide counters of the current period and, when a project
    /// is named, that project's counters too. The project is a filter on the org-wide answer, so absent is the whole
    /// org rather than the caller's default project.
    /// </summary>
    private static async Task<Result<UsageSummaryDto>> GetUsageSummaryAsync(
        Guid o,
        [FromQuery(Name = "project_id")] Guid? project_id,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new GetUsageSummaryQuery(o, project_id), cancellationToken).ConfigureAwait(false);
}
