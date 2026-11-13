using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// One installation access token. The value is derived, short-lived state: it is never persisted, and
/// <see cref="ToString"/> redacts it so an interpolated log line cannot leak it.
/// </summary>
/// <param name="Token">The bearer value GitHub issued.</param>
/// <param name="ExpiresAt">Expiry GitHub declared.</param>
public sealed record GitHubInstallationToken(string Token, DateTimeOffset ExpiresAt)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"GitHubInstallationToken(expires_at={ExpiresAt:O}, redacted)");
}

/// <summary>
/// The token seam every feature uses (ADR-0007): the caller names its feature and its stored repository ids, never a
/// permission set, and receives one cached, least-privilege installation token for the calling org.
/// </summary>
public interface IGitHubInstallationTokens
{
    /// <summary>
    /// A token for <paramref name="orgId"/>'s active installation, carrying only <paramref name="feature"/>'s
    /// permissions. <paramref name="repositoryIds"/> is mandatory for a repository-scoped feature and must come from
    /// the stored binding, never from request input.
    /// </summary>
    /// <param name="orgId">The tenant whose installation mints the token.</param>
    /// <param name="feature">The consuming feature; its compiled permission row is the ceiling.</param>
    /// <param name="repositoryIds">Numeric repository ids from the stored binding.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, not the shared mint.</param>
    Task<Result<GitHubInstallationToken>> GetAsync(
        Guid orgId,
        GitHubFeature feature,
        IReadOnlyList<long>? repositoryIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the active installation id of an org. Implemented by the module that owns <c>github_installation</c>, so
/// the token source stays free of module SQL and of the GitHub feature modules that consume it.
/// </summary>
public interface IGitHubInstallationResolver
{
    /// <summary>
    /// The numeric installation id of <paramref name="orgId"/>'s active installation, or a typed failure when the org
    /// has none, the installation is suspended or deleted, or no App is configured.
    /// </summary>
    /// <param name="orgId">The tenant to resolve.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<Result<long>> ResolveAsync(Guid orgId, CancellationToken cancellationToken);
}
