using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Evidgate.Modules.Identity.Login.Google;

/// <summary>Source of Google's ID-token signing keys. Production fetches the JWKS over https; a test serves a local keypair.</summary>
internal interface IGoogleJwksProvider
{
    /// <summary>
    /// The key with the given <c>kid</c>, or null when Google does not publish it. An unknown <c>kid</c>
    /// triggers at most one refetch of the document, and never more often than the configured minimum
    /// refresh interval, so a flood of random kids cannot turn this into an amplifier.
    /// </summary>
    /// <param name="keyId">The token header's <c>kid</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="HttpRequestException">The document could not be fetched and no earlier key set still answers for <paramref name="keyId"/>.</exception>
    ValueTask<SecurityKey?> GetKeyAsync(string keyId, CancellationToken cancellationToken);
}

/// <summary>
/// The metric names of Google login (02.3.1 §11 monitoring), in one place so the producer and a test
/// cannot drift. A label carries a closed category only: never a <c>kid</c>, key material, token, email
/// or subject.
/// </summary>
/// <remarks>
/// <see cref="MeterName"/> is <c>EvidgateTelemetry.MeterName</c>: the module already references the
/// Observability building block, so the meter is the application's one meter and the OTel pipeline
/// picks these instruments up with it.
/// </remarks>
internal static class GoogleLoginMetrics
{
    /// <summary>The application meter the OTel pipeline subscribes to.</summary>
    internal const string MeterName = EvidgateTelemetry.MeterName;

    /// <summary>Counter of JWKS document fetches, tagged by provider and outcome.</summary>
    internal const string JwksRefreshCounterName = "evidgate_oidc_jwks_refresh_total";

    /// <summary>Tag naming the login provider (closed set: <c>google</c>).</summary>
    internal const string ProviderTag = "evidgate.oidc.provider";

    /// <summary>Tag carrying the refresh outcome (closed set: <c>succeeded</c> / <c>failed</c>).</summary>
    internal const string OutcomeTag = "evidgate.oidc.jwks.outcome";

    /// <summary>The only provider value this module publishes.</summary>
    internal const string Google = "google";

    /// <summary>Outcome value of a completed fetch.</summary>
    internal const string Succeeded = "succeeded";

    /// <summary>Outcome value of a failed fetch.</summary>
    internal const string Failed = "failed";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> JwksRefreshes = Meter.CreateCounter<long>(
        JwksRefreshCounterName,
        unit: "{refresh}",
        description: "Google OIDC JWKS document fetches, by provider and outcome. Never labelled with a kid, key, token or subject.");

    /// <summary>Records one JWKS fetch attempt.</summary>
    /// <param name="succeeded">Whether the document was fetched and parsed.</param>
    internal static void JwksRefreshed(bool succeeded)
    {
        var tags = new TagList
        {
            { ProviderTag, Google },
            { OutcomeTag, succeeded ? Succeeded : Failed },
        };

        JwksRefreshes.Add(1, tags);
    }
}

/// <summary>
/// Fetches and caches Google's ID-token signing keys (02.3.1-T1.2): a TTL cache with single-flight
/// refresh (concurrent callers share one fetch), a forced refresh on an unknown <c>kid</c> bounded by the
/// minimum refresh interval, <see cref="TimeProvider"/>-driven expiry, a bounded response size and key
/// count, and RSA keys only. The class talks to the provider and nothing else, so it can never run
/// inside a database transaction (P5).
/// </summary>
/// <remarks>
/// A fetch failure surfaces as <see cref="HttpRequestException"/> (the validator maps it to
/// <c>unavailable</c>/503) unless an earlier key set still answers for the requested <c>kid</c> within
/// <see cref="StaleGrace"/>: Google rotates keys rarely, and refusing a token whose key we already hold
/// would turn a JWKS outage into a login outage.
/// </remarks>
internal sealed partial class HttpGoogleJwksCache : IGoogleJwksProvider, IDisposable
{
    /// <summary>Largest <c>kid</c> the cache looks up; a longer one is answered with null without a fetch.</summary>
    internal const int MaxKeyIdLength = 128;

    /// <summary>Largest number of keys a JWKS document may publish; a longer one is refused.</summary>
    internal const int MaxDocumentKeys = 64;

    /// <summary>How long a key set we fetched before an outage keeps answering.</summary>
    internal static readonly TimeSpan StaleGrace = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly GoogleLoginOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<HttpGoogleJwksCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // One immutable snapshot behind a volatile reference: a reader never sees a torn pair.
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public HttpGoogleJwksCache(
        HttpClient http,
        IOptions<GoogleLoginOptions> options,
        TimeProvider clock,
        ILogger<HttpGoogleJwksCache> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<SecurityKey?> GetKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(keyId) || keyId.Length > MaxKeyIdLength)
        {
            return null;
        }

