using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The requirement intake routes under <c>/v1/projects/{p}/authoring/intakes</c>: create one intake and read where it stands.
/// Both address the project in the path; the C2 pipeline decides the permission and the tenant before a handler runs.
/// </summary>
internal static class AuthoringIntakeEndpoints
{
    /// <summary>The body cap of the create route: a text intake is at most 200 000 code points, which is under 1 MiB of UTF-8 in JSON.</summary>
    internal const int BodyCap = 1024 * 1024;

    private static readonly string[] CreateMembers =
        ["kind", "feature_node_id", "artifact_id", "media_type", "bytes", "text", "target_language"];

    internal static void MapAuthoringIntakeEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        // The bodiless 200 replaces the response ASP.NET would infer from the Result wrapper (see the node routes).
        authoring.MapPost("/intakes", CreateAsync)
            .Accepts<CreateIntakeRequest>("application/json")
            .Produces<CreateIntakeResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create a requirement intake from text, a document or an image");

        authoring.MapGet("/intakes/{i}", GetAsync)
            .Produces<IntakeStatusResponse>(StatusCodes.Status200OK)
            .WithSummary("Read the state of one requirement intake");
    }

    private static async Task<Result<CreateIntakeResponse>> CreateAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Refuse an unresolved caller before anything reads the request.
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var (body, readError) = await AuthoringEndpointSupport.ReadBodyAsync(context, BodyCap, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<CreateIntakeResponse>(readError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, CreateMembers, "requirement intake") is { } unknown)
        {
            return Result.Failure<CreateIntakeResponse>(unknown);
        }

        return await mediator.Send(
            new CreateIntakeCommand(p, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<IntakeStatusResponse>> GetAsync(
        Guid p,
        Guid i,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        return await mediator.Send(new GetIntakeQuery(p, i), cancellationToken).ConfigureAwait(false);
    }
}
