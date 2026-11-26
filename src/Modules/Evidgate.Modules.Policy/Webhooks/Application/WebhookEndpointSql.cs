namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>
/// The statements of the endpoint and delivery routes. Every one names its columns: the application role holds
/// column-limited INSERT/UPDATE on both tables, and the secret's ciphertext column is never selected here.
/// </summary>
internal static class WebhookEndpointSql
{
    /// <summary>A bounded wait for every lock a write takes, the same 5 s the policy writes allow.</summary>
    internal const string LockTimeout = "select set_config('lock_timeout', '5000', true)";

    /// <summary>
    /// The project row, locked <c>FOR NO KEY UPDATE</c>: concurrent registrations of one project queue on it, so the
    /// live-endpoint count below cannot be raced past the cap. A missing or soft-deleted project reads no row.
    /// </summary>
    internal const string ProjectForUpdate = """
        select 1
          from public.project p
         where p.id = @project_id and p.status <> 'deleted'
           for no key update of p
        """;

    /// <summary>A read-only existence check of the project, for the routes that take no lock.</summary>
    internal const string ProjectExists = "select 1 from public.project where id = @project_id and status <> 'deleted'";

    /// <summary>Live endpoints of one project.</summary>
    internal const string CountLive = """
        select count(*)::int
          from public.webhook_endpoint
         where org_id = @org_id and project_id = @project_id and state <> 'deleted'
        """;

    /// <summary>The columns every endpoint read projects: never the secret ciphertext.</summary>
    private const string Columns = """
        id, project_id, url, event_types, state, secret_hint, secret_version,
        version, created_at, updated_at
        """;

    /// <summary>Inserts one endpoint (only granted columns) and reads it back with its trigger-set fields.</summary>
    internal const string Insert = $"""
        insert into public.webhook_endpoint
            (id, org_id, project_id, url, secret_ciphertext, secret_hint, event_types, created_by)
        values
            (@id, @org_id, @project_id, @url, @secret_ciphertext, @secret_hint, @event_types, @created_by)
        returning {Columns}
        """;

    /// <summary>One live endpoint of a project.</summary>
    internal const string Get = $"""
        select {Columns}
          from public.webhook_endpoint
         where org_id = @org_id and project_id = @project_id and id = @endpoint_id and state <> 'deleted'
        """;

    /// <summary>One live endpoint locked for a change, so the version check and the write are one step.</summary>
    internal const string GetForUpdate = $"""
        select {Columns}
          from public.webhook_endpoint
         where org_id = @org_id and project_id = @project_id and id = @endpoint_id and state <> 'deleted'
           for no key update
        """;

    /// <summary>Applies a change; the trigger bumps <c>version</c> and <c>updated_at</c>.</summary>
    internal const string Update = $"""
        update public.webhook_endpoint
           set url = @url,
               event_types = @event_types,
               state = @state
         where org_id = @org_id and project_id = @project_id and id = @endpoint_id
        returning {Columns}
        """;

    /// <summary>The soft delete: the row stays for the delivery history.</summary>
    internal const string SoftDelete = """
        update public.webhook_endpoint
           set state = 'deleted',
               deleted_at = now()
         where org_id = @org_id and project_id = @project_id and id = @endpoint_id and state <> 'deleted'
        """;

    /// <summary>The first page of a project's live endpoints, in creation order.</summary>
    internal const string FirstPage = $"""
        select {Columns}
          from public.webhook_endpoint
         where org_id = @org_id and project_id = @project_id and state <> 'deleted'
         order by created_at, id
         limit @limit
        """;

    /// <summary>The next page: the same projection and order with the row-comparison cursor (00 §9.1).</summary>
    internal const string NextPage = $"""
        select {Columns}
          from public.webhook_endpoint
         where org_id = @org_id and project_id = @project_id and state <> 'deleted'
           and (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by created_at, id
         limit @limit
        """;

    /// <summary>
    /// A page of a project's deliveries, newest first. The optional state equality rides
    /// <c>webhook_outbound_delivery_project_state_idx</c>, the unfiltered page <c>webhook_outbound_delivery_project_idx</c>;
    /// both end in <c>(created_at, id)</c>, so the order is an index scan and needs no sort.
    /// </summary>
    /// <param name="filtered">Whether a <c>state</c> equality is part of the statement.</param>
    /// <param name="cursor">Whether a continuation cursor bounds the page.</param>
    internal static string DeliveryPage(bool filtered, bool cursor) => $"""
        select id, endpoint_id, event_type, event_id, state, attempts, next_attempt_at,
               last_error, last_status_code, created_at, updated_at
          from public.webhook_outbound_delivery
         where org_id = @org_id and project_id = @project_id
           {(filtered ? "and state = @state" : string.Empty)}
           {(cursor ? "and (created_at, id) < (@cursor_created_at::timestamptz, @cursor_id::uuid)" : string.Empty)}
         order by created_at desc, id desc
         limit @limit
        """;
}
