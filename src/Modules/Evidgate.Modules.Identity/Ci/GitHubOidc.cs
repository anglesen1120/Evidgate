using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Serialization;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>The claims of a verified GitHub Actions OIDC token that the exchange uses (02.4.1 AC6/T88).</summary>
/// <param name="Jti">Single-use token id (replay arbiter).</param>
/// <param name="Subject">The <c>sub</c> claim, in either format (F3); informational, never a binding key.</param>
/// <param name="Run">Repository, ref, sha, run and workflow claims.</param>
internal sealed record GitHubOidcClaims(string Jti, string Subject, CiRunClaims Run);

/// <summary>Why an OIDC token was refused; the endpoint maps them to <c>token_invalid</c> (401) or 503.</summary>
internal enum OidcFailure
{
    /// <summary>Signature, issuer, audience, lifetime, algorithm or a required claim failed.</summary>
    Invalid,

    /// <summary>The signing keys could not be fetched; retryable.</summary>
    KeysUnavailable,
}

/// <summary>Outcome of validating a GitHub OIDC token.</summary>
internal readonly record struct OidcResult(GitHubOidcClaims? Claims, OidcFailure? Failure)
{
    public static OidcResult Ok(GitHubOidcClaims claims) => new(claims, null);

    public static OidcResult Fail(OidcFailure failure) => new(null, failure);
}

/// <summary>Source of GitHub's signing keys. Production fetches the JWKS over HTTPS; tests serve a local keypair.</summary>
internal interface IGitHubJwksProvider
{
    /// <summary>
    /// The key with the given <c>kid</c>, or null when GitHub does not publish it. An unknown
    /// <c>kid</c> triggers at most one refetch of the document (and never more often than the minimum
    /// refresh interval), so a flood of random kids cannot turn this into an amplifier.
    /// </summary>
    /// <param name="keyId">The token header's <c>kid</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="HttpRequestException">The document could not be fetched.</exception>
    ValueTask<SecurityKey?> GetKeyAsync(string keyId, CancellationToken cancellationToken);
}

/// <summary>
/// Validates a GitHub Actions OIDC JWT (02.4.1-T1): RS256 only (an <c>alg</c> of <c>none</c> or HS256
/// is never accepted, which closes algorithm confusion), the configured issuer and audience, lifetime
/// with the configured skew, and the claims the binding match needs.
/// </summary>
internal sealed class GitHubOidcValidator(IOptions<CiAuthOptions> options, IGitHubJwksProvider keys, TimeProvider clock)
{
    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly CiAuthOptions _options = options.Value;

    private readonly HashSet<string> _allowedEvents = new(CiAuthOptionsValidator.SplitEventNames(options.Value.AllowedEventNames), StringComparer.Ordinal);

    /// <summary>Validates <paramref name="token"/>.</summary>
    /// <param name="token">The compact JWT from the <c>Authorization: Bearer</c> header.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<OidcResult> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 8192)
        {
            return OidcResult.Fail(OidcFailure.Invalid);
        }

        JsonWebToken unverified;
        try
        {
            unverified = new JsonWebToken(token);
        }
        catch (ArgumentException)
        {
            return OidcResult.Fail(OidcFailure.Invalid);
        }

        // The header is untrusted: it only selects which published key to try. The algorithm is pinned below.
        if (string.IsNullOrEmpty(unverified.Kid) || unverified.Alg != SecurityAlgorithms.RsaSha256)
        {
            return OidcResult.Fail(OidcFailure.Invalid);
        }

        SecurityKey? key;
        try
        {
            key = await keys.GetKeyAsync(unverified.Kid, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return OidcResult.Fail(OidcFailure.KeysUnavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OidcResult.Fail(OidcFailure.KeysUnavailable);
        }

        if (key is null)
        {
            return OidcResult.Fail(OidcFailure.Invalid);
        }

        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _options.GithubOidcIssuer,
            ValidAudience = _options.GithubOidcAudience,
            IssuerSigningKey = key,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(_options.MaxSkewSeconds),
            LifetimeValidator = (notBefore, expires, _, parameters) =>
                LifetimeCheck.IsWithin(clock.GetUtcNow(), notBefore, expires, parameters.ClockSkew),
        }).ConfigureAwait(false);

        if (!result.IsValid)
        {
            return OidcResult.Fail(OidcFailure.Invalid);
        }

        if (result.SecurityToken is not JsonWebToken jwt)
        {
            return OidcResult.Fail(OidcFailure.Invalid);
        }

        // Fail closed: event_name must be one JSON string that is exactly (ordinal, case-sensitive) on the allowlist.
        if (!IsAllowedEvent(jwt))
        {
            return OidcResult.Fail(OidcFailure.Invalid);
        }

