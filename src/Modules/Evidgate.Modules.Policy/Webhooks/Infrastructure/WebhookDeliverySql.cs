namespace Evidgate.Modules.Policy.Webhooks.Infrastructure;

/// <summary>
/// The statements of the delivery pipeline. Every one runs on a tenant scope, so row-level security fixes the tenant;
/// the explicit <c>org_id</c> only keeps the plan on the tenant's index.
/// </summary>
internal static class WebhookDeliverySql
{
    /// <summary>The active endpoints of a project that subscribe to an event type.</summary>
    public const string SelectSubscribedEndpoints =
        """
        select e.id as "Id"
          from public.webhook_endpoint e
         where e.org_id = @org_id
           and e.project_id = @project_id
           and e.state = 'active'
           and @event_type = any (e.event_types)
         order by e.id
        """;

    /// <summary>One delivery per (endpoint, event); a repeat of the same event inserts nothing and returns no row.</summary>
    public const string InsertDelivery =
        """
        insert into public.webhook_outbound_delivery
            (id, org_id, project_id, endpoint_id, event_type, event_id, next_attempt_at)
        values (@id, @org_id, @project_id, @endpoint_id, @event_type, @event_id, @now)
        on conflict (org_id, endpoint_id, event_id) do nothing
        returning id as "Id"
        """;

    /// <summary>Locks the delivery row so two tasks cannot claim the same attempt.</summary>
    public const string LockDelivery =
        """
        select d.state as "State",
               d.next_attempt_at as "NextAttemptAt",
               d.created_at as "CreatedAt",
               d.endpoint_id as "EndpointId",
               d.event_id as "EventId"
          from public.webhook_outbound_delivery d
         where d.org_id = @org_id and d.id = @id
           for update
        """;

    /// <summary>Takes the attempt: the new count is also the fence the result is written under.</summary>
    public const string ClaimDelivery =
        """
        update public.webhook_outbound_delivery
           set state = 'sending', attempts = attempts + 1
         where org_id = @org_id and id = @id
        returning attempts as "Attempts"
        """;

    /// <summary>The endpoint's address and sealed secret as they are now.</summary>
    public const string SelectEndpointForSend =
        """
        select e.url as "Url", e.state as "State", e.secret_ciphertext as "SecretCiphertext"
          from public.webhook_endpoint e
         where e.org_id = @org_id and e.id = @id
        """;

    /// <summary>The evaluation a delivery announces; the body is rebuilt from it on every attempt.</summary>
    public const string SelectEvaluationForSend =
        """
        select e.id as "EvaluationId",
               e.run_id as "RunId",
               e.evidence_revision as "EvidenceRevision",
               e.policy_revision_id as "PolicyRevisionId",
               e.policy_hash as "PolicyHash",
               e.manifest_hash as "ManifestHash",
               e.exceptions_hash as "ExceptionsHash",
               e.decision as "Decision",
               e.reasons as "Reasons"
          from public.evaluation e
         where e.org_id = @org_id and e.id = @id
        """;

    /// <summary>Writes an attempt's result; a row another claim has since taken is left alone.</summary>
    public const string RecordResult =
        """
        update public.webhook_outbound_delivery
           set state = @state,
               next_attempt_at = coalesce(@next_attempt_at, next_attempt_at),
               last_error = @last_error,
               last_status_code = @status_code
         where org_id = @org_id and id = @id and state = 'sending' and attempts = @attempts
        """;
}
