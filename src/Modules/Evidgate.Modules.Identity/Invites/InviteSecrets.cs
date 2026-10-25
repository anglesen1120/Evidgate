using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// The key material of invite minting (02.1.2-T2): the pepper behind the HMAC of an invite link's
/// secret half, with the version stored beside every hash (TL T5, 0016's construction applied to
/// invites). Misconfigured values are rejected at boot by <see cref="InviteOptionsValidator"/>; an
/// absent pepper leaves minting off (<see cref="CanHash"/> false, 503 <c>unavailable</c>).
/// </summary>
/// <remarks>
/// The secret half of the token never reaches the database: this type hashes it once and the
/// command hands the plaintext to <see cref="InviteSecretCarrier"/> only long enough for the
/// endpoint to answer with it. There is no un-hash direction on purpose.
/// </remarks>
internal sealed class InviteSecrets
{
    /// <summary>Shortest pepper this contract accepts, in bytes (256 bits of key material).</summary>
    internal const int MinKeyBytes = 32;

    private readonly (int Version, byte[] Key)? _current;

    /// <summary>Decodes the configured pepper; an absent or malformed value leaves this instance unable to hash.</summary>
    /// <param name="options">The bound invite options.</param>
    public InviteSecrets(IOptions<InviteOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _current = Decode(value.Pepper) is { } pepper ? (value.PepperVersion, pepper) : null;
    }

    /// <summary>Whether a pepper exists, i.e. an invite token can be minted and verified.</summary>
    public bool CanHash => _current is not null;

    /// <summary>HMAC-SHA256 of <paramref name="secretUtf8"/> under the current pepper, with the pepper's version.</summary>
    /// <param name="secretUtf8">The UTF-8 bytes of the token's secret half (<c>InviteTokenFormat.Generated.Secret</c>).</param>
    public (int Version, byte[] Hash) HashCurrent(ReadOnlySpan<byte> secretUtf8)
    {
        var (version, key) = _current ?? throw new InvalidOperationException("no invite pepper is configured");

        return (version, HMACSHA256.HashData(key, secretUtf8));
    }

    /// <summary>
    /// Constant-time comparison of <paramref name="secret"/> against the stored HMAC, under the
    /// pepper of <paramref name="pepperVersion"/>; false when that version is not configured (a
    /// retired pepper can no longer verify anything this deployment minted before it).
    /// </summary>
    /// <param name="pepperVersion">Stored <c>membership.invite_pepper_version</c>.</param>
    /// <param name="storedHash">Stored <c>membership.invite_token_hash</c> (32 bytes).</param>
    /// <param name="secret">The presented token's secret half.</param>
    public bool Verify(int pepperVersion, byte[] storedHash, string secret)
    {
        ArgumentNullException.ThrowIfNull(storedHash);

        if (_current is not { } current || current.Version != pepperVersion || storedHash.Length == 0)
        {
            return false;
        }

        var computed = HMACSHA256.HashData(current.Key, Encoding.UTF8.GetBytes(secret));

        return CryptographicOperations.FixedTimeEquals(computed, storedHash);
    }

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

/// <summary>
/// Resolves the invite pepper through <see cref="ISecretProvider"/> after the binder has run
/// (10.1.1-T2), following <c>SessionOptionsSecretPostConfigure</c>. The property has an internal
/// setter, so <c>BindConfiguration</c> skips it and this is its only writer; validation runs after
/// post-configuration, so <see cref="InviteOptionsValidator"/> still sees the resolved value and
/// still names only the key.
/// </summary>
internal sealed class InviteOptionsSecretPostConfigure : IPostConfigureOptions<InviteOptions>
{
    private readonly ISecretProvider _secrets;

    /// <summary>Binds the secret provider the pepper is resolved from.</summary>
    /// <param name="secrets">The one secret seam of the platform (10.1.1-T2).</param>
    public InviteOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, InviteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Pepper = _secrets.GetSecret(SecretNames.InvitePepper);
    }
}

/// <summary>
/// Boot validation of the invite options: a present pepper must be base64 of at least
/// <see cref="InviteSecrets.MinKeyBytes"/> bytes, and the pepper version at least 1.
/// </summary>
/// <remarks>
/// Null, empty and whitespace are one case here: ABSENT. A deployment that injects an empty value
/// for the pepper leaves minting off (create/resend answer 503) instead of failing the host at
/// boot. A message names the configuration key only — never a value, because the pepper travels
/// through this object.
/// </remarks>
internal sealed class InviteOptionsValidator : IValidateOptions<InviteOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, InviteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckPepper(failures, options.Pepper);

        if (options.PepperVersion < 1)
        {
            failures.Add("EVIDGATE:Invite:PepperVersion must be at least 1");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckPepper(List<string> failures, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            if (Convert.FromBase64String(value).Length < InviteSecrets.MinKeyBytes)
            {
                failures.Add($"EVIDGATE:Invite:Pepper must decode to at least {InviteSecrets.MinKeyBytes} bytes");
            }
        }
        catch (FormatException)
        {
            failures.Add("EVIDGATE:Invite:Pepper is not valid base64");
        }
    }
}
