namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>Repository coordinates; webhook secret references are assigned by the server.</summary>
/// <param name="InstallationId">The org's installed GitHub App installation.</param>
/// <param name="RepositoryId">Repository to verify through that installation.</param>
public sealed record RegisterGitRepoBindingRequest(long InstallationId, long RepositoryId);

/// <summary>A registered repository, without secret material or secret-store coordinates.</summary>
/// <param name="Id">Binding identity.</param>
/// <param name="ProjectId">Project containing the binding.</param>
/// <param name="InstallationId">Verified installation.</param>
/// <param name="RepositoryId">Verified repository.</param>
/// <param name="DefaultBranch">GitHub's default branch at registration.</param>
/// <param name="SyncState">Whether installation events permit synchronization.</param>
public sealed record GitRepoBindingDto(
    Guid Id, Guid ProjectId, long InstallationId, long RepositoryId, string DefaultBranch, string SyncState);
