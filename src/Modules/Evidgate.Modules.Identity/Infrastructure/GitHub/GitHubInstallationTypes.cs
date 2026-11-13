using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Infrastructure.GitHub;

/// <summary>The OAuth user who completed the install authorization, as <c>GET /user</c> reports them.</summary>
/// <param name="Id">Numeric GitHub user id; the only identity compared against an account.</param>
/// <param name="Login">Login, used to address the org-membership endpoint.</param>
internal sealed record GitHubUser(long Id, string Login);

/// <summary>One installation the OAuth user can reach, as <c>GET /user/installations</c> reports it.</summary>
/// <param name="Id">Numeric installation id.</param>
/// <param name="AccountLogin">Login of the account the App is installed on.</param>
/// <param name="AccountId">Numeric id of that account.</param>
/// <param name="AccountType"><c>User</c> or <c>Organization</c>.</param>
internal sealed record GitHubUserInstallation(long Id, string AccountLogin, long AccountId, string AccountType);

/// <summary>One installation as <c>GET /app/installations/{id}</c> reports it to the App itself.</summary>
/// <param name="Id">Numeric installation id.</param>
/// <param name="AccountLogin">Login of the account the App is installed on.</param>
/// <param name="AccountId">Numeric id of that account.</param>
/// <param name="AccountType"><c>User</c> or <c>Organization</c>.</param>
/// <param name="Permissions">Repository permission map the account granted.</param>
internal sealed record GitHubAppInstallation(
    long Id,
    string AccountLogin,
    long AccountId,
    string AccountType,
    IReadOnlyDictionary<string, string> Permissions)
{
    /// <summary>When GitHub reports the installation suspended, or null while it is live.</summary>
    public DateTimeOffset? SuspendedAt { get; init; }
}

/// <summary>
/// The OAuth half of the install callback: exchange the code, then prove the user reaches the installation and
/// administers its account. The user token lives only for these calls and is never stored or logged.
/// </summary>
internal interface IGitHubOAuthClient
{
    /// <summary>Exchanges the callback code for a user access token, or a typed failure.</summary>
    Task<Result<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary><c>GET /user</c> with the user token.</summary>
    Task<Result<GitHubUser>> GetUserAsync(string userToken, CancellationToken cancellationToken);

    /// <summary>Every installation the user token reaches, following pagination up to the configured page cap.</summary>
    Task<Result<IReadOnlyList<GitHubUserInstallation>>> ListInstallationsAsync(
        string userToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes a user access token at GitHub (<c>DELETE /applications/{client_id}/token</c>). Best effort: the
    /// callback has already decided its answer, and GitHub expires the token on its own.
    /// </summary>
    Task RevokeUserTokenAsync(string userToken, CancellationToken cancellationToken);

    /// <summary>
    /// The two-part ownership rule: a User account must equal the OAuth user, an Organization account must report an
    /// active admin membership for that user. Anything else is false, never guessed.
    /// </summary>
    Task<Result<bool>> AdministersAsync(
        string userToken,
        GitHubUser user,
        GitHubUserInstallation installation,
        CancellationToken cancellationToken);
}

/// <summary>The App-authenticated installation lookup that verifies an id before any row is written.</summary>
internal interface IGitHubAppInstallations
{
    /// <summary><c>GET /app/installations/{id}</c> with the App JWT.</summary>
    Task<Result<GitHubAppInstallation>> GetAsync(long installationId, CancellationToken cancellationToken);
}
