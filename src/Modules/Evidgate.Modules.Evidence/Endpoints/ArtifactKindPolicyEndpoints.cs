using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application.Artifacts;
using Evidgate.Modules.Evidence.Domain;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>
/// The per-kind capture-policy override route. The body is a closed object of the row's five knobs, read here under a
/// small cap that counts the bytes as they arrive; the shape rules live in the command's validator and the named
/// refusals in its handler, while authorization, the tenant transaction and the envelope stay the pipeline's and the
/// host's.
/// </summary>
public static class ArtifactKindPolicyEndpoints
{
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

        if (context.Request.ContentLength is { } declared && declared > UploadUrlLimits.MaxBodyBytes)
        {
            return Result.Failure<ArtifactKindPolicyView>(CappedRequestBody.TooLarge);
        }

        // A chunked or length-less body declares nothing the check above could see, so the read itself bounds it:
        // the cap holds for the bytes that actually arrive, and no byte past it reaches the parser.
        ArtifactKindPolicyBody? body;
        var inner = context.Request.Body;
        try
        {
            context.Request.Body = new CappedBodyStream(inner, UploadUrlLimits.MaxBodyBytes);
            body = await context.Request.ReadFromJsonAsync<ArtifactKindPolicyBody>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
        {
            return Result.Failure<ArtifactKindPolicyView>(CappedRequestBody.Unreadable(exception));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<ArtifactKindPolicyView>(Error.BadRequest("the request body is not valid JSON"));
        }
        finally
        {
            context.Request.Body = inner;
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
