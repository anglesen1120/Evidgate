using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence;
using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.Modules.Catalog;
using Evidgate.Modules.Ingestion;
using Evidgate.Modules.Orchestrator;
using Evidgate.Worker.Health;
using Evidgate.Worker.Options;
using Evidgate.Worker.Slice;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Evidgate.Worker;

/// <summary>
/// Composition root of the background entry point (00 §5.1 A1). Split from
/// <c>Program.cs</c> so <c>tests/Hosts.IntegrationTests</c> builds the identical
/// host (AC5) instead of a re-implemented one.
/// </summary>
public static class WorkerHost
{
    /// <summary>10 s shutdown timeout per 01.1.2-T1.4 (AC1).</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Exit code when the process could not start its readiness surface (D5).</summary>
    public const int ReadinessStartExitCode = 2;

    /// <summary>Exit code for a missing or invalid <c>EVIDGATE__*</c> value (NFR-01-2).</summary>
    public const int ConfigurationExitCode = 1;

    /// <summary>Builds the host builder: options, persistence, telemetry, Mediator, modules, hosted services.</summary>
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);

        builder.Services.AddEvidgatePersistence(builder.Configuration);
        builder.AddEvidgateTelemetry("evidgate-worker");

        builder.Services
            .AddOptions<WorkerOptions>()
            .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddMediator();

        builder.Services.AddIngestionModule();
        builder.Services.AddCatalogModule();
        builder.Services.AddOrchestratorModule();

        builder.Services.AddHostedService<HealthProbeListener>();
        builder.Services.AddHostedService<SliceRunConsumer>();

        return builder;
    }

    /// <summary>Builds the host.</summary>
    public static IHost BuildHost(string[] args) => CreateBuilder(args).Build();

    /// <summary>Runs the Worker; returns the process exit code (non-zero on boot validation failure, AC2).</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            using var host = BuildHost(args);
            ValidateOptions(host);
            await host.RunAsync();
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
            // D4: an *invalid* value (e.g. EVIDGATE__WORKER__HEALTHPORT=abc) fails the
            // configuration binder while the options are materialised, which surfaces
            // as an InvalidOperationException naming the key. Same clean path as a
            // missing value: one line, no stack trace, non-zero exit.
            await Console.Error.WriteLineAsync($"Configuration invalid: {exception.Message}");
            return ConfigurationExitCode;
        }
        catch (Exception exception) when (FindBindFailure(exception) is not null)
        {
            // D5: a process whose readiness surface never came up must not exit 0.
            // The host wraps a failing IHostedService.StartAsync in an
            // AggregateException, so look through it for the bind failure.
            await Console.Error.WriteLineAsync($"Worker failed to start: {FindBindFailure(exception)!.Message}");
            return ReadinessStartExitCode;
        }
    }

    /// <summary>The readiness bind failure inside <paramref name="exception"/>, if any.</summary>
    private static HealthListenerBindException? FindBindFailure(Exception exception) => exception switch
    {
        HealthListenerBindException bind => bind,
        AggregateException aggregate => aggregate.InnerExceptions
            .Select(FindBindFailure)
            .FirstOrDefault(failure => failure is not null),
        _ => null,
    };

    /// <summary>
    /// Materialises every validated option group before the host starts. The same
    /// validation runs on start anyway (<c>ValidateOnStart</c>), but by then the
    /// failure escapes <see cref="IHost.RunAsync"/> and the host logs it as a
    /// critical "Hosting failed to start" with a full stack trace — 00 §11 NFR-01-2
    /// wants one named line and no stack trace instead.
    /// </summary>
    private static void ValidateOptions(IHost host)
    {
        _ = host.Services.GetRequiredService<IOptions<WorkerOptions>>().Value;
        _ = host.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        _ = host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
    }
}
