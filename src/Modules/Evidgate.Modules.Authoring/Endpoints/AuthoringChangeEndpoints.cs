using Evidgate.BuildingBlocks.Contracts.Validation;
using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Changes;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.Modules.Authoring.Domain.Changes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The pending-changes queue: the five routes of the batched-review surface (queue, list, detail, discard, apply).
/// They are mapped onto the authoring group like the other route files and their delegates only bind 00 §9.1's
/// parameters and answer in <see cref="Result"/> terms: authorization is the C2 pipeline, the tenant is the
/// request's, and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
/// <remarks>
/// <para>
/// The queue and apply bodies are read as a <see cref="JsonElement"/> through
/// <see cref="AuthoringEndpointSupport.ReadBodyAsync(HttpContext, int, CancellationToken)"/>: the queue body is
/// capped at the 32 KiB its schema declares, and the queue's members are validated and projected by the command,
/// against <c>authoring-change.v1</c>, so a document the schema refuses never reaches the database.
/// </para>
/// <para>
/// The apply route passes the <c>If-Match</c> header through <b>unparsed</b> (quotes and <c>W/</c> preserved, empty
/// read as absent): the tag grammar and the 422/412 answers live in one place, the command, so the endpoint cannot
/// disagree with it. The two reads that carry an <c>ETag</c> write it from the value the query produced, never from
/// a re-derived number.
/// </para>
/// </remarks>
internal static class AuthoringChangeEndpoints
{
    /// <summary>Maps the queue routes onto the authoring group.</summary>
    /// <param name="authoring">The <c>/v1/projects/{p}/authoring</c> group the host already created.</param>
    internal static void MapAuthoringChangeEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        authoring.MapGet("/changes", ListAsync)
            .Produces<AuthoringChangePageResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Keyset page of a project's queued authoring changes");

        authoring.MapPost("/changes", QueueAsync)
            .WithBodyLimit(QueueBodyCap, handlerRefusesFirst: true)
            .Produces<QueuedAuthoringChangeResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithSummary("Queue a comment, delete, merge or split on a checklist item");

        authoring.MapGet("/changes/{c}", GetAsync)
            .Produces<AuthoringChangeDetailResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("One queued change, with the ETag an apply sends back");

        authoring.MapPost("/changes/{c}/discard", DiscardAsync)
            .Produces<DiscardedAuthoringChangeResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Discard one pending change; the state is the precondition");