        return Extract(jwt) is { } claims ? OidcResult.Ok(claims) : OidcResult.Fail(OidcFailure.Invalid);
    }

    private bool IsAllowedEvent(JsonWebToken jwt)
    {
        // The claim set flattens a one-element array to a string and keeps only the last of duplicate properties, so the
        // signed payload itself is inspected: exactly one event_name property whose JSON value is a string.
        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
            string? eventName = null;
            var count = 0;

            foreach (var property in payload.RootElement.EnumerateObject().Where(p => p.NameEquals("event_name")))
            {
                count++;
                eventName = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }

            return count == 1 && eventName is not null && _allowedEvents.Contains(eventName);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static GitHubOidcClaims? Extract(JsonWebToken jwt)
    {
        if (!Text(jwt, "jti", out var jti)
            || !Text(jwt, "sub", out var subject)
            || !Text(jwt, "repository", out var repository)
            || !Text(jwt, "ref", out var gitRef)
            || !Text(jwt, "sha", out var sha)
            || !Text(jwt, "job_workflow_ref", out var workflowRef)
            || !Number(jwt, "repository_id", out var repositoryId)
            || !Number(jwt, "run_id", out var runId)
            || !Number(jwt, "run_attempt", out var runAttempt)
            || repositoryId <= 0
            || runAttempt is < 1 or > int.MaxValue)
        {
            return null;
        }

        return new GitHubOidcClaims(
            jti,
            subject,
            new CiRunClaims(repositoryId, repository, gitRef, sha, runId, (int)runAttempt, workflowRef));
    }

    private static bool Text(JsonWebToken jwt, string name, out string value)
    {
        value = string.Empty;

        if (!jwt.TryGetPayloadValue<string>(name, out var found) || string.IsNullOrWhiteSpace(found) || found.Length > (name == "jti" ? 256 : 512))
        {
            return false;
        }

        value = found;

        return true;
    }

    /// <summary>GitHub sends ids as JSON strings (<c>"repository_id": "123"</c>); a number is accepted too.</summary>
    private static bool Number(JsonWebToken jwt, string name, out long value)
    {
        value = 0;

        if (jwt.TryGetPayloadValue<string>(name, out var text))
        {
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        if (jwt.TryGetPayloadValue<JsonElement>(name, out var element) && element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        return false;
    }
}

/// <summary>
/// Fetches and caches GitHub's JWKS (02.4.1-T1.2). The URI is checked against the issuer's host at boot
/// (S4), the fetch has a hard timeout, concurrent callers share one fetch (single flight), and an
/// unknown <c>kid</c> forces a refresh at most once per <see cref="MinRefreshInterval"/>.
/// </summary>
internal sealed class HttpGitHubJwksProvider(
    HttpClient http,
    IOptions<CiAuthOptions> options,
    TimeProvider clock) : IGitHubJwksProvider, IDisposable
{
    /// <summary>How long a fetched document is trusted without a refresh attempt.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>The soonest an unknown kid may force another fetch.</summary>
    internal static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long expired keys keep serving while GitHub is unreachable (keys rotate rarely).</summary>
    internal static readonly TimeSpan StaleGrace = TimeSpan.FromHours(24);

    /// <summary>After a failed fetch, callers fail fast for this long instead of queueing behind the timeout.</summary>
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // One immutable snapshot published through a volatile reference: readers never see a torn pair.
    private volatile Snapshot _snapshot = Snapshot.Empty;

    /// <inheritdoc />
    public async ValueTask<SecurityKey?> GetKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var snap = _snapshot;
        if (now - snap.FetchedAt < CacheTtl && snap.Keys.TryGetValue(keyId, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = clock.GetUtcNow();
            snap = _snapshot;
            var fresh = now - snap.FetchedAt < CacheTtl;

            if (fresh && snap.Keys.TryGetValue(keyId, out var winner))
            {
                return winner;
            }

            // A recent failure: do not queue every caller behind another timeout; serve what we still trust.
            var backingOff = now - snap.LastFailureAt < FailureBackoff;
            var throttled = fresh && now - snap.FetchedAt < MinRefreshInterval;
            if (!backingOff && !throttled)
            {
                try
                {
                    snap = await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    snap = snap with { LastFailureAt = clock.GetUtcNow() };
                    _snapshot = snap;
                    if (now - snap.FetchedAt >= StaleGrace)
                    {
                        throw;
                    }
                }
            }

            if (now - snap.FetchedAt >= StaleGrace)
            {
                throw new HttpRequestException("no usable JWKS document (the fetch is backing off)");
            }

            // Within the grace window an expired document still verifies signatures (GitHub keys rotate rarely).
            return now - snap.FetchedAt < StaleGrace && snap.Keys.TryGetValue(keyId, out var refreshed) ? refreshed : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<Snapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        using var response = await http.GetAsync(new Uri(options.Value.GithubJwksUri), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await BoundedHttpBody.ReadTextAsync(response.Content, MaxDocumentBytes, timeout.Token).ConfigureAwait(false)
                   ?? throw new HttpRequestException("the JWKS document is unreasonably large");
        var set = new JsonWebKeySet(json);
        var keys = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        foreach (var key in set.GetSigningKeys())
        {
            if (!string.IsNullOrEmpty(key.KeyId))
            {
                keys[key.KeyId] = key;
            }
        }

        var snapshot = new Snapshot(keys, clock.GetUtcNow(), DateTimeOffset.MinValue);
        _snapshot = snapshot;

        return snapshot;
    }

    private const int MaxDocumentBytes = 256 * 1024;

    private sealed record Snapshot(Dictionary<string, SecurityKey> Keys, DateTimeOffset FetchedAt, DateTimeOffset LastFailureAt)
    {
        public static readonly Snapshot Empty = new(new Dictionary<string, SecurityKey>(StringComparer.Ordinal), DateTimeOffset.MinValue, DateTimeOffset.MinValue);
    }
}
