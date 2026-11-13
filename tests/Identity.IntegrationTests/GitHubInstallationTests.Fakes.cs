using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Infrastructure.GitHub;

namespace Identity.IntegrationTests;

/// <summary>
/// The OAuth half of the callback, faked: the code always exchanges, the user is the seeded admin, and whether the
/// user administers the installation's account is the case's own decision.
/// </summary>
internal sealed class FakeGitHubOAuth : IGitHubOAuthClient
{
    /// <summary>A token-shaped value that must never appear in a response, a log or the database.</summary>
    public const string UserToken = "ghu_installation-probe-secret";

    public required long UserId { get; init; }

    public List<GitHubUserInstallation> Installations { get; } = [];

    public bool Administers { get; init; }

    /// <summary>Every code exchange the callback made; a refused attempt must not make a second one.</summary>
    public int ExchangeCalls { get; private set; }

    /// <summary>Every token revoke the callback attempted, so the best-effort call is provable.</summary>
    public int RevokeCalls { get; private set; }

    public Task<Result<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        ExchangeCalls++;

        return Task.FromResult(Result.Success(UserToken));
    }

    public Task<Result<GitHubUser>> GetUserAsync(string userToken, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new GitHubUser(UserId, "probe-user")));

    public Task<Result<IReadOnlyList<GitHubUserInstallation>>> ListInstallationsAsync(
        string userToken,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success<IReadOnlyList<GitHubUserInstallation>>(Installations));

    public Task<Result<bool>> AdministersAsync(
        string userToken,
        GitHubUser user,
        GitHubUserInstallation installation,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(Administers));

    public Task RevokeUserTokenAsync(string userToken, CancellationToken cancellationToken)
    {
        RevokeCalls++;

        return Task.CompletedTask;
    }
}

/// <summary>The App-authenticated lookup, faked: one installation or a 404, and a count of the calls.</summary>
internal sealed class FakeGitHubAppInstallations : IGitHubAppInstallations
{
    public GitHubAppInstallation? Installation { get; init; }

    /// <summary>Every lookup the callback made; a state refused at consumption makes none.</summary>
    public int Lookups { get; private set; }

    public Task<Result<GitHubAppInstallation>> GetAsync(long installationId, CancellationToken cancellationToken)
    {
        Lookups++;

        return Task.FromResult(
            Installation is { } found && found.Id == installationId
                ? Result.Success(found)
                : Result.Failure<GitHubAppInstallation>(new Error(GitHubErrorCodes.NotFound, "probe", 404)));
    }
}
