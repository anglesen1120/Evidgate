using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Application.Queries;
using Evidgate.Modules.Catalog.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Evidgate.Modules.Catalog.Endpoints.CatalogRequestRules;

namespace Evidgate.Modules.Catalog.Endpoints;

/// <summary>
/// The template registry reads, mapped into the catalog group. They are org-level routes: the tenant comes from the
/// authenticated principal and RLS supplies the visible rows, so neither route parses a query value or looks a row up
/// before the pipeline's authorization stage.
/// </summary>
internal static class TemplateEndpoints
{
    /// <summary>Maps the template routes into the given <c>/v1</c> group and returns it.</summary>
    /// <param name="group">The catalog group.</param>
    public static RouteGroupBuilder MapTemplateEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/templates", ListTemplatesAsync)
            .Produces<TemplateListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of active templates, built-ins plus the caller's org rows (03)");

        group.MapGet("/templates/{t}", GetTemplateAsync)
            .Produces<TemplateDetailDto>(StatusCodes.Status200OK)
            .WithSummary("One active template with its conformance rules; unknown, inactive or another org's id is 404 (03)");

        return group;
    }

    private static async Task<Result<TemplateListResponse>> ListTemplatesAsync(
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;

        // The page size and filters are validated in the handler, after the pipeline's authorization stage: an anonymous
        // caller gets 401 instead of a 422 that describes a bound it may not use.
        return await mediator.Send(
            new ListTemplatesQuery(
                Blank(query["kind"]),
                Blank(query["platform"]),
                Blank(query["level"]),
                Blank(query["purpose"]),
                Blank(query["cursor"]),
                Blank(query["limit"])),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<TemplateDetailDto>> GetTemplateAsync(
        Guid t,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new GetTemplateQuery(t), cancellationToken).ConfigureAwait(false);
}
