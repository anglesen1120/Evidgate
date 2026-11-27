using Evidgate.Modules.Authoring.Trackers.Application;

namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>A connection row as <see cref="TrackerConnectionSql"/> returns it: no credential member exists.</summary>
internal sealed class TrackerConnectionRow
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string GrantedScopes { get; set; } = string.Empty;

    public bool CreateIssueEnabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The consent a take returned; the verifier is still ciphertext.</summary>
internal sealed class TrackerConsentRow
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    public string? PkceVerifierCipher { get; set; }
}

/// <summary>What a failed take found for the digest: whose consent it is and whether it was already spent.</summary>
internal sealed class TrackerConsentStateRow
{
    public Guid UserId { get; set; }

    public bool Consumed { get; set; }
}

/// <summary>The stored text of the provider, state and scope columns and its typed counterpart.</summary>
internal static class TrackerWire
{
    internal static string Code(TrackerProvider provider) => provider switch
    {
        TrackerProvider.JiraCloud => "jira_cloud",
        TrackerProvider.Linear => "linear",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "unknown tracker provider"),
    };

    internal static TrackerProvider ParseProvider(string code) => code switch
    {
        "jira_cloud" => TrackerProvider.JiraCloud,
        "linear" => TrackerProvider.Linear,
        _ => throw new InvalidOperationException("the stored tracker provider is not a known value"),
    };

    internal static TrackerConnectionState ParseState(string code) => code switch
    {
        "active" => TrackerConnectionState.Active,
        "needs_reauth" => TrackerConnectionState.NeedsReauth,
        "disconnected" => TrackerConnectionState.Disconnected,
        _ => throw new InvalidOperationException("the stored tracker connection state is not a known value"),
    };

    /// <summary>Joins scopes into the stored space-delimited form; a scope never holds whitespace.</summary>
    internal static string JoinScopes(IReadOnlyList<string> scopes)
    {
        foreach (var scope in scopes)
        {
            if (string.IsNullOrEmpty(scope) || scope.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException("a granted scope is a non-empty token without whitespace", nameof(scopes));
            }
        }

        return string.Join(' ', scopes);
    }

    internal static TrackerConnectionView ToView(TrackerConnectionRow row) => new(
        row.Id,
        row.ProjectId,
        ParseProvider(row.Provider),
        ParseState(row.State),
        row.GrantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        row.CreateIssueEnabled,
        row.CreatedAt);
}
