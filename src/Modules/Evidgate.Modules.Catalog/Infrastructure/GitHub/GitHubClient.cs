using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// The typed GitHub App client: the installation lookup, repository lookup, git trees and git
/// blobs, authenticated with the App JWT or an installation token and always against the pinned
/// API host. Every call returns a typed <see cref="Result{T}"/>; an outage or a rate limit is a
/// value, not an exception.
/// </summary>
/// <remarks>
/// No URL is ever built from caller text: the base address comes from validated options and each
/// path segment is a numeric id or a validated git object id, so a repository or blob name cannot
/// move a request to another host. Redirects are disabled on the primary handler, and a body is
/// refused unread when it exceeds the configured cap.
/// </remarks>
internal sealed partial class GitHubClient
{
    private readonly HttpClient _api;
    private readonly GitHubInstallationTokenSource _tokens;
    private readonly GitHubAppTokenSource _appTokens;
    private readonly IOptions<GitHubOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubClient> _logger;

    /// <summary>Creates the client over the resilient API client and the token sources.</summary>
    public GitHubClient(
        HttpClient api,
        GitHubInstallationTokenSource tokens,
        GitHubAppTokenSource appTokens,
        IOptions<GitHubOptions> options,
        TimeProvider time,
        ILogger<GitHubClient> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(appTokens);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _tokens = tokens;
        _appTokens = appTokens;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary><c>GET /app/installations/{id}</c> with the App JWT.</summary>
    /// <param name="installationId">Numeric installation id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<GitHubInstallation>> GetInstallationAsync(
        long installationId,
        CancellationToken cancellationToken)
    {
        if (installationId <= 0)
        {
            return Result.Failure<GitHubInstallation>(InvalidParameters());
        }

        var jwt = await _appTokens.CreateAsync(cancellationToken).ConfigureAwait(false);
        if (!jwt.IsSuccess)
        {
            return Result.Failure<GitHubInstallation>(jwt.Error!);
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"app/installations/{installationId}");
        var document = await SendAsync(path, jwt.Value, "get-installation", cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return Result.Failure<GitHubInstallation>(document.Error!);
        }

        using var parsed = document.Value;
        return ParseInstallation(parsed.RootElement, installationId);
    }

