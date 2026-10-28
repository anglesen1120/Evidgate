using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Evidgate.BuildingBlocks.Persistence;
using Evidgate.BuildingBlocks.Persistence.Isolation;
using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.Persistence.Secrets;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Pipeline;
using Evidgate.Modules.Catalog;
using Evidgate.Modules.Evidence;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Identity;
using Evidgate.Modules.Ingestion;
using Evidgate.Modules.Orchestrator;
using Evidgate.Modules.Policy;
using Evidgate.Modules.Tenancy;
using Evidgate.Modules.Usage;
using Evidgate.Worker.Health;
using Evidgate.Worker.Options;
using Evidgate.Worker.Slice;
using Evidgate.Worker.Tasks;
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

    /// <summary>
    /// Exit code when the boot-time isolation self-check refuses the start
    /// (02.6.1-T1.4 AC5). The Worker claims tenant tasks and opens tenant scopes, so
    /// it is held to the same gate as the API.
    /// </summary>
    public const int IsolationSelfCheckExitCode = 3;

    /// <summary>Builds the host builder: options, persistence, telemetry, Mediator, modules, hosted services.</summary>
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);

        builder.Services.AddEvidgatePersistence(builder.Configuration);
        // 10.1.1-T2: the single secret seam. Idempotent (TryAdd*), so the
        // composition extensions that also register it change nothing here.
        builder.Services.AddEvidgateSecrets(builder.Configuration);
        builder.AddEvidgateTelemetry("evidgate-worker").AddEvidgateCorrelation();

        builder.Services
            .AddOptions<WorkerOptions>()
            .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddMediator();

        // 01.4.2-T4: the same C2 pipeline as the Api — a Mediator dispatch inside
        // the worker must still pass telemetry/validation/tenant/authorization/
        // idempotency, not run bare (QA-P defect D1; plan §11 names the pipeline
        // the permanent dispatch path). DTO messages bypass the tenant stages.
        builder.Services.AddEvidgatePipeline();

        builder.Services.AddIngestionModule();
        builder.Services.AddCatalogModule();
        builder.Services.AddOrchestratorModule();
        // 06.1.1-T1: the evidence module, so every handler the Api composes resolves here too.
        builder.Services.AddEvidenceModule(builder.Configuration);
        // 06.3.6-T1.3/T1.4: the out-of-process parse runner and its `artifact_parse` handler, behind
        // EVIDGATE__PARSE__OUTOFPROCESS. Registered here and nowhere else: it is a process-spawn capability, and the
        // Api — which shares every other module piece — must never be able to resolve IParseRunner or claim the task
        // (06.3.6 review, security finding).
        builder.Services.AddParseRunner(builder.Configuration);
        // 07.1.1-T1: the policy module, so every handler the Api composes resolves here too.
        builder.Services.AddPolicyModule();
        // 02.2.1-T1.2: both entry points compose the same module set (00 §5.1 A1).
        builder.Services.AddIdentityModule(builder.Configuration);
        // 02.1.1-T2: the registry module, so every handler the Api composes resolves here too.
        builder.Services.AddTenancyModule();
        // 12.3.1-T1.3: the usage module, so the ledger write path resolves in the Worker as well.
        builder.Services.AddUsageModule();

        // 01.5.1-T1.2/T1.4: the durable task queue — claim loops per queue and the
        // cron schedule runner. Both are registered in every worker; the unique fire
        // key, never a leader, decides who actually fires (00 §5.3).
        builder.Services.AddEvidgateTasks(builder.Configuration);

        builder.Services.AddHostedService<HealthProbeListener>();
        builder.Services.AddHostedService<SliceRunConsumer>();
        builder.Services.AddHostedService<TaskQueueHostedService>();
        builder.Services.AddHostedService<TaskScheduleHostedService>();
        // 01.5.1-T3: the outbox dispatcher — same registration posture as the
        // other loop services (one line, every Worker, no leader: SKIP LOCKED
        // and the task_idem_uq dedupe key make concurrent dispatchers harmless).
        builder.Services.AddHostedService<OutboxDispatcherHostedService>();

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
        catch (Exception exception) when (IsolationSelfCheckException.FindIn(exception) is { } refusal)
        {
            // 02.6.1-T1.4 (AC5): the same gate as the API. A worker that serves
            // tenant tasks must not run on a database that stopped enforcing tenant
            // isolation. One named line, no stack trace, non-zero exit.
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
        _ = host.Services.GetRequiredService<IOptions<IsolationSelfCheckOptions>>().Value;
        _ = host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
        _ = host.Services.GetRequiredService<IOptions<TasksOptions>>().Value;
    }
}
