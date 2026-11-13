using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// Exchanges the App JWT for a short-lived installation access token and caches it in memory until shortly before its
/// expiry — never on disk, never in a log line. One instance serves every feature module.
/// </summary>
/// <remarks>
/// The cache key is <c>(org, installation, feature, permission set, repository set)</c>, so a feature never reuses
/// another feature's broader token. One exchange runs per key at a time: concurrent callers await the same task, so a
/// cold cache under load costs one GitHub call. A caller's cancellation stops that caller waiting; it does not cancel
/// the shared exchange. The cache is bounded and drops expired tokens first, then the entry closest to expiry.
/// </remarks>
public sealed class GitHubInstallationTokenSource : IGitHubInstallationTokens
{
    /// <summary>The meter the mint counter is published on.</summary>
    public const string MeterName = "Evidgate.GitHub";

    /// <summary>Instrument name of the per-result mint counter.</summary>
    public const string MintCounterName = "github_token_mint_total";

    private readonly IHttpClientFactory _clients;
    private readonly GitHubAppTokenSource _appTokens;
    private readonly IGitHubInstallationResolver? _resolver;
    private readonly IOptions<GitHubOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubInstallationTokenSource> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _mints;
    private readonly Dictionary<string, GitHubInstallationToken> _cache = [];
    private readonly Dictionary<string, Task<Result<GitHubInstallationToken>>> _inFlight = [];
    private readonly Lock _gate = new();

    /// <summary>Creates the source over the no-retry token client.</summary>
    /// <param name="meters">The host meter factory the mint counter is created on.</param>
    /// <param name="clients">The client factory; the exchange uses the token client.</param>
    /// <param name="appTokens">The App JWT source.</param>
    /// <param name="options">Bound App options.</param>
    /// <param name="time">The clock of expiry decisions.</param>
    /// <param name="logger">The typed logger.</param>
    /// <param name="resolver">
    /// Resolves an org's active installation id. Absent on a host that only mints for an installation id it already
    /// holds; the org-keyed entry point then fails closed instead of guessing.
    /// </param>
    public GitHubInstallationTokenSource(
        IMeterFactory meters,
        IHttpClientFactory clients,
        GitHubAppTokenSource appTokens,
        IOptions<GitHubOptions> options,
        TimeProvider time,
        ILogger<GitHubInstallationTokenSource> logger,
        IGitHubInstallationResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(meters);
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
        _resolver = resolver;
        _meter = meters.Create(new MeterOptions(MeterName));
        _mints = _meter.CreateCounter<long>(MintCounterName, "count", "GitHub installation tokens minted, by result.");
    }

