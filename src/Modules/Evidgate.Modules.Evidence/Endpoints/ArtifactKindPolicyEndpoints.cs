using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application.Artifacts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>
/// The per-kind capture-policy override route. The body is a closed object of the row's five knobs, read here under a
/// small cap; the shape rules live in the command's validator and the named refusals in its handler, while
/// authorization, the tenant transaction and the envelope stay the pipeline's and the host's.
/// </summary>
public static class ArtifactKindPolicyEndpoints
{
    /// <summary>Five members, two of them lists; the cap is generous and still bounds the read before parsing.</summary>
    private const int MaxBodyBytes = 8 * 1024;

    /// <summary>Maps the route and returns the <c>/v1</c> group so the host can apply its filters.</summary>
    public static RouteGroupBuilder MapArtifactKindPolicyEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("artifacts");

        group.MapPut("/projects/{p:guid}/artifact-kind-policy/{kind}", SetArtifactKindPolicyAsync)
            .Produces<ArtifactKindPolicyView>(StatusCodes.Status200OK)
            .WithSummary("Override one artifact kind's capture policy for a project");

        return group;
    }

    /// <summary>
    /// <c>PUT /v1/projects/{p}/artifact-kind-policy/{kind}</c>: reads the closed body, then dispatches the upsert.
    /// An unknown kind is the handler's 422 <c>kind_unknown</c> rather than a route constraint, so the refusal
    /// carries the 00 §9.2 code a client can act on.
    /// </summary>
    private static async Task<Result<ArtifactKindPolicyView>> SetArtifactKindPolicyAsync(
        Guid p,
        string kind,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return Result.Failure<ArtifactKindPolicyView>(new Error(
                ApiErrorCode.RequestInvalid,
                "the request body must be application/json",
                StatusCodes.Status415UnsupportedMediaType));
        }

        if (context.Request.ContentLength is { } declared && declared > MaxBodyBytes)
        {
            return Result.Failure<ArtifactKindPolicyView>(new Error(
                ApiErrorCode.PayloadTooLarge,
                "the request body is too large",
                StatusCodes.Status413PayloadTooLarge));
        }

        ArtifactKindPolicyBody? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<ArtifactKindPolicyBody>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<ArtifactKindPolicyView>(Error.BadRequest("the request body is not valid JSON"));
        }

        if (body is null)
        {
            return Result.Failure<ArtifactKindPolicyView>(Error.BadRequest("the request body is required"));
        }

        return await mediator.Send(
            new SetArtifactKindPolicyOverride(
                p,
                kind,
                body.MaxBytes,
                body.MediaTypes is { Length: > 0 } mediaTypes ? mediaTypes : [],
                body.DefaultSensitivity,
                body.CaptureEnabled,
                body.RejectOnSecret),
            cancellationToken).ConfigureAwait(false);
    }
}
