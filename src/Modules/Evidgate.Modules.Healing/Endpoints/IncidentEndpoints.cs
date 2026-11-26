using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Application;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Healing.Endpoints;

/// <summary>
/// The incident reads: the delegates only bind 00 §9.1's query parameters; authorization is the C2 pipeline
/// (<c>healing:triage</c>) and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
internal static class IncidentEndpoints
{
    /// <summary>Maps both reads on the healing <c>/v1</c> group.</summary>
    internal static void MapIncidentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/projects/{p}/incidents", ListIncidentsAsync)
            .Produces<IncidentPageView>(StatusCodes.Status200OK)
            .WithSummary("Cursor list of a project's incidents, newest first (08.1.1)");

        group.MapGet("/incidents/{i}", GetIncidentAsync)
            .Produces<IncidentView>(StatusCodes.Status200OK)
            .WithSummary("Read one incident (08.1.1)");
    }

    /// <summary>
    /// <c>state</c>, <c>execution_key</c> and <c>cursor</c> are single-valued: a blank or repeated value is a 422
    /// rather than a page quietly narrowed to its first occurrence. The handler validates the values.
    /// </summary>
    private static async Task<Result<IncidentPageView>> ListIncidentsAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (IncidentListRules.LimitError(query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<IncidentPageView>(limitError);
        }

        foreach (var name in new[] { "state", "execution_key", "cursor" })
        {
            if (query.TryGetValue(name, out var values) && (values.Count != 1 || (name != "cursor" && string.IsNullOrEmpty(values[0]))))
            {
                return Result.Failure<IncidentPageView>(Error.Invalid(
                    $"{name} has an invalid value",
                    [new ErrorDetail(name, "invalid_value")]));
            }
        }

        string? Single(string name) => query.TryGetValue(name, out var values) ? values[0] : null;
        var cursor = Single("cursor");
        return await mediator
            .Send(new ListIncidentsQuery(p, Single("state"), Single("execution_key"), string.IsNullOrEmpty(cursor) ? null : cursor, limit), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<IncidentView>> GetIncidentAsync(
        Guid i,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new GetIncidentQuery(i), cancellationToken).ConfigureAwait(false);
}
