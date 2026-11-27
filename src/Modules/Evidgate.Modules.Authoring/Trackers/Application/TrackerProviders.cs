namespace Evidgate.Modules.Authoring.Trackers.Application;

/// <summary>
/// The wire names and the fixed read-only scopes of the two trackers. The scopes are code constants, never
/// configuration: nothing in a request or in an environment can widen a connection to write access.
/// </summary>
internal static class TrackerProviders
{
    /// <summary>The scope string Jira Cloud is asked for: read issues, plus a refresh token.</summary>
    internal const string JiraScope = "read:jira-work offline_access";

    /// <summary>The scope string Linear is asked for.</summary>
    internal const string LinearScope = "read";

    /// <summary>The wire name of <paramref name="provider"/>.</summary>
    internal static string ToWire(TrackerProvider provider) => provider switch
    {
        TrackerProvider.JiraCloud => "jira_cloud",
        TrackerProvider.Linear => "linear",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>The provider of a route segment; false for anything but the two exact wire names.</summary>
    internal static bool TryParse(string? wire, out TrackerProvider provider)
    {
        switch (wire)
        {
            case "jira_cloud":
                provider = TrackerProvider.JiraCloud;
                return true;
            case "linear":
                provider = TrackerProvider.Linear;
                return true;
            default:
                provider = default;
                return false;
        }
    }

    /// <summary>The wire name of a connection state.</summary>
    internal static string ToWire(TrackerConnectionState state) => state switch
    {
        TrackerConnectionState.Active => "active",
        TrackerConnectionState.NeedsReauth => "needs_reauth",
        TrackerConnectionState.Disconnected => "disconnected",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>The scopes requested from <paramref name="provider"/>, space separated.</summary>
    internal static string Scope(TrackerProvider provider) =>
        provider == TrackerProvider.JiraCloud ? JiraScope : LinearScope;
}
