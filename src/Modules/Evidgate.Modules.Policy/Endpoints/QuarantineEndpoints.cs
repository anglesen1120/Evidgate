using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application.Quarantine;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Policy.Endpoints;

/// <summary>
/// The quarantine routes of 07.3.1 §5. Like the waiver routes the delegates only bind 00 §9.1's parameters and
/// answer in <see cref="Result"/> terms: authorization is the C2 pipeline, the tenant is the request's (never the
/// URL's), and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
public static class QuarantineEndpoints
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    /// <summary>Maps the quarantine routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapQuarantineEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("policy");

        group.MapPost("/quarantine", CreateQuarantineEntryAsync)
            .Accepts<QuarantineEntryCreateRequest>("application/json")
            .Produces<QuarantineEntry>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Quarantine a case; a critical case is refused 422 (07.3.1)");

        group.MapDelete("/quarantine/{q}", RemoveQuarantineEntryAsync)
            .Produces<QuarantineEntry>(StatusCodes.Status200OK)
            .WithSummary("Remove a quarantine entry; a non-active entry is 404 (07.3.1)");

        group.MapGet("/projects/{p}/quarantine", ListQuarantineEntriesAsync)
            .Produces<QuarantineEntryPage>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's quarantine entries (07.3.1)");

        return group;
    }

    private static async Task<Result<QuarantineEntry>> CreateQuarantineEntryAsync(
        QuarantineEntryCreateRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new CreateQuarantineEntryCommand(request.ProjectId, request.CaseId, request.Owner, request.Reason, request.ExpiresAt, IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

    private static async Task<Result<QuarantineEntry>> RemoveQuarantineEntryAsync(
        Guid q,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new RemoveQuarantineEntryCommand(q, IdempotencyKey(context)), cancellationToken).ConfigureAwait(false);

    private static async Task<Result<QuarantineEntryPage>> ListQuarantineEntriesAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<QuarantineEntryPage>(error);
        }

        return await mediator.Send(
            new ListQuarantineEntriesQuery(p, Blank(context.Request.Query["cursor"]), limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();

    private static Error? LimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1 || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;

        return null;
    }
}
