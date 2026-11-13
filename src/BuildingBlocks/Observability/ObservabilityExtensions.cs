using System.Reflection;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Evidgate.BuildingBlocks.Observability.Redaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// Host wiring shared by <c>Evidgate.Api</c> and <c>Evidgate.Worker</c>
/// (01.1.2-T1.2, 10.3.1-T1): one log pipeline with the secret redactor in front of
/// every provider and its processors in a fixed order (correlation enrichment, then
/// redaction, then the exporter), one activity source and meter, the resource attributes
/// of 10.3.1-T1.3 on traces, metrics and logs, and an OTLP exporter that exists only
/// when <c>EVIDGATE__TELEMETRY__OTLPENDPOINT</c> is set.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>The resource attribute naming the deployment (10.3.1-T1.3 AC).</summary>
    public const string DeploymentEnvironmentAttribute = "deployment.environment";

    /// <summary>
    /// Adds options binding/validation, redaction and OpenTelemetry to
    /// <paramref name="builder"/> and returns the OpenTelemetry builder so the
    /// host can add its own instrumentation (ASP.NET Core, Prometheus, …).
    /// </summary>
    public static OpenTelemetryBuilder AddEvidgateTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var section = builder.Configuration.GetSection(TelemetryOptions.SectionName);
        var options = section.Get<TelemetryOptions>() ?? new TelemetryOptions();

        builder.Services
            .AddOptions<TelemetryOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSecretRedaction();

        var otlpEndpoint = ResolveOtlpEndpoint(options.OtlpEndpoint);
        var environmentName = builder.Environment.EnvironmentName;
        var serviceVersion = ResolveServiceVersion(options.ServiceVersion);
        var resourceBuilder = CreateTelemetryResource(serviceName, environmentName, serviceVersion);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            // 10.3.1-T1.3: the same resource as traces and metrics, so one filter in the
            // backend finds all three signals of a deployment.
            logging.SetResourceBuilder(resourceBuilder);

            // The state is already redacted by RedactingLogger; the rendered
            // message would duplicate it, so only the structured pairs are exported.
            logging.IncludeFormattedMessage = false;
            logging.IncludeScopes = false;

            // Processor order is registration order, and each processor hands the record to
            // the next one: an exporter registered here buffers the record at its own OnEnd,
            // so anything added after it never reaches the backend. All three stages are
            // therefore registered in this one block, in pipeline order.
            //
            // 1. Correlation enrichment (10.3.1-T1.2): it only ever adds a field, so it runs
            // first and every later stage sees the complete record.
            logging.AddProcessor(new CorrelationLogRecordProcessor());

            // 2. 10.3.1-T1.4: the export-side half of the redaction list. The logger decorator
            // covers everything written through ILoggerFactory; this processor covers the
            // record itself (exception text, and any pair a processor put back), so a value
            // cannot reach the backend because it took a path that skipped the decorator.
            logging.AddProcessor(new LogRedactionProcessor());

            // 3. The export processor, last.
            if (otlpEndpoint is not null)
            {
                logging.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }
        });

        var otel = builder.Services.AddOpenTelemetry();
        otel.ConfigureResource(resource =>
            ConfigureTelemetryResource(resource, serviceName, environmentName, serviceVersion));
        otel.WithTracing(tracing =>
        {
            tracing.SetSampler(TelemetrySampling.CreateSampler(ResolveSampleRatio(options.SampleRatio)));
            tracing.AddSource(EvidgateTelemetry.ActivitySourceName);

            // Must precede the exporter: the SDK export processors skip a span whose
            // recorded flag is off, and this processor is what turns it on for an error
            // span of a trace the ratio dropped (10.3.1-T1.3).
            tracing.AddProcessor(new ErrorRetainingProcessor());

            if (otlpEndpoint is not null)
            {
                tracing.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics.AddMeter(EvidgateTelemetry.MeterName)
                .AddMeter(TelemetryMeters.GitHub);

            if (options.LibraryMetrics)
            {
                // The runtime and pool instruments the operations catalogue reports are published by the
                // libraries themselves; subscribing here is what puts them on the pipeline both hosts export.
                metrics.AddMeter(TelemetryMeters.Runtime)
                    .AddMeter(TelemetryMeters.Npgsql);

                // GC, JIT, thread-pool and exception counters come from the runtime instrumentation; the
                // application records none of them itself.
                metrics.AddRuntimeInstrumentation();
            }

            if (otlpEndpoint is not null)
            {
                metrics.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }
        });

        return otel;
    }

    /// <summary>
    /// The resource shared by the three signals (10.3.1-T1.3): <c>service.name</c>,
    /// <c>deployment.environment</c> and <c>service.version</c>. Logs take it through
    /// <see cref="OpenTelemetryLoggerOptions.SetResourceBuilder"/>, traces and metrics
    /// through <see cref="OpenTelemetryBuilderSdkExtensions.ConfigureResource"/>, and all
    /// three call sites apply <see cref="ConfigureTelemetryResource"/> — the one definition
    /// of the attribute set — so a backend can join a log line to its trace and the three
    /// signals cannot drift apart.
    /// </summary>
    /// <param name="serviceName">The host's service identity, e.g. <c>evidgate-api</c>.</param>
    /// <param name="environmentName">The host environment name, e.g. <c>Production</c>.</param>
    /// <param name="serviceVersion">A concrete version — the resolved informational version or the configured override.</param>
    public static ResourceBuilder CreateTelemetryResource(
        string serviceName,
        string environmentName,
        string serviceVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);

        return ConfigureTelemetryResource(
            ResourceBuilder.CreateDefault(),
            serviceName,
            environmentName,
            serviceVersion);
    }

    /// <summary>
    /// Applies the resource attribute set of 10.3.1-T1.3 to <paramref name="resource"/>: the
    /// one place the three attributes are defined, used by <see cref="CreateTelemetryResource"/>
    /// (the logging path) and by the traces/metrics <c>ConfigureResource</c> callback.
    /// </summary>
    private static ResourceBuilder ConfigureTelemetryResource(
        ResourceBuilder resource,
        string serviceName,
        string environmentName,
        string serviceVersion) =>
        resource
            .AddService(serviceName, serviceVersion: serviceVersion)
            .AddAttributes([new KeyValuePair<string, object>(DeploymentEnvironmentAttribute, environmentName)]);

    /// <summary>
    /// Puts <see cref="SecretRedactor"/> in front of every logger (AC4): the
    /// factory resolves the registered providers itself, so console, OpenTelemetry
    /// and test providers all format redacted state.
    /// </summary>
    public static IServiceCollection AddSecretRedaction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<SecretRedactor>();
        services.AddSingleton<ILoggerFactory>(provider => new RedactingLoggerFactory(
            provider.GetServices<ILoggerProvider>(),
            provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
            provider.GetRequiredService<SecretRedactor>()));

        return services;
    }

    /// <summary>
    /// Resolves the configured head-sampling ratio. An unusable value is <em>not</em> thrown here
    /// (that would be the D4 trap again — a composition-time exception instead of the clean
    /// <c>Configuration invalid</c> path): it falls back to
    /// <see cref="TelemetryOptions.DefaultSampleRatio"/>, while
    /// <see cref="TelemetryOptions.Validate"/> still fails host start and names the key.
    /// </summary>
    private static double ResolveSampleRatio(double configured) =>
        double.IsNaN(configured) || double.IsInfinity(configured) || configured < 0d || configured > 1d
            ? TelemetryOptions.DefaultSampleRatio
            : configured;

    /// <summary>
    /// Resolves the configured OTLP endpoint. An invalid value is <em>not</em>
    /// thrown here (that happened during composition and escaped as an unhandled
    /// exception, D4): it yields <c>null</c> — no exporter is registered — and
    /// <see cref="TelemetryOptions"/> validation fails host start with the key in
    /// the message, which both hosts turn into the clean <c>Configuration invalid</c>
    /// path.
    /// </summary>
    private static Uri? ResolveOtlpEndpoint(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)
            || !Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri;
    }

    /// <summary>
    /// The <c>service.version</c> value (10.3.1-T1.3): the configured override, else the entry
    /// assembly's informational version, else the assembly version, so the attribute is always
    /// present and never blank.
    /// </summary>
    /// <remarks>
    /// The fallback is only as specific as the build makes it: this repository sets no
    /// <c>Version</c>/<c>SourceRevisionId</c>, so a local build reports the SDK default and two
    /// commits are indistinguishable. Staging and production set
    /// <c>EVIDGATE__TELEMETRY__SERVICEVERSION</c> to the image tag or digest; that is the value the
    /// plan's "which build produced this error" filter needs.
    /// </remarks>
    private static string ResolveServiceVersion(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var assembly = Assembly.GetEntryAssembly() ?? typeof(ObservabilityExtensions).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informational;
    }
}
