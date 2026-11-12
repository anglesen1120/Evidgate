using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// Mints the RS256 App JWT that authenticates the App itself (installation lookup and the token
/// exchange). The key is read from <see cref="ISecretProvider"/> by name and parsed in place; the
/// PEM and the JWT never leave this type except as the returned token, and no exception message
/// repeats either.
/// </summary>
/// <remarks>
/// The JWT is minted per call rather than cached: signing is cheap, and a call that follows a key
/// rotation must not present the previous key. Lifetime is ten minutes from <c>iat</c> — GitHub's
/// maximum — with <c>iat</c> backdated so a small clock difference at GitHub does not refuse it.
/// </remarks>
internal sealed class GitHubAppTokenSource : IDisposable
{
    /// <summary>GitHub accepts an App JWT for at most ten minutes.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly ISecretProvider _secrets;
    private readonly IOptions<GitHubOptions> _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private RSA? _key;
    private string? _keyPem;

    /// <summary>Creates the source over the configured App id and the secret holding its key.</summary>
    public GitHubAppTokenSource(
        ISecretProvider secrets,
        IOptions<GitHubOptions> options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _secrets = secrets;
        _options = options;
        _time = time;
    }

    /// <summary>
    /// Returns a fresh App JWT, or <c>github_not_configured</c> when the App id or the key is
    /// absent or unusable. Nothing about the key's value reaches the error.
    /// </summary>
    /// <param name="cancellationToken">Cancels the secret lookup.</param>
    public async ValueTask<Result<string>> CreateAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            return Result.Failure<string>(NotConfigured());
        }

        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = await LoadKeyAsync(options, cancellationToken).ConfigureAwait(false);
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
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256),
            };

            return Result.Success(Handler.CreateToken(descriptor));
        }
        finally
        {
            _keyGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _key?.Dispose();
        _keyGate.Dispose();
    }

    /// <summary>The one "no App is configured" failure, used for a missing id and a missing key alike.</summary>
    internal static Error NotConfigured() =>
        new(GitHubErrorCodes.NotConfigured, "No GitHub App is configured on this host.", 503);

    private async ValueTask<RSA?> LoadKeyAsync(GitHubOptions options, CancellationToken cancellationToken)
    {
        string? pem;
        try
        {
            pem = await _secrets.GetSecretAsync(options.PrivateKeySecretName, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The reference must be a registered name; the validator refuses anything else at boot.
            return null;
        }

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
            return null;
        }

        _key?.Dispose();
        _key = replacement;
        _keyPem = pem;

        return _key;
    }
}
