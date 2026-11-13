using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Infrastructure.GitHub;

/// <summary>
/// The App-authenticated installation lookup: <c>GET /app/installations/{id}</c> with the App JWT proves the
/// installation exists and is visible to this App before any row is written. The JWT never leaves this type.
/// </summary>
internal sealed partial class GitHubAppInstallationsClient(
    IHttpClientFactory clients,
    GitHubAppTokenSource appTokens,
    IOptions<GitHubOptions> options,
    TimeProvider time,
    ILogger<GitHubAppInstallationsClient> logger) : IGitHubAppInstallations
{
    private const int MaxLoginLength = 256;
    private const int MaxAccountTypeLength = 32;
    private const int MaxPermissionEntries = 64;

    /// <inheritdoc />
    public async Task<Result<GitHubAppInstallation>> GetAsync(long installationId, CancellationToken cancellationToken)
    {
        if (installationId <= 0)
        {
            return Result.Failure<GitHubAppInstallation>(GitHubResponseReader.InvalidParameters());
        }

        var jwt = appTokens.Create();
        if (!jwt.IsSuccess)
        {
            return Result.Failure<GitHubAppInstallation>(jwt.Error!);
        }

        var configuration = options.Value;
        if (!Uri.TryCreate(configuration.ApiBaseUrl, UriKind.Absolute, out var baseUri)
            || !GitHubEndpoints.IsAllowedApiBase(baseUri))
        {
            return Result.Failure<GitHubAppInstallation>(GitHubResponseReader.InvalidParameters());
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(GitHubHttpClients.TotalTimeout(configuration));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"app/installations/{installationId}")));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt.Value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubEndpoints.ApiMediaType));
        request.Headers.Add("X-GitHub-Api-Version", GitHubEndpoints.ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(GitHubEndpoints.UserAgent, "1.0"));

        var client = clients.CreateClient(GitHubHttpClients.Api);

        HttpResponseMessage? response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or OperationCanceledException)
        {
            response = null;
        }

        if (response is null)
        {
            return Result.Failure<GitHubAppInstallation>(GitHubResponseReader.Unavailable());
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var error = GitHubResponseReader.MapStatus(response, time.GetUtcNow());
            LogCallFailed(installationId, (int)response.StatusCode, error.Code);

            return Result.Failure<GitHubAppInstallation>(error);
        }

        Result<JsonDocument> document;
        try
        {
            document = await GitHubResponseReader
                .ReadJsonAsync(response, configuration.MaxResponseBytes, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return Result.Failure<GitHubAppInstallation>(GitHubResponseReader.Unavailable());
        }

        if (!document.IsSuccess)
        {
            return Result.Failure<GitHubAppInstallation>(document.Error!);
        }

        using var parsed = document.Value;
        if (ReadInstallation(parsed.RootElement, installationId) is not { } installation)
        {
            return Result.Failure<GitHubAppInstallation>(GitHubResponseReader.Unusable());
        }

        return Result.Success(installation);
    }

    private static GitHubAppInstallation? ReadInstallation(JsonElement root, long expectedId)
    {
        if (!GitHubJson.TryGetId(root, "id", out var id)
            || id != expectedId
            || !root.TryGetProperty("account", out var account)
            || account.ValueKind != JsonValueKind.Object
            || !GitHubJson.TryGetId(account, "id", out var accountId)
            || !GitHubJson.TryGetString(account, "login", MaxLoginLength, out var login)
            || !GitHubJson.TryGetString(account, "type", MaxAccountTypeLength, out var type)
            || !root.TryGetProperty("permissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var granted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in permissions.EnumerateObject())
        {
            if (granted.Count >= MaxPermissionEntries
                || entry.Value.ValueKind != JsonValueKind.String
                || entry.Value.GetString() is not { } level
                || level.Length > 32)
            {
                return null;
            }

            granted[entry.Name] = level;
        }

        return new GitHubAppInstallation(id, login, accountId, type, granted);
    }

    [LoggerMessage(
        EventId = 4720,
        Level = LogLevel.Warning,
        Message = "GitHub installation lookup {InstallationId} failed with {Status} (code {Code}).")]
    private partial void LogCallFailed(long installationId, int status, string code);
}
