namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// The consumers that mint installation tokens. Each value is one row of the single App's permission matrix; the
/// compiled map below is the ceiling for that feature, so a caller can never ask for a broader token than its row.
/// </summary>
public enum GitHubFeature
{
    /// <summary>Catalog sync reads repository contents (03.4.1).</summary>
    Catalog,

    /// <summary>Check-run delivery and the commit-status fallback (07.6.1).</summary>
    Checks,

    /// <summary>Healing pull requests (03.4.2, 08.6).</summary>
    HealingPullRequest,

    /// <summary>Workflow re-run (05.5.2).</summary>
    ActionsRerun,
}

/// <summary>One feature's minted permission subset, its cache identity and whether it must name repositories.</summary>
/// <param name="CacheKey">Canonical text of the permission set, part of the token cache key.</param>
/// <param name="Permissions">Repository permissions sent in the mint body, sorted by name.</param>
/// <param name="RepositoryScoped">True when the mint must also name the repositories it may act on.</param>
public sealed record GitHubPermissionScope(
    string CacheKey,
    IReadOnlyDictionary<string, string> Permissions,
    bool RepositoryScoped);

/// <summary>
/// The compiled feature → permission map (ADR-0007 §Permission matrix). It is deliberately not configuration and not
/// a call parameter: omitting <c>permissions</c> from a mint yields a token with every permission the installation was
/// granted, so the safe default is a table a feature cannot widen.
/// </summary>
public static class GitHubFeaturePermissions
{
    private static readonly Dictionary<GitHubFeature, GitHubPermissionScope> Map = new()
    {
        [GitHubFeature.Catalog] = Scope(("contents", "read"), ("metadata", "read")),
        [GitHubFeature.Checks] = Scope(("checks", "write"), ("metadata", "read")),
        [GitHubFeature.HealingPullRequest] = Scope(("contents", "write"), ("metadata", "read"), ("pull_requests", "write")),
        [GitHubFeature.ActionsRerun] = Scope(("actions", "write"), ("metadata", "read")),
    };

    /// <summary>
    /// The scope of <paramref name="feature"/>. An unknown value and an empty map both throw: an empty map would send
    /// no <c>permissions</c> member and mint a token with every permission the installation holds.
    /// </summary>
    /// <param name="feature">The consuming feature.</param>
    public static GitHubPermissionScope For(GitHubFeature feature)
    {
        if (!Map.TryGetValue(feature, out var scope) || scope.Permissions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No GitHub permission map is compiled for feature '{feature}'; minting would request every granted permission.");
        }

        return scope;
    }

    private static GitHubPermissionScope Scope(params (string Name, string Level)[] entries)
    {
        var permissions = entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Name, entry => entry.Level, StringComparer.Ordinal);

        var cacheKey = string.Join(
            '&',
            permissions.Select(entry => string.Concat(entry.Key, ":", entry.Value)));

        // Every row in the matrix acts on repositories, so every mint names them (ADR-0007 token-per-feature rule).
        return new GitHubPermissionScope(cacheKey, permissions, RepositoryScoped: true);
    }
}
