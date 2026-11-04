using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Changes;
using Evidgate.Modules.Authoring.Application.Queries;
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
/// capped at the 32 KiB its schema declares, and the member <em>values</em> are the command's job, because the
/// payload is validated against its own contract there.
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
            .Produces<AuthoringChangePage>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Keyset page of a project's queued authoring changes");

        authoring.MapPost("/changes", QueueAsync)
            .Produces<QueuedChange>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithSummary("Queue a comment, delete, merge or split on a checklist item");

        authoring.MapGet("/changes/{c}", GetAsync)
            .Produces<AuthoringChangeView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("One queued change, with the ETag an apply sends back");

        authoring.MapPost("/changes/{c}/discard", DiscardAsync)
            .Produces<DiscardedAuthoringChange>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Discard one pending change; the state is the precondition");

        authoring.MapPost("/changes:apply", ApplyAsync)
            .Produces<ApplyAuthoringChangesResult>(StatusCodes.Status200OK)
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
    private static async Task<Result<AuthoringChangePage>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<AuthoringChangePage>(limitError);
        }

        if (AuthoringEndpointText.OptionalId(context.Request.Query, "target_id", out var targetId) is { } targetError)
        {
            return Result.Failure<AuthoringChangePage>(targetError);
        }

        if (OptionalIds(context.Request.Query, out var ids) is { } idsError)
        {
            return Result.Failure<AuthoringChangePage>(idsError);
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
    private static async Task<Result<QueuedChange>> QueueAsync(
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
            return Result.Failure<QueuedChange>(bodyError);
        }

        var result = await mediator.Send(
            new QueueAuthoringChangeCommand(
                p,
                Text(body, "target_kind"),
                Id(body, "target_id"),
                Text(body, "change_type"),
                Payload(body),
                Text(body, "reason"),
                AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            SetQuotedETag(context, result.Value.Etag);
        }

        return result;
    }

    /// <summary>The detail read: the change plus the <c>ETag</c> its row version carries.</summary>
    private static async Task<Result<AuthoringChangeView>> GetAsync(
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
    private static async Task<Result<DiscardedAuthoringChange>> DiscardAsync(
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
    private static async Task<Result<ApplyAuthoringChangesResult>> ApplyAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure<ApplyAuthoringChangesResult>(bodyError);
        }

        // The header is passed through as sent: the command owns the tag grammar, the 422 `/If-Match` and the 412,
        // so an endpoint that parsed it here could refuse a tag the contract accepts (or accept one it refuses).
        var ifMatch = AuthoringEndpointSupport.Blank(context.Request.Headers.IfMatch.ToString());

        return await mediator.Send(
            new ApplyAuthoringChangesCommand(
                p,
                ChangeIds(body),
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

    /// <summary>A string member, or <c>null</c> when it is absent or of another kind.</summary>
    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A uuid member, or <see cref="Guid.Empty"/> so the command can name <c>/target_id</c> itself.</summary>
    private static Guid Id(JsonElement body, string name) =>
        Text(body, name) is { } raw && Guid.TryParse(raw, out var id) ? id : Guid.Empty;

    /// <summary>The payload member as sent; an absent one is <see cref="JsonValueKind.Undefined"/> for the command.</summary>
    private static JsonElement Payload(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("payload", out var payload) ? payload : default;

    /// <summary>The <c>change_ids</c> members that are uuids; the command counts and reports the rest.</summary>
    private static List<Guid> ChangeIds(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("change_ids", out var members)
            || members.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<Guid>(members.GetArrayLength());

        foreach (var member in members.EnumerateArray())
        {
            if (member.ValueKind == JsonValueKind.String && Guid.TryParse(member.GetString(), out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
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
