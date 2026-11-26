using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>The task and outbox payload of an <c>evaluation.created</c> event: which evaluation, in which project.</summary>
/// <param name="EvaluationId">The evaluation that was stored; it is also the event id.</param>
/// <param name="ProjectId">The project whose endpoints receive it.</param>
internal sealed record WebhookFanoutPayload(Guid EvaluationId, Guid ProjectId);

/// <summary>The payload of one <c>webhook.deliver</c> task: one delivery row of one tenant.</summary>
/// <param name="OrgId">The tenant that owns the row.</param>
/// <param name="DeliveryId">The <c>webhook_outbound_delivery.id</c>.</param>
internal sealed record WebhookDeliveryPayload(Guid OrgId, Guid DeliveryId);

/// <summary>Task types, task keys and payload encoding of the outbound-webhook pipeline.</summary>
internal static class WebhookTasks
{
    /// <summary>The task type that sends one delivery.</summary>
    public const string Deliver = "webhook.deliver";

    /// <summary>The key of the delivery task a fan-out enqueues, so a repeated fan-out cannot enqueue a second one.</summary>
    public const string InitialKeySuffix = "initial";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The outbox payload of <paramref name="payload"/>, in the task payload's snake_case shape.</summary>
    /// <param name="payload">The event reference.</param>
    public static string Serialize(WebhookFanoutPayload payload) => JsonSerializer.Serialize(payload, Json);

    /// <summary>Enqueues the delivery task of one row inside the caller's open transaction.</summary>
    /// <param name="writer">The task queue writer.</param>
    /// <param name="tasks">The routing options.</param>
    /// <param name="connection">The scope's connection.</param>
    /// <param name="transaction">The scope's transaction.</param>
    /// <param name="orgId">The tenant.</param>
    /// <param name="deliveryId">The delivery to send.</param>
    /// <param name="keySuffix">Distinguishes a replay's task from the original one, whose key is spent.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    public static async Task EnqueueDeliveryAsync(
        ITaskQueueWriter writer,
        IOptions<TasksOptions> tasks,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orgId,
        Guid deliveryId,
        string keySuffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(tasks);

        await writer.EnqueueAsync(
                connection,
                transaction,
                new TaskEnqueue
                {
                    Queue = TaskQueueRouting.QueueFor(tasks.Value, Deliver),
                    TaskType = Deliver,
                    PayloadJson = JsonSerializer.Serialize(new WebhookDeliveryPayload(orgId, deliveryId), Json),
                    IdempotencyKey = $"webhook:deliver:{deliveryId:D}:{keySuffix}",
                    OrgId = orgId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Writes the outbox row that announces a stored live evaluation.</summary>
internal static class EvaluationCreatedPublisher
{
    /// <summary>Adds the <c>evaluation.created</c> row to the caller's transaction, one per evaluation.</summary>
    /// <param name="outbox">The outbox writer.</param>
    /// <param name="evaluationId">The stored evaluation.</param>
    /// <param name="projectId">The project the run belongs to.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    public static Task PublishAsync(
        IOutboxWriter outbox,
        Guid evaluationId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);

        return outbox.EnqueueAsync(
            EvaluationCreatedPayload.EventType,
            WebhookTasks.Serialize(new WebhookFanoutPayload(evaluationId, projectId)),
            "evaluation",
            evaluationId,
            $"policy:evaluation:{evaluationId:D}:created",
            cancellationToken);
    }
}
