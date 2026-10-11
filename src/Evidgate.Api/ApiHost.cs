using Evidgate.Api.Endpoints;
using Evidgate.Api.Json;
using Evidgate.Api.Middleware;
using Evidgate.Api.Tenancy;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence;
using Evidgate.BuildingBlocks.Persistence.Health;
using Evidgate.BuildingBlocks.Persistence.Isolation;
using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.Persistence.Secrets;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Evidgate.Modules.Catalog;
using Evidgate.Modules.Evidence;
using Evidgate.Modules.Identity;
using Evidgate.Modules.Identity.Endpoints;
using Evidgate.Modules.Ingestion;
using Evidgate.Modules.Orchestrator;
using Evidgate.Modules.Policy;
using Evidgate.Modules.Catalog.Endpoints;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Orchestrator.Endpoints;
using Evidgate.Modules.Policy.Endpoints;
using Evidgate.Modules.Tenancy;
using Evidgate.Modules.Tenancy.Endpoints;
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
        // 10.1.1-T2: the single secret seam. Idempotent (TryAdd*), so the
        // composition extensions that also register it change nothing here.
        builder.Services.AddEvidgateSecrets(builder.Configuration);

        var otel = builder.AddEvidgateTelemetry(ServiceName);
        otel.WithTracing(tracing => tracing.AddAspNetCoreInstrumentation());
        otel.WithMetrics(metrics => metrics.AddPrometheusExporter());

        builder.Services.AddHealthChecks()
            .AddCheck<ReadinessHealthCheck>(ReadinessHealthCheck.Name, tags: [ReadinessHealthCheck.Name]);

        // 02.6.2-T1.1: pre-tenant resolution; credential authenticators (02.3.2, 02.4.x)
        // register ICallerAuthenticator, none exists yet so a deployed host authenticates nobody.
        builder.Services.AddSingleton<TenantResolver>();

        builder.Services.AddMediator();

        // 01.4.2-T4: the single data-access path and the C2 pipeline (00 §5.5 C2).
        // Mediator's generated implementation stays registered by the host — the
        // source generator is the entry point's, and two generated mediators would
        // shadow each other. The pipeline wires the behaviours it owns in the fixed
        // order on top of it.
        builder.Services.AddEvidgateTenancy();
        builder.Services.AddEvidgatePipeline();

        // 00 §5.5: the composition root registers the modules' slice services;
        // Mediator's source generator registered the handlers themselves.
        builder.Services.AddIngestionModule();
        builder.Services.AddCatalogModule();
        builder.Services.AddOrchestratorModule();
        // 06.1.1-T1: the evidence module — the artifact and kind-policy write model.
        builder.Services.AddEvidenceModule();
        // 07.1.1-T1: the policy module — the append-only revision write model and store.
        builder.Services.AddPolicyModule();
        // 02.2.1-T1.2/T2.2: the permission-catalogue module — reads (00 §2, 02 §4.2),
        // the flagged custom-role writes and the C2 permission evaluator, which
        // replaces the pipeline's fail-closed default.
        builder.Services.AddIdentityModule(builder.Configuration);
        // 02.1.1-T2/T3: the org/project registry — write model, validators and the
        // ITenantLimits read surface plan 12 consumes.
        builder.Services.AddTenancyModule();

        // 00 §5.5 edge: snake_case JSON through the source-generated context;
        // reflection stays enabled as a fallback for framework types.
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, EvidgateJsonContext.Default);
        });

        builder.Services.AddOpenApi();

        // 01.5.1-T4.2 (00 §9.1): the operator-ingress task routes and the
        // fail-closed gate that guards them until 02.x lands authentication.
        builder.Services.AddOperatorEndpoints();

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
        app.MapOperatorEndpoints();
        // 02.2.1-T1.2 (00 §5.5 C8): the module maps its /v1 read routes and the host
        // applies the envelope filter, so a Result becomes the 00 §9.2 body once.
        app.MapIdentityEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 02.1.2-T1.2 (00 §5.5 C8): the membership routes of 02.1.2 and the same
        // single Result → 00 §9.2 envelope filter.
        app.MapMembershipEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 02.1.1-T2.2 (00 §5.5 C8): the org/project registry routes, same filter.
        app.MapTenancyEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        app.MapCatalogEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 05.1.1/05.1.2: manifests and environment revisions, same filters.
        app.MapOrchestratorEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 06.1.2-T1: the artifact upload-url route, same filters.
        app.MapEvidenceEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 07.1.1-T3: the policy revision routes, same filters.
        app.MapPolicyEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 07.3.1-T1: the waiver routes of 07.3.1 §5, same filters.
        app.MapWaiverEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 07.3.1-T2: the quarantine routes of 07.3.1 §5, same filters.
        app.MapQuarantineEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 02.7.1-T3.1: the audit list and export, same filters.
        app.MapAuditEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 02.4.1/02.4.2: CI bindings and ingest tokens (admin), and the pre-tenant credential exchange, which
        // resolves its own tenant (no TenantResolutionFilter: there is no caller yet).
        app.MapCiAdminEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        app.MapCiTokenEndpoints().AddEndpointFilter<ResultEndpointFilter>();
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
        catch (Exception exception) when (IsolationSelfCheckException.FindIn(exception) is { } refusal)
        {
            // 02.6.1-T1.4 (AC5): the boot-time self-check found a table that is not
            // under database-enforced isolation, so the API must not serve tenant
            // traffic. One named line (no connection string, no stack trace) and a
            // non-zero exit; the diagnostic detail is in the Error log line the
            // hosted service already wrote.
            await Console.Error.WriteLineAsync($"Isolation self-check failed: {refusal.Message}");
            return IsolationSelfCheckExitCode;
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
    /// Exit code when the boot-time isolation self-check refuses the start
    /// (02.6.1-T1.4 AC5): the process must never serve tenant traffic on a database
    /// that stopped enforcing tenant isolation.
    /// </summary>
    public const int IsolationSelfCheckExitCode = 3;

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
        _ = app.Services.GetRequiredService<IOptions<IsolationSelfCheckOptions>>().Value;
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
