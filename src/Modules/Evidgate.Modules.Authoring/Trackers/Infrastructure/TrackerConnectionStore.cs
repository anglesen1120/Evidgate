using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Trackers.Application;

namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>
/// The tracker connection store over the caller's open tenant scope. Tokens are protected before they are bound and
/// unprotected only for the PKCE verifier a consent hands back; the status reads never select a credential column.
/// The organisation always comes from the scope, never from an argument, and every statement names it.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="protector">Protects tokens and the PKCE verifier at rest.</param>
/// <param name="time">The clock for expiry and disconnect stamps.</param>
internal sealed class TrackerConnectionStore(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    TrackerTokenProtector protector,
    TimeProvider time) : ITrackerConnectionStore
{
    /// <inheritdoc />
    public async Task SaveConsentAsync(byte[] stateHash, PendingConsent consent, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stateHash);
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        var orgId = scopes.Require().OrgId;
        if (consent.OrgId != orgId)
        {
            throw new InvalidOperationException("the consent names a different tenant than the open scope");
        }

        var now = time.GetUtcNow();
        await db.ExecuteAsync(
            TrackerConnectionSql.InsertConsent,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                project_id = consent.ProjectId,
                user_id = consent.UserId,
                provider = TrackerWire.Code(consent.Provider),
                state_hash = stateHash,
                pkce_verifier_cipher = consent.PkceVerifier is null ? null : protector.ProtectPkceVerifier(consent.PkceVerifier),
                redirect_uri = consent.RedirectUri,
                expires_at = now + ttl,
                now,
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConsentLookup> TakeConsentAsync(byte[] stateHash, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stateHash);

        var orgId = scopes.Require().OrgId;
        var taken = await db.QuerySingleOrDefaultAsync<TrackerConsentRow>(
            TrackerConnectionSql.TakeConsent,
            new { org_id = orgId, state_hash = stateHash, user_id = userId, now = time.GetUtcNow() },
            ct).ConfigureAwait(false);
        if (taken is not null)
        {
            var verifier = taken.PkceVerifierCipher is null ? null : protector.UnprotectPkceVerifier(taken.PkceVerifierCipher);
            return new ConsentLookup.Taken(new PendingConsent(
                orgId, taken.ProjectId, userId, TrackerWire.ParseProvider(taken.Provider), taken.RedirectUri, verifier));
        }

        // Nothing was consumed. The follow-up read only names the refusal; another tenant's digest is invisible under
        // row level security and reads as unknown, and a digest of another user is reported before spent or expired.
        var seen = await db.QuerySingleOrDefaultAsync<TrackerConsentStateRow>(
            TrackerConnectionSql.ConsentState,
            new { org_id = orgId, state_hash = stateHash },
            ct).ConfigureAwait(false);
        if (seen is null)
        {
            return new ConsentLookup.Unknown();
        }

        if (seen.UserId != userId)
        {
            return new ConsentLookup.WrongUser();
        }

        return seen.Consumed ? new ConsentLookup.Replayed() : new ConsentLookup.Expired();
    }

    /// <inheritdoc />
    public async Task<TrackerConnectionView> CreateAsync(NewTrackerConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var orgId = scopes.Require().OrgId;
        var row = await db.QuerySingleOrDefaultAsync<TrackerConnectionRow>(
            TrackerConnectionSql.Upsert,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                project_id = connection.ProjectId,
                provider = TrackerWire.Code(connection.Provider),
                user_id = connection.UserId,
                access_token_cipher = protector.ProtectToken(connection.Provider, connection.AccessToken),
                refresh_token_cipher = connection.RefreshToken is null
                    ? null
                    : protector.ProtectToken(connection.Provider, connection.RefreshToken),
                token_expires_at = connection.ExpiresAt,
                granted_scopes = TrackerWire.JoinScopes(connection.GrantedScopes),
            },
            ct).ConfigureAwait(false);

        return TrackerWire.ToView(row ?? throw new InvalidOperationException("the connection upsert returned no row"));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackerConnectionView>> ListAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await db.QueryAsync<TrackerConnectionRow>(
            TrackerConnectionSql.List,
            new { org_id = scopes.Require().OrgId, project_id = projectId },
            ct).ConfigureAwait(false);

        return [.. rows.Select(TrackerWire.ToView)];
    }

    /// <inheritdoc />
    public async Task<bool> DisconnectAsync(Guid projectId, Guid connectionId, CancellationToken ct)
    {
        var orgId = scopes.Require().OrgId;
        var now = time.GetUtcNow();
        var provider = await db.ScalarAsync<string>(
            TrackerConnectionSql.Disconnect,
            new { org_id = orgId, project_id = projectId, id = connectionId, now },
            ct).ConfigureAwait(false);
        if (provider is null)
        {
            return false;
        }

        // A consent still open for this project and provider would otherwise reconnect what was just disconnected.
        await db.ExecuteAsync(
            TrackerConnectionSql.SpendOpenConsents,
            new { org_id = orgId, project_id = projectId, provider, now },
            ct).ConfigureAwait(false);
        return true;
    }
}
