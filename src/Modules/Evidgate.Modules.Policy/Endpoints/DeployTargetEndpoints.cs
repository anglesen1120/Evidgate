using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Evidgate.Modules.Policy.Application.DeployTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Policy.Endpoints;

/// <summary>
/// The deploy-target routes. The delegates only bind 00 §9.1's parameters (<c>If-Match</c>, <c>Idempotency-Key</c>,
/// the body) and answer in <see cref="Result"/> terms: authorization is the pipeline, the tenant is the request's, and
/// the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>. Every success carries the row version as <c>ETag</c>.
/// </summary>
public static class DeployTargetEndpoints
{
    /// <summary>Maps the deploy-target routes and returns the <c>/v1</c> group so the host can apply its filters.</summary>
    public static RouteGroupBuilder MapDeployTargetEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("policy");

        group.MapGet("/projects/{p}/deploy-targets", ListAsync)
            .Produces<DeployTargetPage>(StatusCodes.Status200OK)
            .WithSummary("A project's deploy targets in display order");

        group.MapPost("/projects/{p}/deploy-targets", CreateAsync)
            .Accepts<DeployTargetCreateRequest>("application/json")
            .Produces<DeployTargetDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create a deploy target; a duplicate name (any case) is 409 target_exists");

        group.MapGet("/projects/{p}/deploy-targets/{t}", GetAsync)
            .Produces<DeployTargetDto>(StatusCodes.Status200OK)
            .WithSummary("One deploy target, with its ETag");

        group.MapPatch("/projects/{p}/deploy-targets/{t}", UpdateAsync)
            .Accepts<JsonElement>("application/json")
            .Produces<DeployTargetDto>(StatusCodes.Status200OK)
            .WithSummary("Change a deploy target; If-Match is required, a stale one is 412");

        group.MapDelete("/projects/{p}/deploy-targets/{t}", DisableAsync)
            .Produces<DeployTargetDto>(StatusCodes.Status200OK)
            .WithSummary("Disable a deploy target (the row is kept); If-Match is required");

        return group;
    }

    private static async Task<Result<DeployTargetPage>> ListAsync(Guid p, IMediator mediator, CancellationToken cancellationToken) =>
        await mediator.Send(new ListDeployTargetsQuery(p), cancellationToken).ConfigureAwait(false);

    private static async Task<Result<DeployTargetDto>> CreateAsync(
        Guid p,
        DeployTargetCreateRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        WithETag(
            context,
            await mediator.Send(new CreateDeployTargetCommand(p, request.Name, request.Order, request.PolicyRevisionId, IdempotencyKey(context)), cancellationToken)
                .ConfigureAwait(false));

    private static async Task<Result<DeployTargetDto>> GetAsync(Guid p, Guid t, HttpContext context, IMediator mediator, CancellationToken cancellationToken) =>
        WithETag(context, await mediator.Send(new GetDeployTargetQuery(p, t), cancellationToken).ConfigureAwait(false));

    private static async Task<Result<DeployTargetDto>> UpdateAsync(
        Guid p,
        Guid t,
        JsonElement body,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (DeployTargetPreconditions.IfMatch(context.Request.Headers.IfMatch.ToString(), out var version) is { } header)
        {
            return Result.Failure<DeployTargetDto>(header);
        }

        if (ReadPatch(body, out var name, out var order, out var pinGiven, out var pin, out var state) is { } invalid)
        {
            return Result.Failure<DeployTargetDto>(invalid);
        }

        return WithETag(
            context,
            await mediator.Send(new UpdateDeployTargetCommand(p, t, version, name, order, pinGiven, pin, state, IdempotencyKey(context)), cancellationToken)
                .ConfigureAwait(false));
    }

    private static async Task<Result<DeployTargetDto>> DisableAsync(Guid p, Guid t, HttpContext context, IMediator mediator, CancellationToken cancellationToken)
    {
        if (DeployTargetPreconditions.IfMatch(context.Request.Headers.IfMatch.ToString(), out var version) is { } header)
        {
            return Result.Failure<DeployTargetDto>(header);
        }

        return WithETag(
            context,
            await mediator.Send(new DisableDeployTargetCommand(p, t, version, IdempotencyKey(context)), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Reads the PATCH body by hand: JSON null for <c>policy_revision_id</c> clears the pin, which a typed record could
    /// not tell apart from an absent member. Unknown members and wrong JSON types are 422, never ignored.
    /// </summary>
    private static Error? ReadPatch(JsonElement body, out string? name, out int? order, out bool pinGiven, out Guid? pin, out string? state)
    {
        name = null;
        order = null;
        pinGiven = false;
        pin = null;
        state = null;

        if (body.ValueKind != JsonValueKind.Object)
        {
            return Bad(string.Empty, "invalid_type");
        }

        if (UnstorableJsonText.HasUnstorableText(body))
        {
            return Bad(string.Empty, "unstorable_text");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in body.EnumerateObject())
        {
            var value = member.Value;
            if (!seen.Add(member.Name))
            {
                return Bad(member.Name, "duplicate_field");
            }

            switch (member.Name)
            {
                case "name" when value.ValueKind == JsonValueKind.String:
                    name = value.GetString();
                    break;
                case "state" when value.ValueKind == JsonValueKind.String:
                    state = value.GetString();
                    break;
                case "order" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed):
                    order = parsed;
                    break;
                case "policy_revision_id" when value.ValueKind == JsonValueKind.Null:
                    pinGiven = true;
                    break;
                case "policy_revision_id" when value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 36 } text && Guid.TryParseExact(text, "D", out var id):
                    pinGiven = true;
                    pin = id;
                    break;
                case "name" or "state" or "order" or "policy_revision_id":
                    return Bad(member.Name, "invalid_type");
                default:
                    return Bad(member.Name, "unknown_field");
            }
        }

        return null;
    }

    private static Error Bad(string path, string reason) =>
        Error.Invalid("the deploy target patch is not valid", [new ErrorDetail(path, reason)]);

    private static Result<DeployTargetDto> WithETag(HttpContext context, Result<DeployTargetDto> result)
    {
        if (result.IsSuccess)
        {
            context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{result.Value.Version}\"");
        }

        return result;
    }

    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
}