    /// <inheritdoc />
    public async Task<Result<GitHubInstallationToken>> GetAsync(
        Guid orgId,
        GitHubFeature feature,
        IReadOnlyList<long>? repositoryIds,
        CancellationToken cancellationToken)
    {
        if (_resolver is null)
        {
            return Result.Failure<GitHubInstallationToken>(GitHubAppTokenSource.NotConfigured());
        }

        var installation = await _resolver.ResolveAsync(orgId, cancellationToken).ConfigureAwait(false);
        if (!installation.IsSuccess)
        {
            return Result.Failure<GitHubInstallationToken>(installation.Error!);
        }

        return await GetAsync(installation.Value, feature, repositoryIds, orgId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The cached token for an installation id the caller already validated, without resolving one from the tenant.
    /// </summary>
    /// <remarks>
    /// A caller that proved the installation belongs to the org in its own unit holds the id across a boundary that has
    /// no tenant scope open — the verification call of a repository binding, for instance, which must not keep a
    /// transaction open while it waits on GitHub.
    /// </remarks>
    /// <param name="installationId">Numeric installation id, already validated by the caller.</param>
    /// <param name="feature">The consuming feature; its compiled permission row is the ceiling.</param>
    /// <param name="repositoryIds">Numeric repository ids the token may act on.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, not the shared exchange.</param>
    public Task<Result<GitHubInstallationToken>> GetAsync(
        long installationId,
        GitHubFeature feature,
        IReadOnlyList<long>? repositoryIds,
        CancellationToken cancellationToken) =>
        GetAsync(installationId, feature, repositoryIds, orgId: null, cancellationToken);

    /// <summary>
    /// The cached token for <paramref name="installationId"/> and this feature's key, or a fresh exchange. Callers that
    /// already hold a validated installation id use this overload; <paramref name="orgId"/> only widens the cache key
    /// for the org-keyed entry point.
    /// </summary>
    /// <param name="installationId">Numeric installation id, already validated by the caller.</param>
    /// <param name="feature">The consuming feature; its compiled permission row is the ceiling.</param>
    /// <param name="repositoryIds">Numeric repository ids from the stored binding; mandatory for a scoped feature.</param>
    /// <param name="orgId">The tenant, when known.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, not the shared exchange.</param>
    internal async Task<Result<GitHubInstallationToken>> GetAsync(
        long installationId,
        GitHubFeature feature,
        IReadOnlyList<long>? repositoryIds,
        Guid? orgId,
        CancellationToken cancellationToken)
    {
        if (installationId <= 0)
        {
            return Result.Failure<GitHubInstallationToken>(GitHubResponseReader.InvalidParameters());
        }

        GitHubPermissionScope scope;
        try
        {
            scope = GitHubFeaturePermissions.For(feature);
            ValidateRepositories(feature, scope, repositoryIds);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Result.Failure<GitHubInstallationToken>(
                new(GitHubErrorCodes.PermissionMapEmpty, exception.Message, 500));
        }

        var key = CacheKey(orgId, installationId, feature, scope, repositoryIds);

        if (TryGetCached(key, out var cached))
        {
            return Result.Success(cached);
        }

        TaskCompletionSource<Result<GitHubInstallationToken>>? pending = null;
        Task<Result<GitHubInstallationToken>> exchange;
        lock (_gate)
        {
            if (TryGetCached(key, out cached))
            {
                return Result.Success(cached);
            }

            if (!_inFlight.TryGetValue(key, out exchange!))
            {
                // The entry is visible before the exchange starts, so a second caller can never begin a second
                // exchange — including when the first one completes without yielding.
                pending = new TaskCompletionSource<Result<GitHubInstallationToken>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                exchange = pending.Task;
                _inFlight[key] = exchange;
            }
        }

        if (pending is not null)
        {
            _ = ExchangeAsync(key, installationId, feature, scope, repositoryIds, pending);
        }

        return await exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops the cached token whose bearer GitHub refused. The value is unique per mint, so no
    /// installation id is needed to find it.</summary>
    /// <param name="rejectedToken">The bearer value GitHub actually refused.</param>
    public bool Invalidate(string rejectedToken)
    {
        ArgumentNullException.ThrowIfNull(rejectedToken);

        lock (_gate)
        {
            var keys = _cache
                .Where(entry => string.Equals(entry.Value.Token, rejectedToken, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .ToList();

            foreach (var key in keys)
            {
                _cache.Remove(key);
            }

            return keys.Count > 0;
        }
    }

    /// <summary>Drops every cached token of an installation, e.g. after a suspend, delete or permission change.</summary>
    /// <param name="installationId">The installation to flush.</param>
    public void InvalidateAll(long installationId)
    {
        var marker = string.Create(CultureInfo.InvariantCulture, $"|{installationId}|");

        lock (_gate)
        {
            foreach (var key in _cache.Keys.Where(key => key.Contains(marker, StringComparison.Ordinal)).ToList())
            {
                _cache.Remove(key);
            }
        }
    }

    private static void ValidateRepositories(
        GitHubFeature feature,
        GitHubPermissionScope scope,
        IReadOnlyList<long>? repositoryIds)
    {
        if (!scope.RepositoryScoped)
        {
            return;
        }

        if (repositoryIds is null || repositoryIds.Count == 0)
        {
            throw new ArgumentException(
                $"GitHub feature '{feature}' is repository-scoped; pass the stored repository ids so the token cannot reach every repository.",
                nameof(repositoryIds));
        }

        if (repositoryIds.Any(id => id <= 0))
        {
            throw new ArgumentException("GitHub repository ids must be positive.", nameof(repositoryIds));
        }
    }

    private static string CacheKey(
        Guid? orgId,
        long installationId,
        GitHubFeature feature,
        GitHubPermissionScope scope,
        IReadOnlyList<long>? repositoryIds)
    {
        var org = orgId?.ToString("N", CultureInfo.InvariantCulture) ?? "-";
        var repositories = repositoryIds is null
            ? "-"
            : string.Join(',', repositoryIds.Distinct().Order());

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{org}|{installationId}|{feature}|{scope.CacheKey}|{repositories}");
    }

    private bool TryGetCached(string key, out GitHubInstallationToken token)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out token!) && token.ExpiresAt - SafetyMargin() > _time.GetUtcNow())
            {
                return true;
            }

            _cache.Remove(key);
            token = null!;
            return false;
        }
    }

    private async Task ExchangeAsync(
        string key,
        long installationId,
        GitHubFeature feature,
        GitHubPermissionScope scope,
        IReadOnlyList<long>? repositoryIds,
        TaskCompletionSource<Result<GitHubInstallationToken>> completion)
    {
        try
        {
            var result = await ExchangeCoreAsync(installationId, feature, scope, repositoryIds).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                lock (_gate)
                {
                    Store(key, result.Value);
                }
            }
            else
            {
                GitHubLog.TokenExchangeFailed(_logger, installationId, feature.ToString(), result.Error!.Code);
            }

            _mints.Add(1, new KeyValuePair<string, object?>("result", result.IsSuccess ? "ok" : "failed"));
            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            _mints.Add(1, new KeyValuePair<string, object?>("result", "failed"));
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(key);
            }
        }
    }

    private async Task<Result<GitHubInstallationToken>> ExchangeCoreAsync(
        long installationId,
        GitHubFeature feature,
        GitHubPermissionScope scope,
        IReadOnlyList<long>? repositoryIds)
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

        // One deadline for headers and body alike: the client timeout stops at headers once the body is read as a
        // stream, so the exchange carries its own bound over the whole exchange.
        using var deadline = new CancellationTokenSource(GitHubHttpClients.AttemptTimeout(_options.Value));
        var client = _clients.CreateClient(GitHubHttpClients.Token);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"app/installations/{installationId}/access_tokens")));

        // Both members are always present for a repository-scoped feature: omitting permissions grants every
        // permission, and omitting repository_ids reaches every repository of the installation.
        request.Content = JsonContent.Create(new AccessTokenRequest(scope.Permissions, repositoryIds));
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

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var featureLabel = feature.ToString();
            GitHubLog.TokenMinted(_logger, installationId, featureLabel, expiresAt);
        }

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

    private void Store(string key, GitHubInstallationToken token)
    {
        var options = _options.Value;
        _cache[key] = token;

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

    private sealed record AccessTokenRequest(
        [property: JsonPropertyName("permissions")] IReadOnlyDictionary<string, string> Permissions,
        [property: JsonPropertyName("repository_ids")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<long>? RepositoryIds);
}