    /// <summary><c>GET /repositories/{id}</c> with an installation token.</summary>
    /// <param name="installationId">Installation the token is minted for.</param>
    /// <param name="repositoryId">Numeric repository id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<GitHubRepository>> GetRepositoryAsync(
        long installationId,
        long repositoryId,
        CancellationToken cancellationToken)
    {
        if (installationId <= 0 || repositoryId <= 0)
        {
            return Result.Failure<GitHubRepository>(InvalidParameters());
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"repositories/{repositoryId}");
        var document = await SendWithInstallationTokenAsync(installationId, path, "get-repository", cancellationToken)
            .ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return Result.Failure<GitHubRepository>(document.Error!);
        }

        using var parsed = document.Value;
        return ParseRepository(parsed.RootElement, repositoryId);
    }

    /// <summary>
    /// <c>GET /repositories/{id}/git/trees/{sha}?recursive=1</c> with an installation token. The
    /// <c>truncated</c> flag is surfaced unchanged so the caller can refuse an incomplete tree.
    /// </summary>
    /// <param name="installationId">Installation the token is minted for.</param>
    /// <param name="repositoryId">Numeric repository id.</param>
    /// <param name="treeSha">Git object id of the tree; validated as lowercase hex.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<GitHubTree>> GetTreeAsync(
        long installationId,
        long repositoryId,
        string treeSha,
        CancellationToken cancellationToken)
    {
        if (installationId <= 0 || repositoryId <= 0 || !IsObjectId(treeSha))
        {
            return Result.Failure<GitHubTree>(InvalidParameters());
        }

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"repositories/{repositoryId}/git/trees/{treeSha}?recursive=1");
        var document = await SendWithInstallationTokenAsync(installationId, path, "get-tree", cancellationToken)
            .ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return Result.Failure<GitHubTree>(document.Error!);
        }

        using var parsed = document.Value;
        return ParseTree(parsed.RootElement);
    }

    /// <summary><c>GET /repositories/{id}/git/blobs/{sha}</c> with an installation token, decoded and capped.</summary>
    /// <param name="installationId">Installation the token is minted for.</param>
    /// <param name="repositoryId">Numeric repository id.</param>
    /// <param name="blobSha">Git object id of the blob; validated as lowercase hex.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Result<GitHubBlob>> GetBlobAsync(
        long installationId,
        long repositoryId,
        string blobSha,
        CancellationToken cancellationToken)
    {
        if (installationId <= 0 || repositoryId <= 0 || !IsObjectId(blobSha))
        {
            return Result.Failure<GitHubBlob>(InvalidParameters());
        }

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"repositories/{repositoryId}/git/blobs/{blobSha}");
        var document = await SendWithInstallationTokenAsync(installationId, path, "get-blob", cancellationToken)
            .ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return Result.Failure<GitHubBlob>(document.Error!);
        }

        using var parsed = document.Value;
        return ParseBlob(parsed.RootElement, blobSha);
    }

    private async Task<Result<JsonDocument>> SendWithInstallationTokenAsync(
        long installationId,
        string path,
        string operation,
        CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAsync(installationId, cancellationToken).ConfigureAwait(false);
        if (!token.IsSuccess)
        {
            return Result.Failure<JsonDocument>(token.Error!);
        }

        var first = await SendAsync(path, token.Value.Token, operation, cancellationToken).ConfigureAwait(false);
        if (first.Error?.Code != GitHubErrorCodes.Unauthorized)
        {
            return first;
        }

        _tokens.Invalidate(installationId);
        GitHubLog.TokenEvicted(_logger, installationId);

        var refreshed = await _tokens.GetAsync(installationId, cancellationToken).ConfigureAwait(false);
        return refreshed.IsSuccess
            ? await SendAsync(path, refreshed.Value.Token, operation, cancellationToken).ConfigureAwait(false)
            : Result.Failure<JsonDocument>(refreshed.Error!);
    }

    private async Task<Result<JsonDocument>> SendAsync(
        string relativePath,
        string bearer,
        string operation,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(_options.Value.ApiBaseUrl, UriKind.Absolute);
        if (!GitHubEndpoints.IsAllowedApiBase(baseUri))
        {
            return Result.Failure<JsonDocument>(InvalidParameters());
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubEndpoints.ApiMediaType));
        request.Headers.Add("X-GitHub-Api-Version", GitHubEndpoints.ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(GitHubEndpoints.UserAgent, "1.0"));

        // One deadline for headers and body. The handler pipeline's own timeouts stop when the headers
        // arrive under ResponseHeadersRead, so the call carries its own bound over the streamed body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(GitHubHttpClients.TotalTimeout(_options.Value));

        using var response = await SendCoreAsync(request, deadline.Token).ConfigureAwait(false);
        if (response is null)
        {
            return Result.Failure<JsonDocument>(GitHubResponseReader.Unavailable());
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = GitHubResponseReader.MapStatus(response, _time.GetUtcNow());
            GitHubLog.CallFailed(_logger, operation, (int)response.StatusCode, error.Code);

            return Result.Failure<JsonDocument>(error);
        }

        try
        {
            return await GitHubResponseReader
                .ReadJsonAsync(response, _options.Value.MaxResponseBytes, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The pipeline deadline aborts a body read in flight the same way a dead connection does.
            return Result.Failure<JsonDocument>(GitHubResponseReader.Unavailable());
        }
    }

    private async Task<HttpResponseMessage?> SendCoreAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Headers only: the reader enforces the body cap while streaming, so an oversized or endless
            // body is refused without first being buffered into memory.
            return await _api
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (ExecutionRejectedException)
        {
            // A pipeline timeout rejects the call instead of throwing TimeoutException.
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsObjectId(string? value) =>
        value is not null && AsciiShape.IsMatch(value, 40, 64, AsciiShape.LowerHex, AsciiShape.LowerHex);

    private static Error InvalidParameters() => GitHubResponseReader.InvalidParameters();

    private static bool IsSafeRepositoryPath(string path)
    {
        if (path.Length is 0 or > 1024 || path[0] == '/' || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or ".." || segment.Any(char.IsControl))
            {
                return false;
            }
        }

        return true;
    }
}
