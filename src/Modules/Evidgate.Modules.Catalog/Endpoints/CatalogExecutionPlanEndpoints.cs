using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Application.Queries;
using Evidgate.Modules.Catalog.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Catalog.Endpoints;

/// <summary>
/// The execution-plan read (<c>GET /v1/suite-revisions/{r}/execution-plan</c>): the members a CI caller needs to build a
/// manifest, without the <c>suite:read</c> that would also expose the full revision.
/// </summary>
internal static class CatalogExecutionPlanEndpoints
{
    /// <summary>The page size when the caller names none.</summary>
    internal const int DefaultLimit = 200;

    /// <summary>The largest page this route serves; its contract is this route's, not the shared 200.</summary>
    internal const int MaxLimit = 500;

    /// <summary>Maps the route onto the catalog group.</summary>
    public static RouteGroupBuilder MapExecutionPlanEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/suite-revisions/{r}/execution-plan", GetExecutionPlanAsync)
            .Produces<SuiteRevisionExecutionPlan>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a suite revision's pinned members for manifest generation");

        return group;
    }

    private static async Task<Result<SuiteRevisionExecutionPlan>> GetExecutionPlanAsync(
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var limitPresent = query.TryGetValue("limit", out var rawLimit);
        if (CatalogRequestRules.RouteLimit(limitPresent, rawLimit.ToString(), DefaultLimit, MaxLimit, out var limit) is { } error)
        {
            return Result.Failure<SuiteRevisionExecutionPlan>(error);
        }

        // An absent cursor is the first page; a present but empty one is undecodable input, refused by the handler.
        var cursor = query.TryGetValue("cursor", out var rawCursor) ? rawCursor.ToString() : null;

        return await mediator.Send(new GetSuiteExecutionPlanQuery(r, cursor, limit), cancellationToken)
            .ConfigureAwait(false);
    }
}