        var ttl = TimeSpan.FromSeconds(_options.JwksTtlSeconds);
        var minRefresh = TimeSpan.FromSeconds(_options.JwksMinRefreshSeconds);

        var now = _clock.GetUtcNow();
        var snapshot = _snapshot;
        if (now - snapshot.FetchedAt < ttl && snapshot.Keys.TryGetValue(keyId, out var cached))
        {
            return cached;
        }

        // Single flight: the first caller fetches, every other caller waits here and then reads the
        // snapshot it published. Because a fetch stamps LastAttemptAt, a second unknown kid inside the
        // minimum refresh window never starts another one — it fails closed instead.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = _clock.GetUtcNow();
            snapshot = _snapshot;
            if (now - snapshot.FetchedAt < ttl && snapshot.Keys.TryGetValue(keyId, out var winner))
            {
                return winner;
            }

            if (now - snapshot.LastAttemptAt >= minRefresh)
            {
                try
                {
                    snapshot = await FetchAsync(cancellationToken);
                    _snapshot = snapshot;
                    GoogleLoginMetrics.JwksRefreshed(succeeded: true);

                    return snapshot.Keys.TryGetValue(keyId, out var fetched) ? fetched : null;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or ArgumentException or FormatException
                                               || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    _snapshot = snapshot with { LastAttemptAt = _clock.GetUtcNow(), LastFailureAt = _clock.GetUtcNow() };
                    GoogleLoginMetrics.JwksRefreshed(succeeded: false);
                    LogRefreshFailed();

                    now = _clock.GetUtcNow();
                    if (snapshot.Keys.TryGetValue(keyId, out var held) && now - snapshot.FetchedAt < StaleGrace)
                    {
                        return held;
                    }

                    throw;
                }
            }

            now = _clock.GetUtcNow();
            if (snapshot.Keys.TryGetValue(keyId, out var live) && now - snapshot.FetchedAt < StaleGrace)
            {
                return live;
            }

            // Throttled after a failure and holding nothing for this kid: the provider is unavailable,
            // not the token invalid, so the caller answers 503 instead of steering the user to a retry
            // that cannot work.
            if (now - snapshot.LastFailureAt < minRefresh)
            {
                throw new HttpRequestException("no usable Google OIDC key set (the JWKS fetch is backing off)");
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();

        // The cache owns the client it was handed (the factory creates one per consumer): release it too so
        // no analyser has to guess which of the two fields is disposed.
        _http.Dispose();
    }

    private async Task<Snapshot> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A backstop above the client's own budget (per attempt plus one retry), never below it: a shorter
        // bound here would abort the pipeline before its per-attempt timeout and retry could run.
        timeout.CancelAfter(GoogleLoginHttpClients.JwksTotalTimeout(_options) + TimeSpan.FromSeconds(1));

        using var response = await _http
            .GetAsync(new Uri(_options.JwksUri, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("the Google OIDC JWKS document could not be fetched");
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > _options.MaxResponseBytes)
        {
            throw new HttpRequestException("the Google OIDC JWKS document is unreasonably large");
        }

        var json = await ReadCappedAsync(response.Content, _options.MaxResponseBytes, timeout.Token).ConfigureAwait(false)
                   ?? throw new HttpRequestException("the Google OIDC JWKS document is unreasonably large");

        var keys = Parse(json);
        var at = _clock.GetUtcNow();

        return new Snapshot(keys, at, at, DateTimeOffset.MinValue);
    }

    /// <summary>Keys of a JWKS document, RSA only and keyed by <c>kid</c>; anything else in the document is ignored.</summary>
    private static Dictionary<string, SecurityKey> Parse(string json)
    {
        var set = new JsonWebKeySet(json);
        if (set.Keys.Count > MaxDocumentKeys)
        {
            throw new HttpRequestException("the Google OIDC JWKS document publishes too many keys");
        }

        var keys = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        foreach (var key in set.GetSigningKeys())
        {
            // Only RSA keys verify an RS256 ID token; an EC or symmetric entry in the document is never a candidate.
            if (key is RsaSecurityKey && !string.IsNullOrEmpty(key.KeyId) && key.KeyId.Length <= MaxKeyIdLength)
            {
                keys[key.KeyId] = key;
            }
        }

        return keys;
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; a longer body is refused rather than buffered whole.</summary>
    private static async Task<string?> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [LoggerMessage(
        EventId = 2430,
        Level = LogLevel.Warning,
        Message = "Google OIDC JWKS fetch failed; a held key set still answers within the stale grace, otherwise the caller answers unavailable")]
    private partial void LogRefreshFailed();

    private sealed record Snapshot(Dictionary<string, SecurityKey> Keys, DateTimeOffset FetchedAt, DateTimeOffset LastAttemptAt, DateTimeOffset LastFailureAt)
    {
        public static readonly Snapshot Empty = new(new Dictionary<string, SecurityKey>(StringComparer.Ordinal), DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
    }
}
