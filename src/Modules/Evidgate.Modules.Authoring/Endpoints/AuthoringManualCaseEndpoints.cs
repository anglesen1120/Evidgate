using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The manual test case routes 12–17 and 19 of 13.1.1 §5. Like the node and checklist routes they are mapped onto the
/// authoring group (<c>/v1/projects/{p}/authoring</c>) and their delegates only bind 00 §9.1's parameters and answer in
/// <see cref="Result"/> terms: authorization is the C2 pipeline, the tenant is the request's, and the envelope is the
/// host's <c>ResultEndpointFilter</c>.
/// </summary>
/// <remarks>
/// <para>
/// The body of every write route is read through <see cref="AuthoringEndpointSupport.ReadBodyAsync"/> (strict object,
/// 64 KiB cap, duplicate members refused) and its member <em>names</em> are checked here, so an unknown field is a 422
/// naming it before a command runs. The member <em>values</em> are validated by the command, which is where the
/// allocator, the writer and the steps schema already sit.
/// </para>
/// <para>
/// The coverage members of route 12 (<c>linked_live_count</c>, <c>linked_case_ids</c>, the <c>covered</c> filter) and the
/// link routes 20/21 read <c>manual_case_link</c>. "Covered" is authoring coverage — the manual case is asserted to be
/// proven by a live automated test — and it is not the execution coverage of the run report; the two numbers are never
/// presented as one. The <c>links</c> member of route 14 is still absent.
/// </para>
/// </remarks>
internal static class AuthoringManualCaseEndpoints
{
    /// <summary>Maps the manual test case routes onto the authoring group.</summary>
    /// <param name="authoring">The <c>/v1/projects/{p}/authoring</c> group the host already created.</param>
    internal static void MapAuthoringManualCaseEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        authoring.MapGet("/manual-cases", ListAsync)
            .Produces<ManualCaseListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Keyset page of a project's manual test cases, order (code, id) (13.1.1 §5 route 12)");

        // ASP.NET derives a response for the default status (200) from the delegate's return type whenever the declared
        // success status is not 200, and that type is the C8 `Result<T>` wrapper — never the wire shape. The bodiless
        // `.Produces(200)` replaces that inferred response so the wrapper never reaches the served OpenAPI document
        // (`ServedDocumentTests` diffs the served schema names against `contracts/openapi.yaml`).
        authoring.MapPost("/manual-cases", CreateAsync)
            .Accepts<CreateManualCaseRequest>("application/json")
            .Produces<CreateManualCaseResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Create a manual test case; the code is allocated per 13.1.1 §4 (route 13)");

        authoring.MapGet("/manual-cases/{m}", GetAsync)
            .Produces<ManualCaseDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("One manual test case with its steps and an ETag (route 14)");

        authoring.MapPatch("/manual-cases/{m}", PatchAsync)
            .Accepts<UpdateManualCaseRequest>("application/json")
            .Produces<AuthoringChangeResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Change a manual test case; code and item are immutable here (route 15)");

