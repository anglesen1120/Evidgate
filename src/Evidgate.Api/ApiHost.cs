using Evidgate.Api.Endpoints;
using Evidgate.Api.Forwarding;
using Evidgate.Api.Json;
using Evidgate.Api.Management;
using Evidgate.Api.Middleware;
using Evidgate.Api.Tenancy;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Evidgate.BuildingBlocks.Persistence;
using Evidgate.BuildingBlocks.Persistence.Health;
using Evidgate.BuildingBlocks.Persistence.Isolation;
using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.Persistence.Secrets;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Evidgate.Modules.Authoring;
using Evidgate.Modules.Authoring.Endpoints;
using Evidgate.Modules.Catalog;
using Evidgate.Modules.Evidence;
using Evidgate.Modules.Identity;
using Evidgate.Modules.Identity.DevLogin;
using Evidgate.Modules.Identity.Endpoints;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Ingestion;
using Evidgate.Modules.Ingestion.Endpoints;
using Evidgate.Modules.Orchestrator;
using Evidgate.Modules.Policy;
using Evidgate.Modules.Catalog.Endpoints;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Endpoints;
using Evidgate.Modules.Orchestrator.Endpoints;
using Evidgate.Modules.Policy.Endpoints;
using Evidgate.Modules.Tenancy;
using Evidgate.Modules.Tenancy.Endpoints;
using Evidgate.Modules.Usage;
using Evidgate.Modules.Usage.Endpoints;
using Mediator;
using Microsoft.Extensions.Logging;
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

        // 02.3.1 §5/§8 (S3), hand-over (viii) of #160: ASP.NET's request log — the category
        // `Microsoft.AspNetCore.Hosting.Diagnostics` — writes "Request starting … GET <url?query>" at
        // Information, and the login callback's query carries the authorization `code` and the one-time
        // `state`. The span of the same request is redacted below (`LoginTraceRedaction`), but the log line
        // cannot be: the framework composes it before any middleware of ours runs, so nothing on our side can
        // take the query out of it. That category therefore starts at Warning for the whole Api: a request
        // stays observable through the (redacted) span — method, route, status, duration — and no framework
        // log line can ever carry a code or a state again, whatever route reaches us.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);

        // N9: the host-wide body limit of a route that declares none, applied by Kestrel before a handler reads.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = RequestBodyLimitMiddleware.DefaultBodyBytes);
        ManagementListener.Configure(builder);
        builder.Services.Configure<ManagementOptions>(builder.Configuration.GetSection(ManagementOptions.SectionName));
        builder.Services.AddTrustedForwardedHeaders(builder.Configuration);

        builder.Services.AddEvidgatePersistence(builder.Configuration);
        // 10.1.1-T2: the single secret seam. Idempotent (TryAdd*), so the
        // composition extensions that also register it change nothing here.
        builder.Services.AddEvidgateSecrets(builder.Configuration);

        // The cancel route writes its kill row itself, so this host reads the same
        // EVIDGATE__TASKS__* group the Worker consumes: a producer that bound nothing would
        // write into `default` while the Worker polls elsewhere and lose the kill. Options only —
        // the claim loops and the dispatcher stay in the Worker.
        builder.Services.AddEvidgateTaskOptions(builder.Configuration);

        var otel = builder.AddEvidgateTelemetry(ServiceName);
        // 10.3.1-T1.2 (F6, documented trade-off): the server instrumentation adopts an
        // inbound W3C `traceparent`, so a caller may choose the trace id its requests join
        // and may ask for `sampled=1`; a client trace is worth keeping whole, and the head
        // sampler (group P) still governs the roots this host starts itself. What stays
        // client-controlled is bounded at the edge instead: `RequestIdMiddleware` drops an
        // oversized or non-printable `tracestate` and a planted `evidgate.run_id` baggage,
        // and `TaskCorrelation.Stamp` bounds the carrier it persists into every payload.
        otel.WithTracing(tracing =>
            tracing.AddAspNetCoreInstrumentation(options =>
                // 00 §5.7 S3 / 02.3.1 §5: the browser reaches the login callback with `code` and `state`
                // in the query string. Neither may reach a trace backend, so the query is taken off the
                // span on those two routes only (`LoginTraceRedaction`: `url.query` deleted, the query
                // stripped from `url.full`/`http.url`).
                options.EnrichWithHttpRequest = (activity, request) =>
                    LoginTraceRedaction.Redact(activity, request.Path.Value)));
        // The server request-duration histogram (http.server.request.duration) is the ingest SLI's source:
        // its p95 per route template is what the dashboards read, and the instrumentation labels a request by
        // its template rather than its URL, so no path parameter becomes a series.
        var libraryMetrics = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()?.LibraryMetrics
            ?? true;
        otel.WithMetrics(metrics =>
        {
            if (libraryMetrics)
            {
                metrics.AddAspNetCoreInstrumentation();
            }

            metrics.AddPrometheusExporter();
        });
        // 10.3.1-T1.1/T1.2: Npgsql/HttpClient client spans and the log-record
        // correlation processor on the pipeline above. The ASP.NET Core instrumentation
        // stays here, where its login-route query redaction is configured.
        otel.AddEvidgateCorrelation();

        builder.Services.AddHealthChecks()
            .AddCheck<ReadinessHealthCheck>(ReadinessHealthCheck.Name, tags: [ReadinessHealthCheck.Name]);

        // 02.6.2-T1.1: pre-tenant resolution; credential authenticators (02.3.2, 02.4.x)
        // register ICallerAuthenticator, none exists yet so a deployed host authenticates nobody.
        builder.Services.AddSingleton<TenantResolver>();

        // Scoped, not the generator's Singleton default: handlers consume the per-request tenant
        // transaction and scoped stores. The option is read at compile time, so each host sets it.
        builder.Services.AddMediator((MediatorOptions options) => options.ServiceLifetime = ServiceLifetime.Scoped);

        // 01.4.2-T4: the single data-access path and the C2 pipeline (00 §5.5 C2).
        // Mediator's generated implementation stays registered by the host — the
        // source generator is the entry point's, and two generated mediators would
        // shadow each other. The pipeline wires the behaviours it owns in the fixed
        // order on top of it.
        builder.Services.AddEvidgateTenancy();
        builder.Services.AddEvidgatePipeline();

        // 00 §5.5: the composition root registers the modules' own services;
        // Mediator's source generator registered the handlers themselves.
        builder.Services.AddIngestionModule();
        builder.Services.AddCatalogModule();
        // 13.1.1-T2.1 (D-S55-Q1): the authoring module — the feature tree's EF write model, the one version
        // writer, the code allocator and the steps validator. Api only: nothing in the Worker authors content.
        builder.Services.AddAuthoringModule();
        builder.Services.AddOrchestratorModule();
        // 06.1.1-T1: the evidence module — the artifact and kind-policy write model.
        builder.Services.AddEvidenceModule(builder.Configuration);
        // 07.1.1-T1: the policy module — the append-only revision write model and store.
        builder.Services.AddPolicyModule();
        // 08.6.3-T1: the healing module — the kill switch store, its resolver and the V1 clamp (Healing:Apply).
        builder.Services.AddHealingModule(builder.Configuration, builder.Environment);
        // 02.2.1-T1.2/T2.2: the permission-catalogue module — reads (00 §2, 02 §4.2),
        // the flagged custom-role writes and the C2 permission evaluator, which
        // replaces the pipeline's fail-closed default.
        builder.Services.AddIdentityModule(builder.Configuration);
        // 02.1.1-T2/T3: the org/project registry — write model, validators and the
        // ITenantLimits read surface plan 12 consumes.
        builder.Services.AddTenancyModule();
        // 12.3.1-T1.3: the usage module — the ledger write path (12.3.1-T1.4) and the IUsageLedger
        // surface the metering callers resolve; the operator route arrives with 12.3.1-T2.4.
        builder.Services.AddUsageModule();
        // The API validates request bodies against the contract schemas; compile them at start, not in the first request.
        builder.Services.AddEvidgateContractSchemaWarmUp();

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

    /// <summary>Builds the application pipeline, module routes and OpenAPI document.</summary>
    public static WebApplication BuildApp(string[] args)
    {
        var app = CreateBuilder(args).Build();

        // First, so every later middleware and endpoint sees the client address, not the proxy's.
        app.UseTrustedForwardedHeaders();

        // 10.3.1-T1.2 (AC1): routing is pinned to the start of the pipeline so the edge
        // knows a route's `{r}` value before it tags the request's span, whatever the
        // framework would otherwise do with its implicit `UseRouting` for an endpoint
        // app. `run_id` has to be on that span and in its baggage — the pipeline spans,
        // the ingest handler and the outbox payload carrier all inherit it from there —
        // and a middleware can only read the route value once the endpoint is matched.
        // Nothing else moves: the envelope and status-code middleware still wrap every
        // route the pipeline reaches.
        app.UseRouting();
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseMiddleware<EnvelopeExceptionMiddleware>();
        // Before any body is read: the route's limit, then the management/public listener split.
        app.UseMiddleware<ManagementPortMiddleware>();
        app.UseMiddleware<RequestBodyLimitMiddleware>();

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

        // 02.3.2-T1 (D6, AC5): host-wide, so EVERY cookie-authenticated mutation is covered without an
        // annotation — a new route included. It runs before the endpoint filters because the session it resolves
        // is what the tenant filter and the antiforgery pair both read (memoized: one statement per request).
        app.UseMiddleware<SessionXsrfMiddleware>();

        app.MapInternalEndpoints();
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
        // 13.1.1-T2 (00 §5.5 C8): the authoring tree, checklist-item and manual-case routes, same two filters.
        app.MapAuthoringEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 05.1.1/05.1.2: manifests and environment revisions, same filters.
        app.MapOrchestratorEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 06.1.2-T1: the artifact upload-url route, same filters.
        app.MapEvidenceEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 06.4.1: the per-project retention settings of the evidence area, same filters.
        app.MapRetentionEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // The per-kind capture-policy override of the evidence area, same filters.
        app.MapArtifactKindPolicyEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        app.MapIngestionEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 07.1.1-T3: the policy revision routes, same filters.
        app.MapPolicyEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 08.6.3-T1: the healing switch routes of 08.6.3 §5, same filters.
        app.MapHealingEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 07.3.1-T1: the waiver routes of 07.3.1 §5, same filters.
        app.MapWaiverEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 07.3.1-T2: the quarantine routes of 07.3.1 §5, same filters.
        app.MapQuarantineEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 02.7.1-T3.1: the audit list and export, same filters.
        app.MapAuditEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 11.5.1-T5.2 (00 §5.5 C8): the security-events read of AC3, same two filters.
        app.MapSecurityEventEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // The tenant usage summary (counters vs quota), same filters.
        app.MapUsageEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // 02.4.1/02.4.2: CI bindings and ingest tokens (admin), and the pre-tenant credential exchange, which
        // resolves its own tenant (no TenantResolutionFilter: there is no caller yet).
        app.MapCiAdminEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        // Project API tokens for automation: the same tenant and envelope filters as every /v1 tenant route.
        app.MapApiTokenEndpoints().AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>();
        app.MapCiTokenEndpoints().AddEndpointFilter<ResultEndpointFilter>();
        // 02.3.1-T1: the federated login start/callback routes. Pre-tenant as well (the browser holds no
        // session yet), and the callback resolves nothing before the provider has verified the identity, so
        // only the envelope filter applies.
        app.MapLoginEndpoints().AddEndpointFilter<ResultEndpointFilter>();
        // The development-only sign-in: the route is mapped only in a Development host that enabled it and
        // provisioned its secret, so every other environment has no such path at all. Pre-tenant as well.
        app.MapDevSession().AddEndpointFilter<ResultEndpointFilter>();
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
        app.Services.GetRequiredService<IOptions<ManagementOptions>>().Value.Validate();
        // The cancel windows belong to the Orchestrator module and are read through its seam; the database group comes
        // first so a missing connection string is still the key a host with no configuration at all reports.
        app.Services.ValidateOrchestratorOptions();

        // The healing provider is a deployment selection: unset outside Development, a mock outside Development or an
        // incomplete HTTP selection must refuse the boot instead of degrading to no provider at the first call.
        app.Services.ValidateHealingOptions();
    }
}
