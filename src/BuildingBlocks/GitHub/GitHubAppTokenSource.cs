using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// Mints the RS256 App JWT that authenticates the App itself (installation lookup and the token exchange). The key is
/// resolved into startup-bound options through the secret provider; the PEM and the JWT never leave this type except
/// as the returned token, and no exception message repeats either.
/// </summary>
/// <remarks>
/// The JWT is minted per call with a ten-minute lifetime from <c>iat</c>, backdated for clock skew. Rotating the key
/// requires a host restart. Callers outside this type never receive the App JWT: features mint installation tokens.
/// </remarks>
public sealed class GitHubAppTokenSource : IDisposable
{
    /// <summary>GitHub accepts an App JWT for at most ten minutes.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly IOptions<GitHubOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GitHubAppTokenSource> _logger;
    private readonly Lock _keyGate = new();
    private RSA? _key;
    private string? _keyPem;
    private string? _reportedKeyPem;

    /// <summary>Creates the source over the configured App id and the resolved private key.</summary>
    public GitHubAppTokenSource(
        IOptions<GitHubOptions> options,
        TimeProvider time,
        ILogger<GitHubAppTokenSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Returns a fresh App JWT, or <c>github_not_configured</c> when the App id or the key is absent or unusable.
    /// Nothing about the key's value reaches the error.
    /// </summary>
    public Result<string> Create()
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            return Result.Failure<string>(NotConfigured());
        }

        lock (_keyGate)
        {
            try
            {
                var key = LoadKey(options);
                if (key is null)
                {
                    return Result.Failure<string>(NotConfigured());
                }

                var issuedAt = _time.GetUtcNow() - TimeSpan.FromSeconds(options.AppJwtBackdateSeconds);
                var descriptor = new SecurityTokenDescriptor
                {
                    Issuer = options.AppId,
                    IssuedAt = issuedAt.UtcDateTime,
                    Expires = (issuedAt + Lifetime).UtcDateTime,
                    SigningCredentials = new SigningCredentials(KeyFor(key), SecurityAlgorithms.RsaSha256),
                };

                return Result.Success(Handler.CreateToken(descriptor));
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException or InvalidOperationException)
            {
                ReportSigningFailure(options.PrivateKey, exception);
                return Result.Failure<string>(NotConfigured());
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_keyGate)
        {
            _key?.Dispose();
            _key = null;
            _keyPem = null;
        }
    }

    /// <summary>The one "no App is configured" failure, used for a missing id and a missing key alike.</summary>
    public static Error NotConfigured() =>
        new(GitHubErrorCodes.NotConfigured, "No GitHub App is configured on this host.", 503);

    /// <summary>
    /// The signing key for one RSA instance. IdentityModel caches signature providers per key material and hands a
    /// later call a provider bound to a disposed RSA, so this key never caches: the host that disposed the old key
    /// must be able to sign again with the same PEM.
    /// </summary>
    private static RsaSecurityKey KeyFor(RSA key) => new(key)
    {
        CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
    };

    private void ReportSigningFailure(string? pem, Exception exception)
    {
        if (string.Equals(_reportedKeyPem, pem, StringComparison.Ordinal))
        {
            return;
        }

        _reportedKeyPem = pem;
        GitHubLog.TokenSigningFailed(_logger, exception.GetType().Name);
    }

    private RSA? LoadKey(GitHubOptions options)
    {
        var pem = options.PrivateKey;
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        if (_key is not null && string.Equals(_keyPem, pem, StringComparison.Ordinal))
        {
            return _key;
        }

        var replacement = RSA.Create();
        try
        {
            replacement.ImportFromPem(pem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            replacement.Dispose();
            ReportSigningFailure(pem, exception);
            return null;
        }

        _key?.Dispose();
        _key = replacement;
        _keyPem = pem;

        return _key;
    }
}
