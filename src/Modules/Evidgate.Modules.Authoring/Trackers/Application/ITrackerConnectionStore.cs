namespace Evidgate.Modules.Authoring.Trackers.Application;

internal enum TrackerProvider { JiraCloud, Linear }
internal enum TrackerConnectionState { Active, NeedsReauth, Disconnected }

/// <param name="ConsentId">The stored consent's id, set when a take returns it; <see cref="Guid.Empty"/> before.</param>
internal sealed record PendingConsent(
    Guid OrgId, Guid ProjectId, Guid UserId, TrackerProvider Provider,
    string RedirectUri, string? PkceVerifier, Guid ConsentId = default)
{
    /// <summary>The type name only: a printed record must never carry the verifier.</summary>
    public override string ToString() => nameof(PendingConsent);
}

internal sealed record TrackerConnectionView(
    Guid Id, Guid ProjectId, TrackerProvider Provider, TrackerConnectionState State,
    IReadOnlyList<string> GrantedScopes, bool CreateIssueEnabled, DateTimeOffset CreatedAt);

/// <param name="MembershipId">The acting member, resolved by the caller; null when the user has no active membership.</param>
/// <param name="ConsentId">The consent this connection completes; an empty id completes nothing and is refused.</param>
internal sealed record NewTrackerConnection(
    Guid ProjectId, TrackerProvider Provider, Guid UserId, string AccessToken,
    string? RefreshToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string> GrantedScopes,
    Guid? MembershipId = null, Guid ConsentId = default)
{
    /// <summary>The type name only: a printed record must never carry a token.</summary>
    public override string ToString() => nameof(NewTrackerConnection);
}

internal abstract record ConsentLookup
{
    internal sealed record Taken(PendingConsent Consent) : ConsentLookup;
    internal sealed record Unknown : ConsentLookup;
    internal sealed record Expired : ConsentLookup;
    internal sealed record Replayed : ConsentLookup;
    internal sealed record WrongUser : ConsentLookup;
}

internal interface ITrackerConnectionStore
{
    /// <summary>Stores the consent; false, with nothing stored, when the user already holds the cap of open consents.</summary>
    Task<bool> SaveConsentAsync(byte[] stateHash, PendingConsent consent, TimeSpan ttl, CancellationToken ct);
    Task<ConsentLookup> TakeConsentAsync(byte[] stateHash, Guid userId, CancellationToken ct);
    /// <summary>
    /// Stores the connection and completes its consent in one statement. Null, with no connection stored, when the
    /// consent was cancelled by a disconnect or completed, or when a live connection is not awaiting reauthorisation.
    /// </summary>
    Task<TrackerConnectionView?> CreateAsync(NewTrackerConnection connection, CancellationToken ct);
    Task<IReadOnlyList<TrackerConnectionView>> ListAsync(Guid projectId, CancellationToken ct);
    /// <summary>The project's non-disconnected connection for the provider, if any.</summary>
    Task<TrackerConnectionView?> FindLiveAsync(Guid projectId, TrackerProvider provider, CancellationToken ct);
    Task<bool> DisconnectAsync(Guid projectId, Guid connectionId, CancellationToken ct);
}
