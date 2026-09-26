using Evidgate.BuildingBlocks.Persistence.Readiness;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Orchestrator.Application.Commands;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Evidgate.Modules.Policy.Application.Queries;
using Evidgate.Worker.Options;
using Mediator;
using Microsoft.Extensions.Options;

namespace Evidgate.Worker.Slice;

/// <summary>
/// Worker-side consumer stub of the sprint-1 slice (01.1.2-T3.3): it waits for
/// the same readiness probe the Api's <c>/internal/health/ready</c> serves, then
/// runs the identical Mediator command chain the Api's
/// <c>POST /internal/slice/run</c> runs — same types, same module handlers —
/// and logs the resulting <c>run_id</c> (AC5).
/// </summary>
internal sealed partial class SliceRunConsumer : BackgroundService
{
    private readonly IMediator _mediator;
    private readonly ReadinessProbe _probe;
    private readonly WorkerOptions _options;
    private readonly ILogger<SliceRunConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public SliceRunConsumer(
        IMediator mediator,
        ReadinessProbe probe,
        IOptions<WorkerOptions> options,
        ILogger<SliceRunConsumer> logger)
    {
        _mediator = mediator;
        _probe = probe;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The run_id of the last slice run (the integration test asserts on the log line too).</summary>
    public string? LastRunId { get; private set; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var ready = await WaitForReadyAsync(stoppingToken);
            if (!ready)
            {
                return;
            }

            var manifest = await _mediator.Send(
                new IngestManifest("worker/skeleton", "slice://worker", "{}"),
                stoppingToken);

            var run = await _mediator.Send(
                new CreateRun("worker/skeleton", manifest.ManifestId),
                stoppingToken);

            var plan = await _mediator.Send(new PlanExecutions(run.RunId), stoppingToken);
            var batch = await _mediator.Send(new AcceptEventBatch(run.RunId, 2), stoppingToken);
            var completeness = await _mediator.Send(new GetCompleteness(run.RunId), stoppingToken);
            var decision = await _mediator.Send(new GetDecision(run.RunId), stoppingToken);

            LastRunId = run.RunId;
            LogSliceCompleted(
                run.RunId,
                manifest.ManifestId,
                plan.ExecutionIds.Count,
                batch.AcceptedCount,
                completeness.Completeness,
                decision.Decision);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown (T1.4): a cancelled poll/send is not a failure —
            // letting it escape makes the host report the service as crashed.
        }
    }

    private async Task<bool> WaitForReadyAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var last = await _probe.CheckAsync(stoppingToken);
            if (last.Ready)
            {
                return true;
            }

            await Task.Delay(_options.ReadinessPollMilliseconds, stoppingToken);
        }

        return false;
    }

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Slice chain complete: run_id {RunId} manifest_id {ManifestId} executions {Executions} accepted {Accepted} completeness {Completeness} decision {Decision}")]
    private partial void LogSliceCompleted(
        string runId,
        string manifestId,
        int executions,
        int accepted,
        string completeness,
        string decision);
}
