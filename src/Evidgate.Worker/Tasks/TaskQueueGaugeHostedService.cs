using Evidgate.BuildingBlocks.Persistence.Tasks;

namespace Evidgate.Worker.Tasks;

/// <summary>
/// Runs the refresher behind <see cref="TaskQueueStateGauge"/>. Kept separate from the claim loops so a
/// queue with no consumer still reports its age and its dead-letter depth.
/// </summary>
internal sealed class TaskQueueGaugeHostedService(TaskQueueStateGauge gauge) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => gauge.RunRefreshAsync(stoppingToken);
}
