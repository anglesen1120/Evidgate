using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// The App client's log lines. They name an operation, an installation or a status code — never a
/// token, a private key or a response body, which is what lets the client log failures at all.
/// </summary>
internal static partial class GitHubLog
{
    [LoggerMessage(
        EventId = 4401,
        Level = LogLevel.Warning,
        Message = "GitHub installation token exchange failed for installation {InstallationId} with {Code}.")]
    internal static partial void TokenExchangeFailed(ILogger logger, long installationId, string code);

    [LoggerMessage(
        EventId = 4402,
        Level = LogLevel.Debug,
        Message = "Minted a GitHub installation token for installation {InstallationId} expiring at {ExpiresAt}.")]
    internal static partial void TokenMinted(ILogger logger, long installationId, DateTimeOffset expiresAt);

    [LoggerMessage(
        EventId = 4403,
        Level = LogLevel.Warning,
        Message = "GitHub call {Operation} failed with {Status} (code {Code}).")]
    internal static partial void CallFailed(ILogger logger, string operation, int status, string code);

    [LoggerMessage(
        EventId = 4404,
        Level = LogLevel.Information,
        Message = "GitHub refused the installation token of installation {InstallationId}; the cached token was evicted and the call retried once.")]
    internal static partial void TokenEvicted(ILogger logger, long installationId);
}
