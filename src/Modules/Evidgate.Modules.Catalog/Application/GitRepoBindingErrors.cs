using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>The machine codes the repository-binding routes add to the 00 §9.2 vocabulary.</summary>
internal static class GitRepoBindingErrorCodes
{
    /// <summary>403: the installation id is unknown, another org's, or this org's but suspended.</summary>
    internal const string InstallationNotOwned = "installation_not_owned";

    /// <summary>422: the installation cannot read the repository, so a sync against it would fail.</summary>
    internal const string RepositoryUnreachable = "repository_unreachable";

    /// <summary>404: no live binding with that id is visible to this tenant.</summary>
    internal const string BindingNotFound = "binding_not_found";
}

/// <summary>The refusals of the binding routes, in one place so their codes cannot drift apart.</summary>
internal static class GitRepoBindingErrors
{
    /// <summary>
    /// 403: the installation id is unknown, belongs to another org, or is this org's but suspended — the ownership
    /// unit only accepts an active installation of the caller's org.
    /// </summary>
    internal static Error InstallationNotOwned { get; } = Error.Denied(
        GitRepoBindingErrorCodes.InstallationNotOwned,
        "the installation id is not this org's active GitHub App installation");

    /// <summary>422: GitHub answered that this installation cannot read the repository.</summary>
    internal static Error RepositoryUnreachable { get; } = Error.Unprocessable(
        GitRepoBindingErrorCodes.RepositoryUnreachable,
        "the GitHub installation cannot read this repository; grant it access at GitHub and register again");

    /// <summary>409: this project already binds the repository.</summary>
    internal static Error AlreadyBound { get; } = Error.Conflict(
        ApiErrorCode.BindingExists,
        "this project already binds that repository");

    /// <summary>409: another project in this org already binds the repository in this installation.</summary>
    internal static Error BoundElsewhere { get; } = Error.Conflict(
        ApiErrorCode.BindingConflict,
        "another project in this org already binds that repository in this installation");

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
                or GitHubErrorCodes.PermissionNotGranted or GitHubErrorCodes.Unprocessable => RepositoryUnreachable,
            GitHubErrorCodes.NotConfigured => NotConfigured,
            _ => error,
        };
    }
}
