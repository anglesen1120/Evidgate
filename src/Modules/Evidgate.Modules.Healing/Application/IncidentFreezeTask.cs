using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The Healing module's consumer of the frozen-evidence event: it enqueues exactly one <c>record_incidents</c> task per
/// (org, run, revision) and does no recording itself, so the freeze commit never waits on incident work.
/// <para>
/// <b>Dedupe on the natural key and the payload.</b> The tenant's idempotency record for (run, revision) stores the
/// payload hash: a redelivery of the same event replays and enqueues nothing, and the same key arriving with another
/// payload (for instance another revision id) is refused instead of being silently dropped. The queue's own unique
/// task key stays the last line once the record has expired.
/// </para>
/// </summary>
internal sealed partial class IncidentFreezeTaskHandler(
    IServiceScopeFactory serviceScopes,
    ILogger<IncidentFreezeTaskHandler> logger) : ITaskHandler<EvidenceRevisionFrozenPayload>
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc />
    public string TaskType => IncidentTasks.EvidenceRevisionFrozen;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        EvidenceRevisionFrozenPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null || payload.OrgId != scope.OrgId)
        {
            throw new InvalidOperationException(
                "the incident freeze task has no tenant scope or names another org, so nothing is enqueued");
        }

        if (payload.RunId == Guid.Empty || payload.EvidenceRevisionNo < 1)
        {
            throw new InvalidOperationException("the incident freeze task names no run or no revision number");
        }

        await using var services = serviceScopes.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var idempotency = provider.GetRequiredService<IIdempotencyStore>();

        var begin = await idempotency
            .TryBeginAsync(
                IncidentTasks.EnqueueOperation,
                IncidentTasks.RequestKey(payload.RunId, payload.EvidenceRevisionNo),
                JsonSerializer.Serialize(payload, WireOptions),
                cancellationToken)
            .ConfigureAwait(false);

        if (begin.Outcome == IdempotencyOutcome.Replay)
        {
            LogAlreadyEnqueued(logger, payload.RunId, payload.EvidenceRevisionNo);
            return;
        }

        if (begin.Outcome != IdempotencyOutcome.Proceed || begin.RecordId is not { } recordId)
        {
            throw new InvalidOperationException(
                $"run {payload.RunId:D} revision {payload.EvidenceRevisionNo} was already announced with a different "
                + "payload, so no second incident task is enqueued");
        }

        var tasks = provider.GetRequiredService<ITaskQueueWriter>();
        var options = provider.GetRequiredService<IOptions<TasksOptions>>().Value;
        await tasks
            .EnqueueAsync(
                scope.Connection,
                scope.Transaction,
                new TaskEnqueue
                {
                    Queue = TaskQueueRouting.QueueFor(options, IncidentTasks.RecordIncidents),
                    TaskType = IncidentTasks.RecordIncidents,
                    PayloadJson = IncidentTasks.ToJson(
                        new RecordIncidentsPayload(payload.OrgId, payload.RunId, payload.EvidenceRevisionNo)),
                    IdempotencyKey = IncidentTasks.NaturalKey(payload.OrgId, payload.RunId, payload.EvidenceRevisionNo),
                    OrgId = payload.OrgId,
                },
                cancellationToken)
            .ConfigureAwait(false);

        await idempotency.CompleteAsync(recordId, 202, "{}", cancellationToken).ConfigureAwait(false);
        LogEnqueued(logger, payload.RunId, payload.EvidenceRevisionNo);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "run {RunId} revision {RevisionNo} enqueued for incident recording")]
    private static partial void LogEnqueued(ILogger logger, Guid runId, int revisionNo);

    [LoggerMessage(Level = LogLevel.Information, Message = "run {RunId} revision {RevisionNo} was already enqueued for incident recording")]
    private static partial void LogAlreadyEnqueued(ILogger logger, Guid runId, int revisionNo);
}
