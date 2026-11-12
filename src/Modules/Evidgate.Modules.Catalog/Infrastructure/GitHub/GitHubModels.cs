using System.Globalization;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>One GitHub App installation, as <c>GET /app/installations/{id}</c> reports it.</summary>
/// <param name="Id">Numeric installation id.</param>
/// <param name="AccountLogin">Login of the account the App is installed on.</param>
/// <param name="RepositorySelection">GitHub's selection mode: <c>all</c> or <c>selected</c>.</param>
internal sealed record GitHubInstallation(long Id, string AccountLogin, string RepositorySelection);

/// <summary>One repository, as <c>GET /repositories/{id}</c> reports it.</summary>
/// <param name="Id">Numeric repository id; the binding stores this, never a name.</param>
/// <param name="FullName">Owner-scoped name, e.g. <c>evidgate/catalog</c>.</param>
/// <param name="DefaultBranch">Branch name of the default branch.</param>
internal sealed record GitHubRepository(long Id, string FullName, string DefaultBranch);

/// <summary>One tree entry of a git tree response.</summary>
/// <param name="Path">Repository-relative path; validated by <see cref="GitHubTree"/>'s reader.</param>
/// <param name="Type"><c>blob</c>, <c>tree</c> or <c>commit</c>.</param>
/// <param name="Sha">Object id of the entry.</param>
/// <param name="Size">Blob size in bytes; zero for a tree entry.</param>
internal sealed record GitHubTreeEntry(string Path, string Type, string Sha, long Size);

/// <summary>A git tree, recursive when the caller asked for it.</summary>
/// <param name="Sha">Object id of the tree itself.</param>
/// <param name="Truncated">True when GitHub returned fewer entries than the tree holds.</param>
/// <param name="Entries">The entries, capped by <see cref="GitHubOptions.MaxTreeEntries"/>.</param>
internal sealed record GitHubTree(string Sha, bool Truncated, IReadOnlyList<GitHubTreeEntry> Entries);

/// <summary>A decoded git blob. Its bytes are file content, never executed at sync.</summary>
/// <param name="Sha">Object id of the blob.</param>
/// <param name="Size">Size GitHub declared, in bytes.</param>
/// <param name="Content">The decoded content, at most <see cref="GitHubOptions.MaxBlobBytes"/>.</param>
internal sealed record GitHubBlob(string Sha, long Size, byte[] Content);

/// <summary>
/// One installation access token. The value is derived, short-lived state: it is never persisted,
/// and <see cref="ToString"/> redacts it so an interpolated log line cannot leak it.
/// </summary>
/// <param name="Token">The bearer value GitHub issued.</param>
/// <param name="ExpiresAt">Expiry GitHub declared.</param>
internal sealed record GitHubInstallationToken(string Token, DateTimeOffset ExpiresAt)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"GitHubInstallationToken(expires_at={ExpiresAt:O}, redacted)");
}
