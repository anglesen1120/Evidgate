using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Infrastructure.GitHub;

/// <summary>
/// The OAuth user half of the install callback. Every request goes to a pinned host, every body is read under the
/// configured cap through the shared reader, and no token or code ever reaches a log line or an error message.
/// </summary>
internal sealed partial class GitHubOAuthClient(
    IHttpClientFactory clients,
    IOptions<GitHubOptions> options,
    TimeProvider time,
    ILogger<GitHubOAuthClient> logger) : IGitHubOAuthClient
{
    private const int MaxPageSize = 100;
    private const int MaxLoginLength = 256;
    private const int MaxTokenLength = 4096;
    private const int MaxAccountTypeLength = 32;

    /// <inheritdoc />
    public async Task<Result<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.ClientId) || string.IsNullOrWhiteSpace(configuration.ClientSecret))
        {
            LogNotConfigured();

            return Result.Failure<string>(GitHubAppTokenSource.NotConfigured());
        }

        if (!TryBaseUri(configuration.OAuthBaseUrl, GitHubEndpoints.IsAllowedOAuthBase, out var baseUri))
        {
            return Result.Failure<string>(GitHubResponseReader.InvalidParameters());
        }

        using var deadline = Deadline(configuration, cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, "login/oauth/access_token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = configuration.ClientId,
                ["client_secret"] = configuration.ClientSecret,
                ["code"] = code,
            }),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubEndpoints.OAuthMediaType));

        var document = await SendAsync(request, GitHubHttpClients.OAuth, deadline.Token, cancellationToken)
            .ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return Result.Failure<string>(document.Error!);
        }

        using var parsed = document.Value;
        if (!GitHubJson.TryGetString(parsed.RootElement, "access_token", MaxTokenLength, out var token))
        {
            // GitHub answers 200 with an `error` member when it refuses a code; that is client input, not an outage.
            return Result.Failure<string>(new("github_oauth_refused", "GitHub refused the authorization code.", 400));
        }

        return Result.Success(token);
    }

    /// <inheritdoc />
    public async Task<Result<GitHubUser>> GetUserAsync(string userToken, CancellationToken cancellationToken)
    {
        var document = await GetAsync(userToken, "user", cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return Result.Failure<GitHubUser>(document.Error!);
        }

        using var parsed = document.Value;
        if (!GitHubJson.TryGetId(parsed.RootElement, "id", out var id)
            || !GitHubJson.TryGetString(parsed.RootElement, "login", MaxLoginLength, out var login))
        {
            return Result.Failure<GitHubUser>(GitHubResponseReader.Unusable());
        }

        return Result.Success(new GitHubUser(id, login));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<GitHubUserInstallation>>> ListInstallationsAsync(
        string userToken,
        CancellationToken cancellationToken)
    {
        var found = new List<GitHubUserInstallation>();
        var maxPages = options.Value.MaxInstallationPages;

        for (var page = 1; page <= maxPages; page++)
        {
            var path = string.Create(
                CultureInfo.InvariantCulture,
                $"user/installations?per_page={MaxPageSize}&page={page}");
            var document = await GetAsync(userToken, path, cancellationToken).ConfigureAwait(false);
            if (!document.IsSuccess)
            {
                return Result.Failure<IReadOnlyList<GitHubUserInstallation>>(document.Error!);
            }

            using var parsed = document.Value;
            if (!parsed.RootElement.TryGetProperty("installations", out var installations)
                || installations.ValueKind != JsonValueKind.Array)
            {
                return Result.Failure<IReadOnlyList<GitHubUserInstallation>>(GitHubResponseReader.Unusable());
            }

            var counted = 0;
            foreach (var entry in installations.EnumerateArray())
            {
                if (ReadInstallation(entry) is not { } installation)
                {
                    return Result.Failure<IReadOnlyList<GitHubUserInstallation>>(GitHubResponseReader.Unusable());
                }

                found.Add(installation);
                counted++;
            }

            if (counted < MaxPageSize)
            {
                break;
            }
        }

        return Result.Success<IReadOnlyList<GitHubUserInstallation>>(found);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> AdministersAsync(
        string userToken,
        GitHubUser user,
        GitHubUserInstallation installation,
        CancellationToken cancellationToken)
    {
        if (string.Equals(installation.AccountType, "User", StringComparison.Ordinal))
        {
            // A personal account is administered by its owner, and the owner is the OAuth user who authorized.
            return Result.Success(installation.AccountId == user.Id);
        }

        if (!string.Equals(installation.AccountType, "Organization", StringComparison.Ordinal))
        {
            return Result.Success(false);
        }

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"user/memberships/orgs/{Uri.EscapeDataString(installation.AccountLogin)}");
        var document = await GetAsync(userToken, path, cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            // A 404 means "not a member", a refusal, not an outage: fail closed without a 5xx.
            return document.Error!.Code == GitHubErrorCodes.NotFound
                ? Result.Success(false)
                : Result.Failure<bool>(document.Error!);
        }

        using var parsed = document.Value;
        var active = GitHubJson.TryGetPropertyString(parsed.RootElement, "state", out var state)
            && string.Equals(state, "active", StringComparison.Ordinal);
        var admin = GitHubJson.TryGetPropertyString(parsed.RootElement, "role", out var role)
            && string.Equals(role, "admin", StringComparison.Ordinal);

        return Result.Success(active && admin);
    }

    private async Task<Result<JsonDocument>> GetAsync(
        string userToken,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (!TryBaseUri(configuration.ApiBaseUrl, GitHubEndpoints.IsAllowedApiBase, out var baseUri))
        {
            return Result.Failure<JsonDocument>(GitHubResponseReader.InvalidParameters());
        }

        using var deadline = Deadline(configuration, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubEndpoints.ApiMediaType));
        request.Headers.Add("X-GitHub-Api-Version", GitHubEndpoints.ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(GitHubEndpoints.UserAgent, "1.0"));

        var document = await SendAsync(request, GitHubHttpClients.Api, deadline.Token, cancellationToken)
            .ConfigureAwait(false);

        return document;
    }

    private async Task<Result<JsonDocument>> SendAsync(
        HttpRequestMessage request,
        string clientName,
        CancellationToken deadlineToken,
        CancellationToken callerToken)
    {
        var client = clients.CreateClient(clientName);

        HttpResponseMessage? response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadlineToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or OperationCanceledException)
        {
            response = null;
        }

        if (response is null)
        {
            return Result.Failure<JsonDocument>(GitHubResponseReader.Unavailable());
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var error = GitHubResponseReader.MapStatus(response, time.GetUtcNow());
            LogCallFailed(request.Method.Method, (int)response.StatusCode, error.Code);

            return Result.Failure<JsonDocument>(error);
        }

        try
        {
            return await GitHubResponseReader
                .ReadJsonAsync(response, options.Value.MaxResponseBytes, deadlineToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return Result.Failure<JsonDocument>(GitHubResponseReader.Unavailable());
        }
    }

    private static GitHubUserInstallation? ReadInstallation(JsonElement entry)
    {
        if (!GitHubJson.TryGetId(entry, "id", out var id)
            || !entry.TryGetProperty("account", out var account)
            || account.ValueKind != JsonValueKind.Object
            || !GitHubJson.TryGetId(account, "id", out var accountId)
            || !GitHubJson.TryGetString(account, "login", MaxLoginLength, out var login)
            || !GitHubJson.TryGetString(account, "type", MaxAccountTypeLength, out var type))
        {
            return null;
        }

        return new GitHubUserInstallation(id, login, accountId, type);
    }

    private static bool TryBaseUri(string? value, Func<Uri, bool> allowed, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !allowed(parsed))
        {
            return false;
        }

        baseUri = parsed;
        return true;
    }

    private static CancellationTokenSource Deadline(GitHubOptions configuration, CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(GitHubHttpClients.TotalTimeout(configuration));

        return deadline;
    }

    [LoggerMessage(
        EventId = 4710,
        Level = LogLevel.Warning,
        Message = "The GitHub App client id or client secret is absent; the install callback reports github_not_configured.")]
    private partial void LogNotConfigured();

    [LoggerMessage(
        EventId = 4711,
        Level = LogLevel.Warning,
        Message = "GitHub call {Operation} failed with {Status} (code {Code}).")]
    private partial void LogCallFailed(string operation, int status, string code);
}
