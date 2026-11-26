using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Webhooks.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>
/// The worker's handler of the <c>evaluation.created</c> outbox task: one delivery row and one <c>webhook.deliver</c>
/// task per active endpoint of the project that subscribes. The row insert is arbitrated by the table's
/// (org, endpoint, event) key and the task is enqueued only for a row this call inserted, so a redelivered message
/// creates neither a second row nor a second task, and an endpoint that is disabled or deleted is never selected.
/// </summary>
internal sealed partial class WebhookFanoutHandler(
    ITenantDb db,
    ITaskQueueWriter writer,
    IOptions<TasksOptions> tasks,
    TimeProvider time,
    ILogger<WebhookFanoutHandler> logger) : ITaskHandler<WebhookFanoutPayload>
{
    private sealed record EndpointRow(Guid Id);

    private sealed record InsertedRow(Guid Id);

    /// <inheritdoc />
    public string TaskType => EvaluationCreatedPayload.EventType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        WebhookFanoutPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (scope is null)
        {
            throw new InvalidOperationException("the evaluation.created task has no tenant scope, so no delivery is created");
        }

        var endpoints = await db
            .QueryAsync<EndpointRow>(
                WebhookDeliverySql.SelectSubscribedEndpoints,
                new { org_id = scope.OrgId, project_id = payload.ProjectId, event_type = TaskType },
                cancellationToken)
            .ConfigureAwait(false);

        var created = 0;
        foreach (var endpoint in endpoints)
        {
            var inserted = await db
                .QuerySingleOrDefaultAsync<InsertedRow>(
                    WebhookDeliverySql.InsertDelivery,
                    new
                    {
                        id = Guid.CreateVersion7(),
                        org_id = scope.OrgId,
                        project_id = payload.ProjectId,
                        endpoint_id = endpoint.Id,
                        event_type = TaskType,
                        event_id = payload.EvaluationId,
                        now = time.GetUtcNow(),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (inserted is null)
            {
                continue;
            }

            await WebhookTasks
                .EnqueueDeliveryAsync(
                    writer,
                    tasks,
                    scope.Connection,
                    scope.Transaction,
                    scope.OrgId,
                    inserted.Id,
                    WebhookTasks.InitialKeySuffix,
                    cancellationToken)
                .ConfigureAwait(false);
            created++;
        }

        LogFannedOut(logger, payload.EvaluationId, created);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation {EvaluationId} fanned out to {Created} webhook deliveries")]
    private static partial void LogFannedOut(ILogger logger, Guid evaluationId, int created);
}
