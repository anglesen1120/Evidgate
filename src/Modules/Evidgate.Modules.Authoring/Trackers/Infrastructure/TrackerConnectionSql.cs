namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>
/// The statements of the tracker connection store. Every one names <c>org_id</c>, so a sibling tenant's row is never
/// read even before row level security answers, and the status reads list only the non-credential columns.
/// </summary>
internal static class TrackerConnectionSql
{
    /// <summary>The columns of a connection as the API may show it: never a token column.</summary>
    private const string ViewColumns = "id, project_id, provider, state, granted_scopes, create_issue_enabled, created_at";

    /// <summary>Serialises one user's consent inserts so the cap below cannot be passed by concurrent starts.</summary>
    internal const string ConsentLock = "select pg_advisory_xact_lock(7413, hashtext(@lock_key))";

    /// <summary>
    /// Stores one pending consent unless the user already holds @max_open unspent, unexpired ones; the digest is the
    /// lookup key, the verifier is already ciphertext. Returns the id, or nothing when capped.
    /// </summary>
    internal const string InsertConsent = """
        insert into public.tracker_oauth_state
            (id, org_id, project_id, user_id, provider, state_hash, pkce_verifier_cipher, redirect_uri, expires_at, created_at)
        select @id::uuid, @org_id::uuid, @project_id::uuid, @user_id::uuid, @provider::text, @state_hash::bytea,
               @pkce_verifier_cipher::text, @redirect_uri::text, @expires_at::timestamptz, @now::timestamptz
         where (select count(*)
                  from public.tracker_oauth_state
                 where org_id = @org_id
                   and user_id = @user_id
                   and consumed_at is null
                   and expires_at > @now) < @max_open
        returning id
        """;

    /// <summary>
    /// Consumes a consent in one statement: the row is taken only when it belongs to the caller, is unspent and has
    /// not expired, so two concurrent callbacks cannot both succeed.
    /// </summary>
    internal const string TakeConsent = """
        update public.tracker_oauth_state
           set consumed_at = @now
         where org_id = @org_id
           and state_hash = @state_hash
           and user_id = @user_id
           and consumed_at is null
           and expires_at > @now
        returning id, project_id, provider, redirect_uri, pkce_verifier_cipher
        """;

    /// <summary>The state a failed take found, to name the refusal; read only after the take returned no row.</summary>
    internal const string ConsentState = """
        select user_id, consumed_at is not null as consumed
          from public.tracker_oauth_state
         where org_id = @org_id
           and state_hash = @state_hash
        """;

    /// <summary>
    /// Completes the consent and stores its connection in one statement, so a consent cancelled by a disconnect
    /// yields no row. A conflict updates only a connection that needs reauthorisation, and replaces its refresh
    /// token (a reauth that grants none leaves none); an active connection is never overwritten.
    /// </summary>
    internal const string Upsert = $"""
        with claim as (
            update public.tracker_oauth_state
               set completed_at = @now
             where org_id = @org_id
               and id = @consent_id
               and user_id = @user_id
               and consumed_at is not null
               and completed_at is null
               and cancelled_at is null
            returning id)
        insert into public.tracker_connection as c
            (id, org_id, project_id, provider, created_by_membership_id, access_token_cipher, refresh_token_cipher,
             token_expires_at, granted_scopes)
        select @id::uuid, @org_id::uuid, @project_id::uuid, @provider::text, @membership_id::uuid,
               @access_token_cipher::text, @refresh_token_cipher::text, @token_expires_at::timestamptz,
               @granted_scopes::text
          from claim
        on conflict (org_id, project_id, provider) where state <> 'disconnected'
        do update set
            state = 'active',
            access_token_cipher = excluded.access_token_cipher,
            refresh_token_cipher = excluded.refresh_token_cipher,
            token_expires_at = excluded.token_expires_at,
            granted_scopes = excluded.granted_scopes
         where c.state = 'needs_reauth'
        returning {ViewColumns}
        """;

    /// <summary>The project's connections, live ones first then history newest first, so a live one is on the page whatever the history; no credential column.</summary>
    internal const string List = $"""
        select {ViewColumns}
          from public.tracker_connection
         where org_id = @org_id
           and project_id = @project_id
         order by (state = 'disconnected'), created_at desc, id desc
         limit 100
        """;

    /// <summary>The one non-disconnected connection of a project and provider; the partial unique key serves it.</summary>
    internal const string FindLive = $"""
        select {ViewColumns}
          from public.tracker_connection
         where org_id = @org_id
           and project_id = @project_id
           and provider = @provider
           and state <> 'disconnected'
        """;

    /// <summary>The provider of a live connection, read unlocked so the consents can be cancelled before the connection row is locked.</summary>
    internal const string LiveProvider = """
        select provider
          from public.tracker_connection
         where org_id = @org_id
           and project_id = @project_id
           and id = @id
           and state <> 'disconnected'
        """;

    /// <summary>Disconnects a live connection and clears both ciphers; returns its provider, or nothing.</summary>
    internal const string Disconnect = """
        update public.tracker_connection
           set state = 'disconnected',
               access_token_cipher = null,
               refresh_token_cipher = null,
               disconnected_at = @now
         where org_id = @org_id
           and project_id = @project_id
           and id = @id
           and state <> 'disconnected'
        returning provider
        """;

    /// <summary>
    /// Cancels every consent of the project and provider that has not completed: an unspent, unexpired one is spent
    /// and cancelled, and one already taken (an exchange in flight) is cancelled so its completion is refused.
    /// </summary>
    internal const string CancelOpenConsents = """
        update public.tracker_oauth_state
           set consumed_at = coalesce(consumed_at, @now),
               cancelled_at = @now
         where org_id = @org_id
           and project_id = @project_id
           and provider = @provider
           and completed_at is null
           and cancelled_at is null
           and (consumed_at is not null or expires_at > @now)
        """;
}
