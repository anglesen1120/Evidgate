using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>The link and coverage routes of a requirement; the route group and the shared helpers are in the other part.</summary>
internal static partial class AuthoringRequirementEndpoints
{
    private static readonly string[] LinkMembers = ["checklist_item_id", "manual_test_case_id", "expected_version_id"];

    /// <summary>The longest case code a coverage cursor may carry; a code is 10 characters, so more is never one we issued.</summary>
    private const int MaximumAfterCodeLength = 64;

    private static void MapLinkRoutes(RouteGroupBuilder authoring)
    {
        authoring.MapGet("/requirements/{r}/coverage", GetCoverageAsync)
            .Produces<RequirementCoverageResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("The cases a requirement reaches, its pinned targets and stale counts, one keyset page of cases");

        // The bodiless `.Produces(200)` keeps the C8 `Result<T>` wrapper out of the served OpenAPI document.
        authoring.MapPost("/requirements/{r}/links", CreateLinkAsync)
            .Accepts<CreateRequirementLinkRequest>("application/json")
            .Produces<RequirementLinkResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Pin one checklist item or manual case to the head version of a requirement");

        authoring.MapDelete("/requirements/{r}/links/{l}", DeleteLinkAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Remove one link of a requirement");
    }

    private static async Task<Result<RequirementCoverageResponse>> GetCoverageAsync(
        Guid p,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var requestError = AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit);
        var afterCode = AuthoringEndpointText.Blank(context.Request.Query["after_code"]);
        if (requestError is null && afterCode is { Length: > MaximumAfterCodeLength })
        {
            requestError = AuthoringErrors.Invalid("/after_code", "invalid", "after_code is not a case code this list issued");
        }

        // The pipeline authorizes the caller before the handler answers the query error.
        return await mediator.Send(
            new GetRequirementCoverageQuery(p, r, afterCode, limit, requestError),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<RequirementLinkResponse>> CreateLinkAsync(
        Guid p,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure<RequirementLinkResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, LinkMembers, "requirement link") is { } unknown)
        {
            return Result.Failure<RequirementLinkResponse>(unknown);
        }

        var itemError = ReadId(body, "checklist_item_id", out var item);
        var manualError = ReadId(body, "manual_test_case_id", out var manual);
        var expectedError = ReadId(body, "expected_version_id", out var expected);
        var idError = itemError ?? manualError ?? expectedError;
        if (idError is not null)
        {
            return Result.Failure<RequirementLinkResponse>(idError);
        }

        return await mediator.Send(
            new LinkRequirementCommand(p, r, item, manual, expected, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result> DeleteLinkAsync(
        Guid p,
        Guid r,
        Guid l,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        return await mediator.Send(
            new UnlinkRequirementCommand(p, r, l, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an optional uuid member: absent or <c>null</c> is no value, anything else must be a non-empty uuid string.</summary>
    private static Error? ReadId(JsonElement body, string member, out Guid? value)
    {
        value = null;
        if (!body.TryGetProperty(member, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String && AuthoringEndpointSupport.TryParseGuid(element.GetString(), out var id) && id != Guid.Empty)
        {
            value = id;

            return null;
        }

        return AuthoringErrors.Invalid("/" + member, "invalid", member + " must be a uuid");
    }
}
