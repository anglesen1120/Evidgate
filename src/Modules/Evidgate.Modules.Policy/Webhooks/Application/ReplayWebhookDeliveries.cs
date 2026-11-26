using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>Operator replay of every dead-lettered delivery of one event in one org.</summary>
/// <param name="OrgId">The org the operator route names.</param>
/// <param name="EventId">The event (evaluation) whose dead-lettered deliveries are replayed.</param>
/// <param name="Actor">The authenticated operator, the audit row's actor.</param>
internal sealed record ReplayWebhookDeliveriesCommand(Guid OrgId, Guid EventId, Guid Actor);

/// <summary>
/// Handler of <see cref="ReplayWebhookDeliveriesCommand"/>, called by the operator route. Deliberately not a Mediator
/// handler: the operator surface has no tenant principal for the C2 pipeline to publish, so the org arrives as a
/// parameter and this service opens that org's tenant transaction, the one the owner replay function reads the org
/// from. The function moves the rows and writes one audit row each in that transaction, so the handler writes none;
/// the delivery tasks are enqueued in the same transaction, so a replayed row is never left without a task.
/// </summary>
internal sealed class ReplayWebhookDeliveriesHandler(
    ITenantScopeFactory scopes,
    ITaskQueueWriter writer,
    IOptions<TasksOptions> tasks)
{
    private const string ReplaySql = "select delivery_id from evidgate.replay_webhook_delivery(@event_id, @actor)";

    private const string EventExistsSql = """
        select 1
          from public.webhook_outbound_delivery
         where org_id = @org_id and event_id = @event_id
         limit 1
        """;

    /// <summary>Requeues the event's dead-lettered deliveries of active endpoints.</summary>
    /// <param name="command">The org, the event and the operator.</param>
    /// <param name="cancellationToken">Cancels the transaction.</param>
    public async Task<Result<WebhookReplayResult>> ExecuteAsync(
        ReplayWebhookDeliveriesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await scopes.BeginAsync(command.OrgId, null, false, cancellationToken).ConfigureAwait(false);

        List<Guid> replayed;

        try
        {
            replayed = (await scope.Connection.QueryAsync<Guid>(new CommandDefinition(
                ReplaySql,
                new { event_id = command.EventId, actor = command.Actor },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Result.Failure<WebhookReplayResult>(WebhookEndpointErrors.Busy);
        }

        if (replayed.Count == 0)
        {
            return Result.Failure<WebhookReplayResult>(await NothingToReplayAsync(scope, command, cancellationToken).ConfigureAwait(false));
        }

        foreach (var deliveryId in replayed)
        {
            // A replay's task needs a key of its own: the original delivery task's key is already spent.
            await WebhookTasks.EnqueueDeliveryAsync(
                    writer,
                    tasks,
                    scope.Connection,
                    scope.Transaction,
                    command.OrgId,
                    deliveryId,
                    $"replay-{Guid.CreateVersion7():N}",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new WebhookReplayResult(command.EventId, replayed));
    }

    private static async Task<Error> NothingToReplayAsync(
        ITenantScope scope,
        ReplayWebhookDeliveriesCommand command,
        CancellationToken cancellationToken)
    {
        var known = await scope.Connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            EventExistsSql,
            new { org_id = command.OrgId, event_id = command.EventId },
            scope.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return known is null
            ? Error.NotFound("no webhook delivery exists for that event in this organization")
            : Error.Conflict(
                WebhookEndpointErrors.DeliveryNotDeadLetter,
                "the event has no dead-lettered delivery to an active endpoint");
    }
}
