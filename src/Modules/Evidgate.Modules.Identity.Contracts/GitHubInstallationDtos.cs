namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// Body of <c>POST /v1/github/installations/start</c>: the GitHub install URL the browser is sent to. The
/// <c>state</c> it carries is single-use, bound to the caller's org and user, and expires in ten minutes.
/// </summary>
/// <param name="Url">Absolute install URL on the pinned GitHub host.</param>
public sealed record GitHubInstallStartResponse(string Url);

/// <summary>
/// One org's GitHub App installation as the API reports it. It carries no secret: the row holds an installation id,
/// the account it belongs to, the granted permission map and the state.
/// </summary>
/// <param name="Id">The row id this org disconnects (the <c>{i}</c> of the disconnect route).</param>
/// <param name="InstallationId">Numeric installation id GitHub issued.</param>
/// <param name="AccountLogin">Login of the account the App is installed on.</param>
/// <param name="State"><c>active</c>, <c>suspended</c> or <c>deleted</c>.</param>
/// <param name="Permissions">Repository permission map the account granted, as GitHub reports it.</param>
public sealed record GitHubInstallationDto(
    Guid Id,
    long InstallationId,
    string AccountLogin,
    string State,
    IReadOnlyDictionary<string, string> Permissions);
