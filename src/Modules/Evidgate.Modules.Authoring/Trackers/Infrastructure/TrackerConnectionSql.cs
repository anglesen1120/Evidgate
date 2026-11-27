namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>
/// The statements of the tracker connection store. Every one names <c>org_id</c>, so a sibling tenant's row is never
/// read even before row level security answers, and the status reads list only the non-credential columns.
/// </summary>
internal static class TrackerConnectionSql
{
    /// <summary>The columns of a connection as the API may show it: never a token column.</summary>
    private const string ViewColumns = "id, project_id, provider, state, granted_scopes, create_issue_enabled, created_at";

    /// <summary>Stores one pending consent; the digest is the lookup key, the verifier is already ciphertext.</summary>
    internal const string InsertConsent = """
        insert into public.tracker_oauth_state
            (id, org_id, project_id, user_id, provider, state_hash, pkce_verifier_cipher, redirect_uri, expires_at, created_at)
        values
            (@id, @org_id, @project_id, @user_id, @provider, @state_hash, @pkce_verifier_cipher, @redirect_uri, @expires_at, @now)
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
        returning project_id, provider, redirect_uri, pkce_verifier_cipher
        """;

    /// <summary>The state a failed take found, to name the refusal; read only after the take returned no row.</summary>
    internal const string ConsentState = """
        select user_id, consumed_at is not null as consumed
          from public.tracker_oauth_state
         where org_id = @org_id
           and state_hash = @state_hash
        """;

    /// <summary>
    /// Creates the project's connection, or refreshes the live one for the same provider in place: the partial unique
    /// key makes this one race-free statement, and a refresh keeps the first creator and an unreplaced refresh token.
    /// </summary>
    internal const string Upsert = $"""
        insert into public.tracker_connection as c
            (id, org_id, project_id, provider, created_by_membership_id, access_token_cipher, refresh_token_cipher,
             token_expires_at, granted_scopes)
        values
            (@id, @org_id, @project_id, @provider,
             (select m.id
                from public.membership m
               where m.org_id = @org_id and m.user_id = @user_id and m.status = 'active'
               order by m.id
               limit 1),
             @access_token_cipher, @refresh_token_cipher, @token_expires_at, @granted_scopes)
        on conflict (org_id, project_id, provider) where state <> 'disconnected'
        do update set
            state = 'active',
            access_token_cipher = excluded.access_token_cipher,
            refresh_token_cipher = coalesce(excluded.refresh_token_cipher, c.refresh_token_cipher),
            token_expires_at = excluded.token_expires_at,
            granted_scopes = excluded.granted_scopes
        returning {ViewColumns}
        """;

    /// <summary>The project's connections, oldest first; credential columns are not selected.</summary>
    internal const string List = $"""
        select {ViewColumns}
          from public.tracker_connection
         where org_id = @org_id
           and project_id = @project_id
         order by created_at, id
         limit 100
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

    /// <summary>Spends the consents still open for a project and provider, so a disconnect leaves none to complete.</summary>
    internal const string SpendOpenConsents = """
        update public.tracker_oauth_state
           set consumed_at = @now
         where org_id = @org_id
           and project_id = @project_id
           and provider = @provider
           and consumed_at is null
        """;
}
