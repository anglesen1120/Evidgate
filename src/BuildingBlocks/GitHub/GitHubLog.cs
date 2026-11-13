using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// The App client's log lines. They name an operation, an installation, a feature or a status code — never a token,
/// a private key, a client secret or a response body, which is what lets the client log failures at all.
/// </summary>
public static partial class GitHubLog
{
    [LoggerMessage(
        EventId = 4401,
        Level = LogLevel.Warning,
        Message = "GitHub installation token exchange failed for installation {InstallationId} feature {Feature} with {Code}.")]
    public static partial void TokenExchangeFailed(ILogger logger, long installationId, string feature, string code);

    [LoggerMessage(
        EventId = 4402,
        Level = LogLevel.Debug,
        Message = "Minted a GitHub installation token for installation {InstallationId} feature {Feature} expiring at {ExpiresAt}.")]
    public static partial void TokenMinted(ILogger logger, long installationId, string feature, DateTimeOffset expiresAt);

    [LoggerMessage(
        EventId = 4403,
        Level = LogLevel.Warning,
        Message = "GitHub call {Operation} failed with {Status} (code {Code}).")]
    public static partial void CallFailed(ILogger logger, string operation, int status, string code);

    [LoggerMessage(
        EventId = 4404,
        Level = LogLevel.Information,
        Message = "GitHub refused an installation token of org {OrgId}; the cached token was evicted and the call retried once.")]
    public static partial void TokenEvicted(ILogger logger, Guid orgId);

    [LoggerMessage(
        EventId = 4405,
        Level = LogLevel.Warning,
        Message = "The configured GitHub App private key cannot sign an App JWT ({ExceptionType}); every call reports github_not_configured.")]
    public static partial void TokenSigningFailed(ILogger logger, string exceptionType);

    [LoggerMessage(
        EventId = 4406,
        Level = LogLevel.Warning,
        Message = "The GitHub App is not configured; the install flow reports {Code} and no request is sent.")]
    public static partial void AppNotConfigured(ILogger logger, string code);
}
