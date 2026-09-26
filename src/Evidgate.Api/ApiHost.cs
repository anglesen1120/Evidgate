using Evidgate.Api.Endpoints;
using Evidgate.Api.Json;
using Evidgate.Api.Middleware;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence;
using Evidgate.BuildingBlocks.Persistence.Health;
using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Evidgate.Modules.Catalog;
using Evidgate.Modules.Ingestion;
using Evidgate.Modules.Orchestrator;
using Mediator;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Evidgate.Api;

/// <summary>
/// Composition root of the HTTP entry point (00 §5.1 A1). Split from
/// <c>Program.cs</c> so <c>tests/Hosts.IntegrationTests</c> builds the identical
/// pipeline (AC5) instead of a re-implemented one.
/// </summary>
public static partial class ApiHost
{
    /// <summary>10 s shutdown timeout per 01.1.2-T1.4 (AC1).</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private const string ServiceName = "evidgate-api";

    /// <summary>Builds the host builder: services and options only, no HTTP pipeline yet.</summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);

        builder.Services.AddEvidgatePersistence(builder.Configuration);

        var otel = builder.AddEvidgateTelemetry(ServiceName);
        otel.WithTracing(tracing => tracing.AddAspNetCoreInstrumentation());
        otel.WithMetrics(metrics => metrics.AddPrometheusExporter());

        builder.Services.AddHealthChecks()
            .AddCheck<ReadinessHealthCheck>(ReadinessHealthCheck.Name, tags: [ReadinessHealthCheck.Name]);

        builder.Services.AddMediator();

        // 00 §5.5: the composition root registers the modules' slice services;
        // Mediator's source generator registered the handlers themselves.
        builder.Services.AddIngestionModule();
        builder.Services.AddCatalogModule();
        builder.Services.AddOrchestratorModule();

        // 00 §5.5 edge: snake_case JSON through the source-generated context;
        // reflection stays enabled as a fallback for framework types.
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, EvidgateJsonContext.Default);
        });

        builder.Services.AddOpenApi();

        return builder;
    }

    /// <summary>Builds the application: pipeline, internal + slice routes, OpenAPI document.</summary>
    public static WebApplication BuildApp(string[] args)
    {
        var app = CreateBuilder(args).Build();

        app.UseMiddleware<RequestIdMiddleware>();
        app.UseMiddleware<EnvelopeExceptionMiddleware>();

        // D6: a route/method the pipeline rejects on its own (404/405) leaves the
        // response body-less, so wrap those statuses in the 00 §9.2 envelope too.
        app.UseStatusCodePages(async statusCodeContext =>
        {
            var status = statusCodeContext.HttpContext.Response.StatusCode;
            await ErrorEnvelopeWriter.WriteAsync(
                statusCodeContext.HttpContext,
                status,
                ErrorEnvelopeWriter.CodeForStatus(status),
                ErrorEnvelopeWriter.MessageForStatus(status));
        });

        app.MapInternalEndpoints();
        app.MapSliceEndpoints();
        app.MapOpenApi();

        return app;
    }

    /// <summary>Runs the Api; returns the process exit code (non-zero on boot validation failure, AC2).</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            await using var app = BuildApp(args);
            ValidateOptions(app);
            LogBootConfiguration(app);
            await app.RunAsync();
            return 0;
        }
        catch (OptionsValidationException exception)
        {
            // 00 §11 NFR-01-2: a missing/invalid EVIDGATE__* value must name the key
            // and exit non-zero, without a stack trace leaking into the log.
            await Console.Error.WriteLineAsync($"Configuration invalid: {exception.Message}");
            return ConfigurationExitCode;
        }
        catch (InvalidOperationException exception)
        {
            // D4: an *invalid* value (e.g. EVIDGATE__TELEMETRY__OTLPENDPOINT='not a url'
            // reaching the binder) must take the same clean, stack-trace-free path as a
            // missing one instead of escaping as an unhandled exception.
            await Console.Error.WriteLineAsync($"Configuration invalid: {exception.Message}");
            return ConfigurationExitCode;
        }
    }

    /// <summary>Exit code for a missing or invalid <c>EVIDGATE__*</c> value (NFR-01-2).</summary>
    public const int ConfigurationExitCode = 1;

    /// <summary>
    /// Materialises every validated option group before the host starts. The same
    /// validation runs on start anyway (<c>ValidateOnStart</c>), but by then the
    /// failure escapes <c>RunAsync</c> and the host logs it as a critical "Hosting
    /// failed to start" with a full stack trace — 00 §11 NFR-01-2 wants one named
    /// line and no stack trace instead.
    /// </summary>
    private static void ValidateOptions(WebApplication app)
    {
        _ = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        _ = app.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
    }

    /// <summary>
    /// Boot line that deliberately carries the bound connection string — the
    /// redaction layer (AC4, 00 §5.7 S3) must mask it wherever it appears. The
    /// <c>Authorization</c> header side is logged by <see cref="RequestIdMiddleware"/>
    /// per request; keep both, tests assert the mask.
    /// </summary>
    private static void LogBootConfiguration(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiHost));
        var database = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        logger.LogBootConnectionString(database.ConnectionString);
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Boot configuration bound: database connection string {DatabaseConnectionString}")]
    private static partial void LogBootConnectionString(this ILogger logger, string databaseConnectionString);
}
