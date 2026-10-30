using System.Text.Json;
using Evidgate.Api.Endpoints;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Testing;

/// <summary>
/// One anonymous, database-free JSON route for the transport suites (correlation,
/// envelope, endpoint filters). It stands in for the removed slice route so those
/// tests keep exercising the real host pipeline — request id, envelope middleware,
/// <see cref="ResultEndpointFilter"/> — instead of a hand-built context.
/// </summary>
internal sealed class TransportProbeStartupFilter : IStartupFilter
{
    /// <summary>The probe's only route: a POST whose body must carry a non-empty <c>project_ref</c>.</summary>
    public const string Path = "/v1/test/transport";

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // The host has already mapped its own routes; the probe joins them so routing,
        // endpoint filters and the envelope middleware apply to it exactly as to a module route.
        next(app);

        if (app is IEndpointRouteBuilder endpoints)
        {
            Map(endpoints);
            return;
        }

        app.UseEndpoints(Map);
    };

    private static void Map(IEndpointRouteBuilder endpoints) => endpoints
        .MapPost(Path, HandleAsync)
        .AddEndpointFilter<ResultEndpointFilter>()
        .ExcludeFromDescription();

    private static async Task<Result<TransportProbeResponse>> HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        // The same helper a module handler uses, so the trace-join assertion has the child span
        // a real mediator handler would export; the module tag names this probe, not a module.
        using var activity = EvidgateTelemetry.StartModuleActivity("transport-probe", "TransportProbe");

        JsonElement body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException)
        {
            // Kestrel's own status (413 for an oversized body) is preserved by the envelope middleware.
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<TransportProbeResponse>(Error.BadRequest("the request body is not valid JSON"));
        }

        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("project_ref", out var project)
            || project.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(project.GetString()))
        {
            return Result.Failure<TransportProbeResponse>(Error.Invalid(
                "project_ref is required", [new ErrorDetail("project_ref", "required")]));
        }

        return Result.Success(new TransportProbeResponse(true));
    }
}

/// <summary>The probe's success body: one member, so the wire is trivially snake_case.</summary>
internal sealed record TransportProbeResponse(bool Accepted);
