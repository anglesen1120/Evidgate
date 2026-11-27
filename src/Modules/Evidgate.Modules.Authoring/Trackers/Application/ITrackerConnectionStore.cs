namespace Evidgate.Modules.Authoring.Trackers.Application;

internal enum TrackerProvider { JiraCloud, Linear }
internal enum TrackerConnectionState { Active, NeedsReauth, Disconnected }

internal sealed record PendingConsent(
    Guid OrgId, Guid ProjectId, Guid UserId, TrackerProvider Provider,
    string RedirectUri, string? PkceVerifier);

internal sealed record TrackerConnectionView(
    Guid Id, Guid ProjectId, TrackerProvider Provider, TrackerConnectionState State,
    IReadOnlyList<string> GrantedScopes, bool CreateIssueEnabled, DateTimeOffset CreatedAt);

internal sealed record NewTrackerConnection(
    Guid ProjectId, TrackerProvider Provider, Guid UserId, string AccessToken,
    string? RefreshToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string> GrantedScopes);

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
    Task SaveConsentAsync(byte[] stateHash, PendingConsent consent, TimeSpan ttl, CancellationToken ct);
    Task<ConsentLookup> TakeConsentAsync(byte[] stateHash, Guid userId, CancellationToken ct);
    Task<TrackerConnectionView> CreateAsync(NewTrackerConnection connection, CancellationToken ct);
    Task<IReadOnlyList<TrackerConnectionView>> ListAsync(Guid projectId, CancellationToken ct);
    Task<bool> DisconnectAsync(Guid projectId, Guid connectionId, CancellationToken ct);
}
