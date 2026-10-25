using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The key material of session creation (02.3.1-T1.4): the pepper behind the HMAC of the session
/// cookie value, with the version stored beside every hash (TL T5, 0016's construction applied to
/// sessions). Misconfigured values are rejected at boot by <see cref="SessionOptionsValidator"/>; an
/// absent pepper leaves session creation off (<see cref="CanHash"/> false, 503 <c>unavailable</c>).
/// </summary>
/// <remarks>
/// The cookie VALUE never reaches the database: this type hashes it once, and the caller discards it
/// after handing it to the browser. There is no un-hash direction on purpose.
/// </remarks>
internal sealed class SessionSecrets
{
    /// <summary>Shortest pepper this contract accepts, in bytes (256 bits of key material).</summary>
    internal const int MinKeyBytes = 32;

    private readonly (int Version, byte[] Key)? _current;

    /// <summary>Decodes the configured pepper; an absent or malformed value leaves this instance unable to hash.</summary>
    /// <param name="options">The bound session options.</param>
    public SessionSecrets(IOptions<SessionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _current = Decode(value.Pepper) is { } pepper ? (value.PepperVersion, pepper) : null;
    }

    /// <summary>Whether a pepper exists, i.e. a session cookie can be hashed and a session created.</summary>
    public bool CanHash => _current is not null;

    /// <summary>HMAC-SHA256 of <paramref name="data"/> under the current pepper, with the pepper's version.</summary>
    /// <param name="data">The secret bytes (the cookie value's ASCII bytes).</param>
    public (int Version, byte[] Hash) HashCurrent(ReadOnlySpan<byte> data)
    {
        var (version, key) = _current ?? throw new InvalidOperationException("no session pepper is configured");

        return (version, HMACSHA256.HashData(key, data));
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
/// Resolves the session pepper through <see cref="ISecretProvider"/> after the binder has run
/// (10.1.1-T2), following <c>CiAuthOptionsSecretPostConfigure</c> and
/// <c>GoogleLoginOptionsSecretPostConfigure</c>. The property has an internal setter, so
/// <c>BindConfiguration</c> skips it and this is its only writer; validation runs after
/// post-configuration, so <see cref="SessionOptionsValidator"/> still sees the resolved value and still
/// names only the key.
/// </summary>
internal sealed class SessionOptionsSecretPostConfigure : IPostConfigureOptions<SessionOptions>
{
    private readonly ISecretProvider _secrets;

    /// <summary>Binds the secret provider the pepper is resolved from.</summary>
    /// <param name="secrets">The one secret seam of the platform (10.1.1-T2).</param>
    public SessionOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Pepper = _secrets.GetSecret(SecretNames.SessionPepper);
    }
}

/// <summary>
/// Boot validation of the session options: a present pepper must be base64 of at least
/// <see cref="SessionSecrets.MinKeyBytes"/> bytes, the pepper version at least 1, and the idle lifetime
/// must sit inside the absolute one, which must itself stay within a year.
/// </summary>
/// <remarks>
/// Null, empty and whitespace are one case here: ABSENT. A deployment that injects an empty value for the
/// pepper leaves session creation off (login answers 503) instead of failing the host at boot. A message
/// names the configuration key only — never a value, because the pepper travels through this object.
/// </remarks>
internal sealed class SessionOptionsValidator : IValidateOptions<SessionOptions>
{
    /// <summary>Shortest lifetime this contract accepts, in seconds.</summary>
    internal const int MinLifetimeSeconds = 60;

    /// <summary>Longest absolute lifetime this contract accepts, in seconds (one year).</summary>
    internal const int MaxLifetimeSeconds = 31_536_000;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        CheckPepper(failures, options.Pepper);

        if (options.PepperVersion < 1)
        {
            failures.Add("EVIDGATE:Session:PepperVersion must be at least 1");
        }

        if (options.IdleSeconds < MinLifetimeSeconds)
        {
            failures.Add($"EVIDGATE:Session:IdleSeconds must be at least {MinLifetimeSeconds} seconds");
        }

        if (options.AbsoluteSeconds > MaxLifetimeSeconds)
        {
            failures.Add($"EVIDGATE:Session:AbsoluteSeconds must be at most {MaxLifetimeSeconds} seconds");
        }

        if (options.IdleSeconds > options.AbsoluteSeconds)
        {
            failures.Add("EVIDGATE:Session:IdleSeconds must not exceed AbsoluteSeconds");
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
            if (Convert.FromBase64String(value).Length < SessionSecrets.MinKeyBytes)
            {
                failures.Add($"EVIDGATE:Session:Pepper must decode to at least {SessionSecrets.MinKeyBytes} bytes");
            }
        }
        catch (FormatException)
        {
            failures.Add("EVIDGATE:Session:Pepper is not valid base64");
        }
    }
}
