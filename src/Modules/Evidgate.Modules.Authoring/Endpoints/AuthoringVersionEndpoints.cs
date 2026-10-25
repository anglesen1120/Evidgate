using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Queries;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The two version read routes of 13.1.1 §5 — route 22 (<c>GET …/authoring/versions</c>) and route 23
/// (<c>GET …/authoring/versions/{v}</c>). Like the node, checklist and manual case routes they are mapped onto the
/// authoring group (<c>/v1/projects/{p}/authoring</c>) and their delegates only bind 00 §9.1's parameters and answer in
/// <see cref="Result"/> terms: authorization is the C2 pipeline, the tenant is the request's, and the envelope is the
/// host's <c>ResultEndpointFilter</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract for <c>AuthoringEndpoints</c>:</b> this file exposes
/// <c>internal static void MapAuthoringVersionEndpoints(RouteGroupBuilder authoring)</c>, in the same static class as its
/// delegates, and maps its routes as relative paths on the group it is given (<c>authoring.MapGet("/versions", …)</c>) —
/// never with its own <c>/v1/projects/{p}/authoring</c> prefix. The two routes are reads, so the permission sits on the
/// handler classes, never here.
/// </para>
/// <para>
/// <b>What the edge answers and what the handler answers</b> (the D-S55-Q3 split): the page size and the two uuid-shaped
/// filters are read here, so a malformed shape is refused before the mediator runs; the enum vocabularies, the instants,
/// the <c>from &gt; to</c> range and the cursor are the handler's, which is where the same values are compared against
/// the stored rows. Neither side ever echoes a caller value into an answer.
/// </para>
/// <para>
/// <b>Route 23's <c>ETag</c></b> is the stored <c>content_hash</c> quoted exactly (<c>"sha256:&lt;hex&gt;"</c>), which is
/// the tag a client can compare after a re-read: the content is immutable, so the hash of a version never changes.
/// </para>
/// </remarks>
internal static class AuthoringVersionEndpoints
{
    /// <summary>Maps the version read routes (22 and 23) onto the authoring group.</summary>
    /// <param name="authoring">The <c>/v1/projects/{p}/authoring</c> group the host already created.</param>
    internal static void MapAuthoringVersionEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        authoring.MapGet("/versions", ListAsync)
            .Produces<AuthoringVersionListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Keyset page of a project's authoring versions, newest first (13.1.1 §5 route 22)");

        authoring.MapGet("/versions/{v}", GetAsync)
            .Produces<AuthoringVersionDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("One authoring version with its stored content and an ETag (route 23)");
    }

    /// <summary>Route 22: the newest-first page of versions matching the query's filters.</summary>
    private static async Task<Result<AuthoringVersionListResponse>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<AuthoringVersionListResponse>(limitError);
        }

        if (AuthoringEndpointText.OptionalId(context.Request.Query, "entity_id", out var entityId) is { } entityError)
        {
            return Result.Failure<AuthoringVersionListResponse>(entityError);
        }

        // §5 route 22 names the author filter `actor`; AC9 spells it `actor_membership_id`. The route binds the §5
        // spelling (the published contract) and the gap is recorded with the slice's hand-over.
        if (AuthoringEndpointText.OptionalId(context.Request.Query, "actor", out var actor) is { } actorError)
        {
            return Result.Failure<AuthoringVersionListResponse>(actorError);
        }

        return await mediator.Send(
            new ListAuthoringVersionsQuery(
                p,
                AuthoringEndpointText.Blank(context.Request.Query["entity_kind"]),
                entityId,
                AuthoringEndpointText.Blank(context.Request.Query["source"]),
                actor,
                AuthoringEndpointText.Blank(context.Request.Query["from"]),
                AuthoringEndpointText.Blank(context.Request.Query["to"]),
                AuthoringEndpointText.Blank(context.Request.Query["cursor"]),
                limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 23: one stored version, its content verbatim and the <c>ETag</c> of that content.</summary>
    private static async Task<Result<AuthoringVersionDetailDto>> GetAsync(
        Guid p,
        Guid v,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await mediator.Send(new GetAuthoringVersionQuery(p, v), cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            SetContentHashETag(context, result.Value.ContentHash);
        }

        return result;
    }

    /// <summary>
    /// Writes route 23's <c>ETag</c>: the stored <c>content_hash</c> in quotes, exactly the value the row carries
    /// (<c>"sha256:&lt;hex&gt;"</c>), so a client compares it without re-deriving the digest.
    /// </summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="contentHash">The row's <c>content_hash</c>.</param>
    private static void SetContentHashETag(HttpContext context, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contentHash);

        context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{contentHash}\"");
    }
}
