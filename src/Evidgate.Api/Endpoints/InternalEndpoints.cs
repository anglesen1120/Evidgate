using Evidgate.Api.Json;
using Evidgate.BuildingBlocks.Persistence.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// Operational endpoints of 00 §9.1 (01.1.2-T1.3): <c>/internal/health/live</c>,
/// <c>/internal/health/ready</c> and <c>/metrics</c> live outside <c>/v1</c> and
/// are excluded from the served OpenAPI document (AC7).
/// </summary>
public static class InternalEndpoints
{
    /// <summary>Maps the operational endpoints outside /v1, excluded from the OpenAPI document.</summary>
    public static void MapInternalEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // 00 §9.1: live means only "the process is up" — no checks run.
        app.MapHealthChecks(
                "/internal/health/live",
                new() { Predicate = _ => false, ResponseWriter = WriteHealthReport })
            .ExcludeFromDescription();

        app.MapHealthChecks(
                "/internal/health/ready",
                new()
                {
                    Predicate = check => check.Name == ReadinessHealthCheck.Name,
                    ResponseWriter = WriteHealthReport,
                })
            .ExcludeFromDescription();

        app.MapPrometheusScrapingEndpoint()
            .ExcludeFromDescription();
    }

    private static async Task WriteHealthReport(HttpContext context, HealthReport report)
    {
        HealthReportEntry? ready = report.Entries.TryGetValue(ReadinessHealthCheck.Name, out var entry)
            ? entry
            : null;

        var dto = new HealthReportDto(
            Status: ready is null ? "live" : (ready.Value.Status == HealthStatus.Healthy ? "ready" : "unready"),
            Reason: ready?.Data.TryGetValue("reason", out var reason) == true ? reason as string : null,
            JournalHead: ready?.Data.TryGetValue("journal_head", out var head) == true ? head as string : null,
            ExpectedHead: ready?.Data.TryGetValue("expected_head", out var expected) == true ? expected as string : null);

        // 00 §9.2: a 503 carries Retry-After.
        if (context.Response.StatusCode == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.RetryAfter = "5";
        }

        await context.Response.WriteAsJsonAsync(
            dto,
            ApiJsonContext.Default.HealthReportDto,
            "application/json",
            context.RequestAborted);
    }
}

/// <summary>Body of the operational endpoints (lower_snake_case on the wire).</summary>
public sealed record HealthReportDto(string Status, string? Reason, string? JournalHead, string? ExpectedHead);