        authoring.MapPost("/changes:apply", ApplyAsync)
            .Produces<ApplyAuthoringChangesResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Apply a selected set of pending changes as one version");
    }

    /// <summary>The page: filters, an optional selection and the keyset cursor.</summary>
    private static async Task<Result<AuthoringChangePageResponse>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<AuthoringChangePageResponse>(limitError);
        }

        if (AuthoringEndpointText.OptionalId(context.Request.Query, "target_id", out var targetId) is { } targetError)
        {
            return Result.Failure<AuthoringChangePageResponse>(targetError);
        }

        if (OptionalIds(context.Request.Query, out var ids) is { } idsError)
        {
            return Result.Failure<AuthoringChangePageResponse>(idsError);
        }

        var result = await mediator.Send(
            new ListAuthoringChangesQuery(
                p,
                AuthoringEndpointText.Blank(context.Request.Query["state"]),
                AuthoringEndpointText.Blank(context.Request.Query["change_type"]),
                targetId,
                ids,
                AuthoringEndpointText.Blank(context.Request.Query["cursor"]),
                limit),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            SetQuotedETag(context, result.Value.Etag);
        }

        return result;
    }

    /// <summary>The queue write: 201 with the change's identity and the target version it read.</summary>
    private static async Task<Result<QueuedAuthoringChangeResponse>> QueueAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (body, bodyError) = await AuthoringEndpointSupport
            .ReadBodyAsync(context, QueueBodyCap, cancellationToken)
            .ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure<QueuedAuthoringChangeResponse>(bodyError);
        }

        var result = await mediator.Send(
            new QueueAuthoringChangeCommand(
                p,
                body,
                AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            SetQuotedETag(context, result.Value.Etag);
        }

        return result;
    }

    /// <summary>The detail read: the change plus the <c>ETag</c> its row version carries.</summary>
    private static async Task<Result<AuthoringChangeDetailResponse>> GetAsync(
        Guid p,
        Guid c,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await mediator.Send(new GetAuthoringChangeQuery(p, c), cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            SetQuotedETag(context, result.Value.Etag);
        }

        return result;
    }

    /// <summary>The discard write: no <c>If-Match</c>, because the state is the precondition.</summary>
    private static async Task<Result<DiscardedAuthoringChangeResponse>> DiscardAsync(
        Guid p,
        Guid c,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await mediator.Send(
            new DiscardAuthoringChangeCommand(p, c, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The apply write: the selection and the batch tag, in one transaction on the handler's side.</summary>
    private static async Task<Result<ApplyAuthoringChangesResponse>> ApplyAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure<ApplyAuthoringChangesResponse>(bodyError);
        }

        // The header is passed through as sent: the command owns the tag grammar, the 422 `/If-Match` and the 412,
        // so an endpoint that parsed it here could refuse a tag the contract accepts (or accept one it refuses).
        var ifMatch = AuthoringEndpointSupport.Blank(context.Request.Headers.IfMatch.ToString());
        var (changeIds, selectionError) = ChangeIds(body);

        if (selectionError is not null)
        {
            return Result.Failure<ApplyAuthoringChangesResponse>(selectionError);
        }

        return await mediator.Send(
            new ApplyAuthoringChangesCommand(
                p,
                changeIds ?? [],
                ifMatch,
                AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The queue body cap its schema declares, so an oversized document is the 413 before any write.</summary>
    private const int QueueBodyCap = 32 * 1024;

    /// <summary>An optional uuid list, sent once comma-separated or repeated; a bad member names <c>/ids</c>.</summary>
    private static Error? OptionalIds(IQueryCollection query, out IReadOnlyList<Guid>? ids)
    {
        ArgumentNullException.ThrowIfNull(query);

        ids = null;

        if (!query.TryGetValue("ids", out var rawValues))
        {
            return null;
        }

        var parsed = new List<Guid>();

        foreach (var value in rawValues)
        {
            foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var id) || id == Guid.Empty)
                {
                    return Error.Invalid("ids must be uuids", [new ErrorDetail("/ids", "value_invalid")]);
                }

                parsed.Add(id);
            }
        }

        if (parsed.Count == 0)
        {
            return Error.Invalid("ids must be uuids", [new ErrorDetail("/ids", "value_invalid")]);
        }

        ids = parsed;

        return null;
    }

    /// <summary>
    /// The <c>change_ids</c> selection, or the 422 that names the member it refused: every member must be a uuid string,
    /// and the 200 limit is read from the raw array so an oversized selection is refused as oversized even when one of
    /// its members is malformed too. A member that no longer parses is never dropped silently — the command's ETag covers
    /// the selection the caller sent, not the one the server could make of it.
    /// </summary>
    /// <param name="body">The apply body.</param>
    private static (List<Guid>? Ids, Error? Refusal) ChangeIds(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("change_ids", out var members)
            || members.ValueKind != JsonValueKind.Array)
        {
            return ([], null);
        }

        if (members.GetArrayLength() > AuthoringChangeLimits.SelectionMax)
        {
            return (null, AuthoringErrors.Invalid(
                "/change_ids",
                "too_many_changes",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"at most {AuthoringChangeLimits.SelectionMax} changes can be applied at once")));
        }

        var ids = new List<Guid>(members.GetArrayLength());
        var index = 0;

        foreach (var member in members.EnumerateArray())
        {
            if (member.ValueKind != JsonValueKind.String
                || !Guid.TryParse(member.GetString(), out var id))
            {
                return (null, AuthoringErrors.Invalid(
                    string.Create(CultureInfo.InvariantCulture, $"/change_ids/{index}"),
                    "payload_invalid",
                    "change_ids must be uuids"));
            }

            ids.Add(id);
            index++;
        }

        return (ids, null);
    }

    /// <summary>Writes the response validator, quoting a bare value so the header stays syntactically valid.</summary>
    private static void SetQuotedETag(HttpContext context, string? etag)
    {
        if (string.IsNullOrEmpty(etag))
        {
            return;
        }

        context.Response.Headers.ETag = etag.StartsWith('"') ? etag : "\"" + etag + "\"";
    }
}
