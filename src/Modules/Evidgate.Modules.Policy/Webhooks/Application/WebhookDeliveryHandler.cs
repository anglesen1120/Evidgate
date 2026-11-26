using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Webhooks.Domain;
using Evidgate.Modules.Policy.Webhooks.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>
/// Sends one delivery. Three steps, each in its own place: a short transaction claims the attempt, the network call
/// runs with no transaction open, and a second short transaction records the result. The URL guard runs again for every
/// attempt, because a host that was public at registration can resolve to a private address later; a failed attempt
/// schedules itself by deferring the task (which burns no task attempt) until the stored <c>next_attempt_at</c>.
/// </summary>
internal sealed partial class WebhookDeliveryHandler(
    ITenantScopeFactory scopes,
    ITenantDb db,
    WebhookUrlGuard guard,
    WebhookSecretProtector protector,
    WebhookHttpSender sender,
    TimeProvider time,
    ILogger<WebhookDeliveryHandler> logger) : ITransactionlessTaskHandler<WebhookDeliveryPayload>
{
    private sealed record LockRow(
        string State,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset CreatedAt,
        Guid EndpointId,
        Guid EventId);

    private sealed record EndpointRow(string Url, string State, byte[] SecretCiphertext);

    private sealed record EvaluationRow(
        Guid EvaluationId,
        Guid RunId,
        int EvidenceRevision,
        Guid PolicyRevisionId,
        string PolicyHash,
        string ManifestHash,
        string ExceptionsHash,
        string Decision,
        string[] Reasons);

    /// <summary>What a claim found: nothing to do, not yet due, or an attempt to make.</summary>
    private sealed record Claim(
        int Attempt,
        DateTimeOffset CreatedAt,
        Guid EventId,
        EndpointRow? Endpoint,
        EvaluationCreatedPayload? Body,
        DateTimeOffset? NotBefore);

    /// <inheritdoc />
    public string TaskType => WebhookTasks.Deliver;

    /// <inheritdoc />
    public async ValueTask HandleAsync(WebhookDeliveryPayload payload, Guid orgId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.OrgId != orgId || payload.DeliveryId == Guid.Empty)
        {
            throw new InvalidOperationException("the webhook delivery task names no delivery or another org");
        }

        var now = time.GetUtcNow();
        var claim = await ClaimAsync(orgId, payload.DeliveryId, now, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            LogNothingToSend(logger, payload.DeliveryId);
            return;
        }

        if (claim.NotBefore is { } notBefore)
        {
            throw new TaskDeferredException(notBefore - now);
        }

        if (claim.Endpoint is not { State: WebhookStates.EndpointActive } endpoint || claim.Body is null)
        {
            var stopped = new WebhookAttempt(WebhookErrors.EndpointDisabled, null);
            await RecordAsync(orgId, payload.DeliveryId, claim, WebhookStates.DeliveryDeadLetter, stopped, null, cancellationToken)
                .ConfigureAwait(false);
            LogOutcome(logger, payload.DeliveryId, claim.Attempt, WebhookStates.DeliveryDeadLetter, stopped.Error);
            return;
        }

        var attempt = await AttemptAsync(claim, endpoint, claim.Body, cancellationToken).ConfigureAwait(false);
        await FinishAsync(orgId, payload.DeliveryId, claim, attempt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Guards, signs and posts; every refusal is a result code, never an exception that carries a secret.</summary>
    private async Task<WebhookAttempt> AttemptAsync(
        Claim claim,
        EndpointRow endpoint,
        EvaluationCreatedPayload body,
        CancellationToken cancellationToken)
    {
        var check = await guard.CheckAsync(endpoint.Url, cancellationToken).ConfigureAwait(false);
        if (check.Target is not { } target)
        {
            return new WebhookAttempt(WebhookErrors.DnsRebindingBlocked, null);
        }

        string secret;
        try
        {
            secret = protector.Unprotect(endpoint.SecretCiphertext);
        }
        catch (CryptographicException)
        {
            return new WebhookAttempt(WebhookDeliveryErrors.SecretUnreadable, null);
        }

        var bytes = body.ToUtf8Bytes();
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(secret, timestamp, bytes);

        return await sender.SendAsync(target, claim.EventId, timestamp, signature, bytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Stores the result, then asks the queue to run the task again when a retry is due.</summary>
    private async Task FinishAsync(
        Guid orgId,
        Guid deliveryId,
        Claim claim,
        WebhookAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (attempt.Delivered)
        {
            await RecordAsync(orgId, deliveryId, claim, WebhookStates.DeliveryDelivered, attempt, null, cancellationToken)
                .ConfigureAwait(false);
            LogOutcome(logger, deliveryId, claim.Attempt, WebhookStates.DeliveryDelivered, null);
            return;
        }

        var failedAt = time.GetUtcNow();
        var next = WebhookRetryPolicy.Next(claim.CreatedAt, failedAt, claim.Attempt);
        var state = next is null ? WebhookStates.DeliveryDeadLetter : WebhookStates.DeliveryRetrying;
        var recorded = await RecordAsync(orgId, deliveryId, claim, state, attempt, next, cancellationToken)
            .ConfigureAwait(false);
        LogOutcome(logger, deliveryId, claim.Attempt, state, attempt.Error);

        // A claim that lost the row to a newer attempt leaves the schedule to that attempt.
        if (recorded && next is { } at)
        {
            throw new TaskDeferredException(at - failedAt);
        }
    }

    private async Task<Claim?> ClaimAsync(Guid orgId, Guid deliveryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = await scopes.BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        var key = new { org_id = orgId, id = deliveryId };

        var row = await db.QuerySingleOrDefaultAsync<LockRow>(WebhookDeliverySql.LockDelivery, key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null
            || row.State is WebhookStates.DeliveryDelivered or WebhookStates.DeliveryDeadLetter)
        {
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (row.State == WebhookStates.DeliveryRetrying && row.NextAttemptAt > now)
        {
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new Claim(0, row.CreatedAt, row.EventId, null, null, row.NextAttemptAt);
        }

        var attempts = await db.ScalarAsync<int>(WebhookDeliverySql.ClaimDelivery, key, cancellationToken)
            .ConfigureAwait(false);
        var endpoint = await db
            .QuerySingleOrDefaultAsync<EndpointRow>(
                WebhookDeliverySql.SelectEndpointForSend,
                new { org_id = orgId, id = row.EndpointId },
                cancellationToken)
            .ConfigureAwait(false);
        var body = endpoint?.State == WebhookStates.EndpointActive
            ? await LoadBodyAsync(orgId, row.EventId, cancellationToken).ConfigureAwait(false)
            : null;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Claim(attempts, row.CreatedAt, row.EventId, endpoint, body, null);
    }

    private async Task<EvaluationCreatedPayload> LoadBodyAsync(Guid orgId, Guid evaluationId, CancellationToken cancellationToken)
    {
        var e = await db
            .QuerySingleOrDefaultAsync<EvaluationRow>(
                WebhookDeliverySql.SelectEvaluationForSend,
                new { org_id = orgId, id = evaluationId },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"evaluation {evaluationId:D} behind a webhook delivery is missing");

        return new EvaluationCreatedPayload(
            e.EvaluationId,
            e.RunId,
            e.EvidenceRevision,
            e.PolicyRevisionId,
            e.PolicyHash,
            e.ManifestHash,
            e.ExceptionsHash,
            e.Decision,
            e.Reasons);
    }

    /// <summary>Writes the result under the attempt count the claim took; false means a newer claim owns the row.</summary>
    private async Task<bool> RecordAsync(
        Guid orgId,
        Guid deliveryId,
        Claim claim,
        string state,
        WebhookAttempt attempt,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes.BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        var rows = await db
            .ExecuteAsync(
                WebhookDeliverySql.RecordResult,
                new
                {
                    org_id = orgId,
                    id = deliveryId,
                    attempts = claim.Attempt,
                    state,
                    next_attempt_at = nextAttemptAt,
                    last_error = attempt.Error,
                    status_code = attempt.StatusCode,
                },
                cancellationToken)
            .ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return rows == 1;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} has nothing to send: finished or gone")]
    private static partial void LogNothingToSend(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} attempt {Attempt} ended {State} ({Error})")]
    private static partial void LogOutcome(ILogger logger, Guid deliveryId, int attempt, string state, string? error);
}
