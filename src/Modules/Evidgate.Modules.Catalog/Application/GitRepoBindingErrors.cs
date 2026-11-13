using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>The machine codes the repository-binding routes add to the 00 §9.2 vocabulary.</summary>
internal static class GitRepoBindingErrorCodes
{
    /// <summary>403: the installation id is not this org's own live installation.</summary>
    internal const string InstallationNotOwned = "installation_not_owned";

    /// <summary>409: the org has no live installation, or the installation cannot mint a token.</summary>
    internal const string InstallationUnavailable = "installation_unavailable";

    /// <summary>422: the installation cannot read the repository, so a sync against it would fail.</summary>
    internal const string RepositoryUnreachable = "repository_unreachable";

    /// <summary>404: no live binding with that id is visible to this tenant.</summary>
    internal const string BindingNotFound = "binding_not_found";
}

/// <summary>The refusals of the binding routes, in one place so their codes cannot drift apart.</summary>
internal static class GitRepoBindingErrors
{
    /// <summary>403: the installation belongs to another org, is unknown, or is not the live one.</summary>
    internal static Error InstallationNotOwned { get; } = Error.Denied(
        GitRepoBindingErrorCodes.InstallationNotOwned,
        "the installation id is not this org's live GitHub App installation");

    /// <summary>409: the org holds no live installation, so no repository can be verified through one.</summary>
    internal static Error InstallationUnavailable { get; } = Error.Conflict(
        GitRepoBindingErrorCodes.InstallationUnavailable,
        "this org has no live GitHub installation, or its installation is suspended");

    /// <summary>422: GitHub answered that this installation cannot read the repository.</summary>
    internal static Error RepositoryUnreachable { get; } = Error.Unprocessable(
        GitRepoBindingErrorCodes.RepositoryUnreachable,
        "the GitHub installation cannot read this repository; grant it access at GitHub and register again");

    /// <summary>409: this project already binds the repository.</summary>
    internal static Error AlreadyBound { get; } = Error.Conflict(
        ApiErrorCode.BindingExists,
        "this project already binds that repository");

    /// <summary>409: another org already binds the repository in this installation.</summary>
    internal static Error BoundElsewhere { get; } = Error.Conflict(
        ApiErrorCode.BindingConflict,
        "another org already binds that repository in this installation");

    /// <summary>404: the binding id names no live binding of the project.</summary>
    internal static Error BindingNotFound { get; } = new(
        GitRepoBindingErrorCodes.BindingNotFound,
        "no repository binding with that id is visible to this tenant",
        StatusCodes.Status404NotFound);

    /// <summary>503: no GitHub App is configured, so the binding cannot be verified (no outbound call was made).</summary>
    internal static Error NotConfigured { get; } = new(
        GitHubErrorCodes.NotConfigured,
        "no GitHub App is configured on this host, so a repository cannot be verified",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    /// <summary>
    /// Maps the verification call's failure: a repository the installation cannot read is the caller's 422, an
    /// unconfigured App its 503, and an outage or rate limit keeps the retryable status GitHub reported.
    /// </summary>
    /// <param name="error">The failure <see cref="Infrastructure.GitHub.GitHubClient"/> returned.</param>
    internal static Error FromVerification(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Code switch
        {
            GitHubErrorCodes.NotFound or GitHubErrorCodes.Forbidden or GitHubErrorCodes.Unauthorized
                or GitHubErrorCodes.PermissionNotGranted => RepositoryUnreachable,
            GitHubErrorCodes.NotConfigured => NotConfigured,
            // The token source resolves the org's live installation first, so "no live installation" answers with the
            // installation vocabulary rather than as a repository the caller got wrong.
            "installation_unavailable" => InstallationUnavailable,
            _ => error,
        };
    }
}
