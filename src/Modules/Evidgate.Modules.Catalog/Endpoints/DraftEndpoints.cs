using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Application.Queries;
using Evidgate.Modules.Catalog.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Evidgate.Modules.Catalog.Endpoints.CatalogRequestRules;

namespace Evidgate.Modules.Catalog.Endpoints;

/// <summary>
/// The draft routes, mapped into the catalog group. A draft body carries a file set and optional provenance, so the
/// delegate reads it through the shared capped reader and hands the raw element on: the pipeline's authorization stage
/// answers a refused caller before any member of the body or the <c>If-Match</c> header is read.
/// </summary>
internal static class DraftEndpoints
{
    /// <summary>A draft body is a file set plus a short provenance object; the route refuses anything past 256 KiB.</summary>
    internal const int MaxBodyBytes = 256 * 1024;

    /// <summary>Maps the draft routes into the given <c>/v1</c> group and returns it.</summary>
    /// <param name="group">The catalog group.</param>
    public static RouteGroupBuilder MapDraftEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/projects/{p}/drafts", CreateDraftAsync)
            .WithBodyLimit(MaxBodyBytes)
            .Accepts<CreateDraftRequest>("application/json")
            .Produces<DraftDto>(StatusCodes.Status201Created)
            .WithSummary("Create a draft in a project; its ETag is the version (03)");

        group.MapPatch("/drafts/{d}", UpdateDraftAsync)
            .WithBodyLimit(MaxBodyBytes)
            .Accepts<UpdateDraftRequest>("application/json")
            .Produces<DraftDto>(StatusCodes.Status200OK)
            .WithSummary("Replace a draft's content under If-Match; a stale version is 412 (03)");

        group.MapGet("/projects/{p}/drafts", ListDraftsAsync)
            .Produces<DraftListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's drafts, optionally filtered by status (03)");

        group.MapGet("/drafts/{d}", GetDraftAsync)
            .Produces<DraftDto>(StatusCodes.Status200OK)
            .WithSummary("One draft; another org's or project's id is 404 (03)");

        return group;
    }

    private static async Task<Result<DraftDto>> CreateDraftAsync(
        Guid p,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await ReadAuthenticatedJsonAsync(context, principals, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<DraftDto>(readError);
        }

        var result = await mediator.Send(
            new CreateDraftCommand(p, body, IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, draft => draft.Version);
    }

    private static async Task<Result<DraftDto>> UpdateDraftAsync(
        Guid d,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await ReadAuthenticatedJsonAsync(context, principals, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<DraftDto>(readError);
        }

        var result = await mediator.Send(
            new UpdateDraftCommand(d, body, RawIfMatch(context), IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, draft => draft.Version);
    }

    private static async Task<Result<DraftListResponse>> ListDraftsAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;

        // The page size is validated in the handler, after the pipeline's authorization stage: an anonymous caller
        // gets 401 instead of a 422 that describes a bound it may not use.
        return await mediator.Send(
            new ListDraftsQuery(p, Blank(query["status"]), Blank(query["cursor"]), Blank(query["limit"])),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<DraftDto>> GetDraftAsync(
        Guid d,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetDraftQuery(d), cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, draft => draft.Version);
    }

    /// <summary>
    /// The <c>If-Match</c> header verbatim, so the command decides whether it is the ETag it issued. Reading it is not
    /// parsing it: a refused caller gets the pipeline's answer whatever it sent.
    /// </summary>
    private static string? RawIfMatch(HttpContext context) => context.Request.Headers.IfMatch.ToString();
}
