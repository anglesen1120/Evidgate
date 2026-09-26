using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Ingestion.Infrastructure;
using Mediator;

namespace Evidgate.Modules.Ingestion.Application.Commands;

/// <summary>
/// Skeleton command of the sprint-1 slice: accept an event batch for a run.
/// Replaced by the batch ingest of 04 (permission <c>event:write</c>, as
/// <c>POST /v1/projects/{p}/runs/{r}/events</c> in contracts/openapi.yaml).
/// </summary>
internal sealed record AcceptEventBatch(string RunId, int EventCount) : ICommand<BatchResult>;

/// <summary>How many events the batch contained (<c>duplicate_count</c> stays 0 until 04 dedupes).</summary>
internal sealed record BatchResult(string RunId, int AcceptedCount, int DuplicateCount);

/// <summary>Handler of <see cref="AcceptEventBatch"/>: writes to the in-memory slice sink.</summary>
[Permission("event:write")]
internal sealed class AcceptEventBatchHandler(InMemoryEventStore events)
    : ICommandHandler<AcceptEventBatch, BatchResult>
{
    /// <inheritdoc />
    public ValueTask<BatchResult> Handle(AcceptEventBatch command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(AcceptEventBatch));

        events.Accept(command.RunId, command.EventCount);

        return ValueTask.FromResult(new BatchResult(command.RunId, command.EventCount, 0));
    }
}
