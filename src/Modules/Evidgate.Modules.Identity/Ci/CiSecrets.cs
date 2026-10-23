using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>
/// The key material of CI authentication: the peppers behind ingest-token and binding-challenge
/// HMACs (TL T5) and the key that signs minted <c>ci_producer</c> tokens (D7). Misconfigured
/// values are rejected at boot by <see cref="CiAuthOptionsValidator"/>; absent values leave the
/// matching capability off.
/// </summary>
internal sealed class CiSecrets
{
    internal const int MinKeyBytes = 32;

    private readonly byte[]? _signingKey;
    private readonly (int Version, byte[] Key)? _current;
    private readonly (int Version, byte[] Key)? _previous;

    public CiSecrets(IOptions<CiAuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _signingKey = Decode(value.SigningKey);
        _current = Decode(value.Pepper) is { } current ? (value.PepperVersion, current) : null;
        _previous = Decode(value.PreviousPepper) is { } previous ? (value.PreviousPepperVersion, previous) : null;
    }

    /// <summary>The HS256 signing key, or null when none is configured.</summary>
    public byte[]? SigningKey => _signingKey;

    /// <summary>Whether a current pepper exists, i.e. secrets can be hashed.</summary>
    public bool CanHash => _current is not null;

    /// <summary>HMAC-SHA256 of <paramref name="data"/> under the current pepper, with the pepper's version.</summary>
    /// <param name="data">The secret bytes.</param>
    public (int Version, byte[] Hash) HashCurrent(ReadOnlySpan<byte> data)
    {
        var (version, key) = _current ?? throw new InvalidOperationException("no pepper is configured");

        return (version, HMACSHA256.HashData(key, data));
    }

    /// <summary>HMAC under the pepper of <paramref name="version"/>, or null when that version is no longer configured.</summary>
    /// <param name="version">Stored <c>pepper_version</c>.</param>
    /// <param name="data">The secret bytes.</param>
    public byte[]? HashWithVersion(int version, ReadOnlySpan<byte> data)
    {
        if (_current is { } current && current.Version == version)
        {
            return HMACSHA256.HashData(current.Key, data);
        }

        return _previous is { } previous && previous.Version == version
            ? HMACSHA256.HashData(previous.Key, data)
            : null;
    }

    /// <summary>The HMAC of <paramref name="data"/> under every live pepper, current first.</summary>
    /// <param name="data">The secret bytes.</param>
    public IReadOnlyList<byte[]> HashUnderEveryPepper(ReadOnlySpan<byte> data)
    {
        var hashes = new List<byte[]>(2);
        if (_current is { } current)
        {
            hashes.Add(HMACSHA256.HashData(current.Key, data));
        }

        if (_previous is { } previous)
        {
            hashes.Add(HMACSHA256.HashData(previous.Key, data));
        }

        return hashes;
    }

    /// <summary>UTF-8 bytes of a text secret.</summary>
    /// <param name="secret">The secret.</param>
    public static byte[] Bytes(string secret) => Encoding.UTF8.GetBytes(secret);

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Boot validation of the key material: present values must be base64 and long enough.</summary>
internal sealed class CiAuthOptionsValidator : IValidateOptions<CiAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, CiAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        Check(failures, nameof(CiAuthOptions.SigningKey), options.SigningKey);
        Check(failures, nameof(CiAuthOptions.Pepper), options.Pepper);
        Check(failures, nameof(CiAuthOptions.PreviousPepper), options.PreviousPepper);

        if (options.PreviousPepper is not null && options.PreviousPepperVersion == options.PepperVersion)
        {
            failures.Add("EVIDGATE:Ci:PreviousPepperVersion must differ from PepperVersion");
        }

        if (!Uri.TryCreate(options.GithubJwksUri, UriKind.Absolute, out var jwks)
            || jwks.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(options.GithubOidcIssuer, UriKind.Absolute, out var issuer)
            || !string.Equals(jwks.Host, issuer.Host, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("EVIDGATE:Ci:GithubJwksUri must be https on the host of GithubOidcIssuer (S4 allowlist)");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(List<string> failures, string property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            if (Convert.FromBase64String(value).Length < CiSecrets.MinKeyBytes)
            {
                failures.Add($"EVIDGATE:Ci:{property} must be at least {CiSecrets.MinKeyBytes} bytes");
            }
        }
        catch (FormatException)
        {
            failures.Add($"EVIDGATE:Ci:{property} is not valid base64");
        }
    }
}

/// <summary>
/// Resolves the CI key material through <see cref="ISecretProvider"/> after the
/// binder has run (10.1.1-T2). The three properties have internal setters, so
/// <c>BindConfiguration</c> skips them and this is their only writer; validation
/// runs after post-configuration, so <see cref="CiAuthOptionsValidator"/> still sees
/// the resolved value and still names only the key.
/// </summary>
internal sealed class CiAuthOptionsSecretPostConfigure : IPostConfigureOptions<CiAuthOptions>
{
    private readonly ISecretProvider _secrets;

    public CiAuthOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, CiAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.SigningKey = _secrets.GetSecret(SecretNames.CiSigningKey);
        options.Pepper = _secrets.GetSecret(SecretNames.CiPepper);
        options.PreviousPepper = _secrets.GetSecret(SecretNames.CiPreviousPepper);
    }
}
