using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// Exchanges the App JWT for a short-lived installation access token and caches it in memory until
/// shortly before its expiry — never on disk, never in a log line.
/// </summary>
/// <remarks>
/// One exchange runs per installation at a time: concurrent callers await the same task, so a cold
/// cache under load costs one GitHub call instead of one per caller. The cache is bounded and drops
/// expired tokens first, then the entry closest to expiry, so a host with many installations cannot
/// grow it without limit. A caller's cancellation stops that caller waiting; it does not cancel the
/// shared exchange.
/// </remarks>
internal sealed class GitHubInstallationTokenSource
{
    private readonly IHttpClientFactory _clients;
    private readonly GitHubAppTokenSource _appTokens;
    private readonly IOptions<GitHubOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubInstallationTokenSource> _logger;
    private readonly Dictionary<long, GitHubInstallationToken> _cache = [];
    private readonly Dictionary<long, Task<Result<GitHubInstallationToken>>> _inFlight = [];
    private readonly object _gate = new();

    /// <summary>Creates the source over the no-retry token client.</summary>
    public GitHubInstallationTokenSource(
        IHttpClientFactory clients,
        GitHubAppTokenSource appTokens,
        IOptions<GitHubOptions> options,
        TimeProvider time,
        ILogger<GitHubInstallationTokenSource> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(appTokens);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _appTokens = appTokens;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>The cached token for <paramref name="installationId"/>, or a fresh exchange.</summary>
    /// <param name="installationId">Numeric installation id, already validated by the caller.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, not the shared exchange.</param>
    public async Task<Result<GitHubInstallationToken>> GetAsync(
        long installationId,
        CancellationToken cancellationToken)
    {
        if (TryGetCached(installationId, out var cached))
        {
            return Result.Success(cached);
        }

        TaskCompletionSource<Result<GitHubInstallationToken>>? pending = null;
        Task<Result<GitHubInstallationToken>> exchange;
        lock (_gate)
        {
            if (TryGetCached(installationId, out cached))
            {
                return Result.Success(cached);
            }

            if (!_inFlight.TryGetValue(installationId, out exchange!))
            {
                // The entry is visible before the exchange starts, so a second caller can never begin
                // a second exchange — including when the first one completes without yielding.
                pending = new TaskCompletionSource<Result<GitHubInstallationToken>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                exchange = pending.Task;
                _inFlight[installationId] = exchange;
            }
        }

        if (pending is not null)
        {
            _ = ExchangeAsync(installationId, pending);
        }

        return await exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops a rejected token only if it is still the cached token.</summary>
    /// <param name="installationId">The installation whose token was rejected.</param>
    /// <param name="rejectedToken">The bearer value GitHub actually refused.</param>
    public bool Invalidate(long installationId, string rejectedToken)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(installationId, out var cached)
                && string.Equals(cached.Token, rejectedToken, StringComparison.Ordinal)
                && _cache.Remove(installationId);
        }
    }

    private bool TryGetCached(long installationId, out GitHubInstallationToken token)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(installationId, out token!) && token.ExpiresAt - SafetyMargin() > _time.GetUtcNow())
            {
                return true;
            }

            _cache.Remove(installationId);
            token = null!;
            return false;
        }
    }

    private async Task ExchangeAsync(
        long installationId,
        TaskCompletionSource<Result<GitHubInstallationToken>> completion)
    {
        try
        {
            var result = await ExchangeCoreAsync(installationId).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                lock (_gate)
                {
                    Store(installationId, result.Value);
                }
            }
            else
            {
                GitHubLog.TokenExchangeFailed(_logger, installationId, result.Error!.Code);
            }

            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(installationId);
            }
        }
    }

    private async Task<Result<GitHubInstallationToken>> ExchangeCoreAsync(long installationId)
    {
        var jwt = _appTokens.Create();
        if (!jwt.IsSuccess)
        {
            return Result.Failure<GitHubInstallationToken>(jwt.Error!);
        }

        var baseUri = new Uri(_options.Value.ApiBaseUrl, UriKind.Absolute);
        if (!GitHubEndpoints.IsAllowedApiBase(baseUri))
        {
            return Result.Failure<GitHubInstallationToken>(GitHubResponseReader.InvalidParameters());
        }

        // One deadline for headers and body alike: the client timeout stops at headers once the body is
        // read as a stream, so the exchange carries its own bound over the whole exchange.
        using var deadline = new CancellationTokenSource(GitHubHttpClients.AttemptTimeout(_options.Value));
        var client = _clients.CreateClient(GitHubHttpClients.Token);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"app/installations/{installationId}/access_tokens")));
        request.Content = JsonContent.Create(new { permissions = new { contents = "read", metadata = "read" } });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt.Value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubEndpoints.ApiMediaType));
        request.Headers.Add("X-GitHub-Api-Version", GitHubEndpoints.ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(GitHubEndpoints.UserAgent, "1.0"));

        HttpResponseMessage? response;
        try
        {
            // Headers only, like the read client: the body is read under the same cap instead of buffered.
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
            return Result.Failure<GitHubInstallationToken>(GitHubResponseReader.Unavailable());
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            return Result.Failure<GitHubInstallationToken>(
                GitHubResponseReader.MapStatus(response, _time.GetUtcNow()));
        }

        Result<JsonDocument> document;
        try
        {
            document = await GitHubResponseReader
                .ReadJsonAsync(response, _options.Value.MaxResponseBytes, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            // The exchange has no caller token of its own: a body read cut short is an unavailable GitHub.
            return Result.Failure<GitHubInstallationToken>(GitHubResponseReader.Unavailable());
        }

        if (!document.IsSuccess)
        {
            return Result.Failure<GitHubInstallationToken>(document.Error!);
        }

        using var parsed = document.Value;
        if (!GitHubJson.TryGetString(parsed.RootElement, "token", 4096, out var token)
            || !TryReadExpiry(parsed.RootElement, out var expiresAt))
        {
            return Result.Failure<GitHubInstallationToken>(GitHubResponseReader.Unusable());
        }

        GitHubLog.TokenMinted(_logger, installationId, expiresAt);

        return Result.Success(new GitHubInstallationToken(token, expiresAt));
    }

    private bool TryReadExpiry(JsonElement root, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        return root.TryGetProperty("expires_at", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out expiresAt)
            && expiresAt > _time.GetUtcNow();
    }

    private void Store(long installationId, GitHubInstallationToken token)
    {
        var options = _options.Value;
        _cache[installationId] = token;

        if (_cache.Count <= options.MaxTokenCacheEntries)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var margin = SafetyMargin();
        foreach (var expired in _cache.Where(entry => entry.Value.ExpiresAt - margin <= now).Select(entry => entry.Key).ToList())
        {
            _cache.Remove(expired);
        }

        while (_cache.Count > options.MaxTokenCacheEntries)
        {
            var earliest = _cache.MinBy(entry => entry.Value.ExpiresAt).Key;
            _cache.Remove(earliest);
        }
    }

    private TimeSpan SafetyMargin() =>
        TimeSpan.FromSeconds(_options.Value.InstallationTokenSafetyMarginSeconds);
}
