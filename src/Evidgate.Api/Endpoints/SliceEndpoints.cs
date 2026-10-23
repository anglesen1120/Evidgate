using System.Text.Json;
using Evidgate.Api.Json;
using Evidgate.Api.Middleware;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Orchestrator.Application.Commands;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Evidgate.Modules.Policy.Application.Queries;
using Mediator;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// The sprint-1 skeleton slice (01.1.2-T3.2): one chain across real module
/// boundaries — ingest manifest → create run → plan executions → accept an event
/// batch → completeness stub → decision stub. It's a dev/testing surface: under
/// <c>/internal/slice</c> it stays out of the served OpenAPI document (AC7); the
/// <c>/v1/slice</c> mount exists so a bad request proves the 00 §9.2 envelope on
/// <c>/v1</c> (AC7) before 04/05 replace this with the real routes.
/// </summary>
public static class SliceEndpoints
{
    /// <summary>Maps the slice route on both mounts (internal dev surface + the /v1 envelope check).</summary>
    public static void MapSliceEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapRun(app.MapGroup("/internal/slice"), excludeFromDescription: true);
        MapRun(app.MapGroup("/v1/slice"), excludeFromDescription: false);
    }

    private static void MapRun(RouteGroupBuilder group, bool excludeFromDescription)
    {
        // 01.4.2-T4.4 (C8): the delegate answers in Result terms and the filter
        // renders the 00 §9.2 envelope — the shape every real route takes.
        // Produces<T> keeps the SERVED document honest: the filter unwraps
        // Result<T>, so the wire body is the slice DTO, not the wrapper the
        // delegate's return type would otherwise document.
        var endpoint = group.MapPost("/run", HandleRunAsync)
            .Produces<SliceRunResponse>(StatusCodes.Status200OK)
            .AddEndpointFilter<ResultEndpointFilter>();

        if (excludeFromDescription)
        {
            endpoint.ExcludeFromDescription();
        }
    }

    /// <summary>
    /// Runs the slice chain. The body is read explicitly (not bound by the
    /// request-delegate factory) so a bad body always produces the 00 §9.2
    /// envelope — the framework's own binding failure writes a body-less 400 under
    /// Kestrel, which would violate AC7. Every body-read failure mode is a client
    /// error: malformed JSON, an unsupported/missing content type (which throws
    /// <see cref="InvalidOperationException"/>), and Kestrel's own
    /// <see cref="BadHttpRequestException"/> (whose status, e.g. 413, is preserved).
    /// </summary>
    private static async Task<Result<SliceRunResponse>> HandleRunAsync(
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        SliceRunRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ApiJsonContext.Default.SliceRunRequest,
                cancellationToken);
        }
        catch (BadHttpRequestException exception)
        {
            var status = exception.StatusCode;
            return Result.Failure<SliceRunResponse>(new Error(
                ErrorEnvelopeWriter.CodeForStatus(status),
                ErrorEnvelopeWriter.MessageForStatus(status),
                status));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<SliceRunResponse>(
                Error.BadRequest("the request body is not valid JSON"));
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ProjectRef))
        {
            return Result.Failure<SliceRunResponse>(Error.Invalid(
                "project_ref is required",
                [new ErrorDetail("project_ref", "required")]));
        }

        var manifest = await mediator.Send(
            new IngestManifest(request.ProjectRef, request.SourceRef ?? "slice://manifest", request.ManifestContent ?? "{}"),
            cancellationToken);

        var run = await mediator.Send(new CreateRun(request.ProjectRef, manifest.ManifestId), cancellationToken);

        var plan = await mediator.Send(new PlanExecutions(run.RunId), cancellationToken);

        var batch = await mediator.Send(
            new AcceptEventBatch(run.RunId, request.EventCount is null or < 1 ? 2 : request.EventCount.Value),
            cancellationToken);

        var completeness = await mediator.Send(new GetCompleteness(run.RunId), cancellationToken);

        var decision = await mediator.Send(new GetDecision(run.RunId), cancellationToken);

        return Result.Success(new SliceRunResponse(
            manifest.ManifestId,
            manifest.ManifestHash,
            run.RunId,
            plan.ExecutionIds,
            batch.AcceptedCount,
            completeness.Completeness,
            decision.Decision));
    }
}
