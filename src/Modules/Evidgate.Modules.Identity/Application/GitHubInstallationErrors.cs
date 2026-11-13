using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Application;

/// <summary>The machine codes the GitHub installation routes add to the 00 §9.2 vocabulary.</summary>
internal static class GitHubInstallationErrorCodes
{
    /// <summary>400: the nonce is missing, expired, already used, or belongs to another org or user.</summary>
    public const string StateInvalid = "state_invalid";

    /// <summary>403: the OAuth user neither reaches the installation nor administers its account.</summary>
    public const string InstallationNotOwned = "installation_not_owned";

    /// <summary>409: the installation or the org is already bound.</summary>
    public const string InstallationBound = "installation_bound";

    /// <summary>409: the org has no active installation, or the bound one is suspended or deleted.</summary>
    public const string InstallationUnavailable = "installation_unavailable";

    /// <summary>409: the org already has an installation to disconnect first.</summary>
    public const string AlreadyInstalled = "already_installed";

    /// <summary>429: this user already holds the maximum number of unspent install states.</summary>
    public const string TooManyFlows = "install_flow_limited";

    /// <summary>400: GitHub refused the authorization code, or the code exchange answered no token.</summary>
    public const string OAuthRefused = "github_oauth_refused";
}

/// <summary>The refusals of the GitHub installation routes, in one place so their codes cannot drift apart.</summary>
internal static class GitHubInstallationErrors
{
    /// <summary>400: the nonce is absent, expired, replayed, or bound to another org or user.</summary>
    public static Error StateInvalid { get; } = new(
        GitHubInstallationErrorCodes.StateInvalid,
        "the install state is missing, expired, already used, or bound to another org or user",
        StatusCodes.Status400BadRequest);

    /// <summary>403: the OAuth user is not entitled to bind this installation.</summary>
    public static Error NotOwned { get; } = Error.Denied(
        GitHubInstallationErrorCodes.InstallationNotOwned,
        "the GitHub user who completed authorization neither reaches this installation nor administers its account");

    /// <summary>409: another org, or this org with another installation, already holds the binding.</summary>
    public static Error Bound { get; } = Error.Conflict(
        GitHubInstallationErrorCodes.InstallationBound,
        "this installation is already bound to another org, this org already holds a live installation, or a "
        + "disconnected row still holds this GitHub installation id; disconnect the live one, or install the App "
        + "again at GitHub");

    /// <summary>409: the installation exists but may not mint tokens or be read.</summary>
    public static Error Unavailable { get; } = Error.Conflict(
        GitHubInstallationErrorCodes.InstallationUnavailable,
        "the GitHub installation is suspended or deleted, or this org has none");

    /// <summary>409: the org already has a live installation, so a second one is refused.</summary>
    public static Error AlreadyInstalled { get; } = Error.Conflict(
        GitHubInstallationErrorCodes.AlreadyInstalled,
        "this org already has a GitHub installation; disconnect it before installing again");

    /// <summary>503: the App's id, client id or slug is absent, so no install URL can be built.</summary>
    public static Error NotConfigured { get; } = new(
        "github_not_configured",
        "no GitHub App is configured on this host",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    /// <summary>400: GitHub refused the authorization code; the flow must start again.</summary>
    public static Error CodeRefused { get; } = new(
        "request_invalid",
        "GitHub refused the authorization code; start the install flow again",
        StatusCodes.Status400BadRequest);

    /// <summary>429: the user holds the maximum number of unspent install states; one expires or is spent.</summary>
    public static Error TooManyFlows { get; } = new(
        GitHubInstallationErrorCodes.TooManyFlows,
        "this user has too many open install flows; finish one or wait for one to expire",
        StatusCodes.Status429TooManyRequests,
        Retryable: true);
}