        authoring.MapDelete("/manual-cases/{m}", DeleteAsync)
            .Accepts<DeleteAuthoringRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status409Conflict)
            .WithSummary("Soft-delete a manual test case (route 16)");

        authoring.MapPost("/manual-cases/{m}/state", SetStateAsync)
            .Accepts<SetManualCaseStateRequest>("application/json")
            .Produces<SetManualCaseStateResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Move a manual test case through draft/approved/retired (route 17, AC21)");

        authoring.MapPost("/manual-cases/{m}/item", MoveAsync)
            .Accepts<MoveManualCaseRequest>("application/json")
            .Produces<MoveManualCaseResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Re-parent a manual test case to another item of the project (route 19, AC22)");

        authoring.MapPost("/manual-cases/{m}/links", LinkAsync)
            .Accepts<LinkManualCaseRequest>("application/json")
            .Produces<LinkManualCaseResponse>(StatusCodes.Status201Created)
            .Produces<LinkManualCaseResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Link a manual test case to a live catalog test case (route 20, AC11)");

        authoring.MapDelete("/manual-cases/{m}/links/{c}", UnlinkAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithSummary("Remove a manual test case's link to a catalog test case (route 21)");
    }

    /// <summary>Route 12: the page of cases matching the query's filters.</summary>
    private static async Task<Result<ManualCaseListResponse>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<ManualCaseListResponse>(limitError);
        }

        if (AuthoringEndpointText.OptionalId(context.Request.Query, "node_id", out var nodeId) is { } nodeError)
        {
            return Result.Failure<ManualCaseListResponse>(nodeError);
        }

        if (AuthoringEndpointText.OptionalId(context.Request.Query, "item_id", out var itemId) is { } itemError)
        {
            return Result.Failure<ManualCaseListResponse>(itemError);
        }

        // AC12: authoring coverage, not the execution coverage of another module's report. An unfiltered page is the
        // default; an explicit value narrows to linked (true) or unlinked (false) cases.
        if (AuthoringEndpointText.OptionalBoolean(context.Request.Query, "covered", out var covered) is { } coveredError)
        {
            return Result.Failure<ManualCaseListResponse>(coveredError);
        }

        return await mediator.Send(
            new ListManualCasesQuery(
                p,
                nodeId,
                itemId,
                AuthoringEndpointText.Blank(context.Request.Query["state"]),
                AuthoringEndpointText.Blank(context.Request.Query["priority"]),
                covered,
                AuthoringEndpointText.Blank(context.Request.Query["q"]),
                AuthoringEndpointText.Blank(context.Request.Query["cursor"]),
                limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 13: create one case and answer 201 with its id, code and version.</summary>
    private static async Task<Result<CreateManualCaseResponse>> CreateAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure<CreateManualCaseResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ManualCaseBody.CreateMembers, "manual test case") is { } unknown)
        {
            return Result.Failure<CreateManualCaseResponse>(unknown);
        }

        var result = await mediator.Send(
            new CreateManualCaseCommand(p, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    /// <summary>Route 14: one case, with the <c>ETag</c> a later <c>If-Match</c> sends back.</summary>
    private static async Task<Result<ManualCaseDetailDto>> GetAsync(
        Guid p,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await mediator.Send(new GetManualCaseQuery(p, m), cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    /// <summary>Route 15: change the case, or answer <c>changed: false</c> when the snapshot is unchanged.</summary>
    private static async Task<Result<AuthoringChangeResponse>> PatchAsync(
        Guid p,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.IfMatchVersion(context, out var version) is { } ifMatchError)
        {
            return Result.Failure<AuthoringChangeResponse>(ifMatchError);
        }

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure<AuthoringChangeResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ManualCaseBody.UpdateMembers, "manual test case") is { } unknown)
        {
            return Result.Failure<AuthoringChangeResponse>(unknown);
        }

        var result = await mediator.Send(
            new UpdateManualCaseCommand(p, m, version, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    /// <summary>Route 16: soft delete; the body is the shared optional <c>{reason?}</c>.</summary>
    private static async Task<Result> DeleteAsync(
        Guid p,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.IfMatchVersion(context, out var version) is { } ifMatchError)
        {
            return Result.Failure(ifMatchError);
        }

        var (body, bodyError) = await AuthoringEndpointSupport.ReadOptionalBodyAsync(context, cancellationToken).ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure(bodyError);
        }

        if (body.ValueKind == System.Text.Json.JsonValueKind.Undefined)
        {
            return await mediator.Send(
                new DeleteManualCaseCommand(p, m, version, null, AuthoringEndpointSupport.IdempotencyKey(context)),
                cancellationToken).ConfigureAwait(false);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ["reason"], "manual test case delete") is { } unknown)
        {
            return Result.Failure(unknown);
        }

        if (ManualCaseBody.OptionalReason(body, out var reason) is { } reasonError)
        {
            return Result.Failure(reasonError);
        }

        return await mediator.Send(
            new DeleteManualCaseCommand(p, m, version, reason, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 17: the state move.</summary>
    private static async Task<Result<SetManualCaseStateResponse>> SetStateAsync(
        Guid p,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.IfMatchVersion(context, out var version) is { } ifMatchError)
        {
            return Result.Failure<SetManualCaseStateResponse>(ifMatchError);
        }

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure<SetManualCaseStateResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ManualCaseBody.StateMembers, "manual test case state") is { } unknown)
        {
            return Result.Failure<SetManualCaseStateResponse>(unknown);
        }

        var result = await mediator.Send(
            new SetManualCaseStateCommand(p, m, version, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    /// <summary>Route 19: re-parent the case, keeping its code (AC22).</summary>
    private static async Task<Result<MoveManualCaseResponse>> MoveAsync(
        Guid p,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.IfMatchVersion(context, out var version) is { } ifMatchError)
        {
            return Result.Failure<MoveManualCaseResponse>(ifMatchError);
        }

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure<MoveManualCaseResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ManualCaseBody.MoveMembers, "manual test case move") is { } unknown)
        {
            return Result.Failure<MoveManualCaseResponse>(unknown);
        }

        var result = await mediator.Send(
            new MoveManualTestCaseCommand(p, m, version, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    /// <summary>Route 20: link the case to a live catalog test case; a repeat is the documented no-op.</summary>
    private static async Task<Result<LinkManualCaseResponse>> LinkAsync(
        Guid p,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        if (bodyError is not null)
        {
            return Result.Failure<LinkManualCaseResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ManualCaseBody.LinkMembers, "manual test case link") is { } unknown)
        {
            return Result.Failure<LinkManualCaseResponse>(unknown);
        }

        return await mediator.Send(
            new LinkManualCaseCommand(p, m, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 21: remove one link; a pair that is not linked is a 404.</summary>
    private static async Task<Result> UnlinkAsync(
        Guid p,
        Guid m,
        Guid c,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await mediator.Send(
            new UnlinkManualCaseCommand(p, m, c, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }
}
