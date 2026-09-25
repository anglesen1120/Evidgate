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
/// (01.1.2-T1.2): one log pipeline with the secret redactor in front of every
/// provider, one activity source and meter, and an OTLP exporter that exists only
/// when <c>EVIDGATE__TELEMETRY__OTLPENDPOINT</c> is set.
/// </summary>
public static class ObservabilityExtensions
{
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

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));

            // The state is already redacted by RedactingLogger; the rendered
            // message would duplicate it, so only the structured pairs are exported.
            logging.IncludeFormattedMessage = false;
            logging.IncludeScopes = false;

            if (otlpEndpoint is not null)
            {
                logging.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }
        });

        var otel = builder.Services.AddOpenTelemetry();
        otel.ConfigureResource(resource => resource.AddService(serviceName));
        otel.WithTracing(tracing =>
        {
            tracing.AddSource(EvidgateTelemetry.ActivitySourceName);

            if (otlpEndpoint is not null)
            {
                tracing.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics.AddMeter(EvidgateTelemetry.MeterName);

            if (otlpEndpoint is not null)
            {
                metrics.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }
        });

        return otel;
    }

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
    /// Resolves the configured OTLP endpoint. An invalid value is <em>not</em>
    /// thrown here (that happened during composition and escaped as an unhandled
    /// exception, D4): it yields <c>null</c> — no exporter is registered — and
    /// <see cref="TelemetryOptions"/> validation fails host start with the key in
    /// the message, which both hosts turn into the clean <c>Configuration invalid</c>
    /// path.
    /// </summary>
    private static Uri? ResolveOtlpEndpoint(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return Uri.TryCreate(configured, UriKind.Absolute, out var endpoint)
            && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
            ? endpoint
            : null;
    }
}
