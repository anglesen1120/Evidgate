using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The key material of the session cookie (02.3.1-T1.4, completed by 02.3.2-T1.1): the pepper behind the
/// HMAC of the cookie value, with the version stored beside every hash (TL T5, 0016's construction applied
/// to sessions), and — during a rotation window — the previous pepper so the sessions it signed keep
/// working while they drain.
/// </summary>
/// <remarks>
/// The cookie VALUE never reaches the database: this type hashes it once, and the caller discards it after
/// handing it to the browser. There is no un-hash direction on purpose.
/// </remarks>
internal sealed class SessionSecrets
{
    /// <summary>Shortest pepper this contract accepts, in bytes (256 bits of key material).</summary>
    internal const int MinKeyBytes = 32;

    private readonly (int Version, byte[] Key)? _current;
    private readonly (int Version, byte[] Key)? _previous;

    /// <summary>Decodes the configured peppers; an absent or malformed value leaves this instance unable to hash.</summary>
    /// <param name="options">The bound session options.</param>
    public SessionSecrets(IOptions<SessionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _current = Decode(value.Pepper) is { } pepper ? (value.PepperVersion, pepper) : null;

        // A previous pepper without a version (or the same version as the current one) is refused at boot by
        // the validator, so a pair is either complete here or absent.
        _previous = Decode(value.PreviousPepper) is { } previous && value.PreviousPepperVersion is { } version
            ? (version, previous)
            : null;
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

    /// <summary>
    /// The lookup candidates for one presented cookie value (02.3.2-T1.1, D4): its hash under the current
    /// pepper first, then — while a rotation window is open — its hash under the previous one. The request
    /// path hands all of them to one statement, so the lookup costs at most two HMAC computations and one
    /// index probe, and a session signed with the retired pepper keeps working until it is re-hashed.
    /// </summary>
    /// <param name="data">The cookie value's ASCII bytes.</param>
    /// <returns>
    /// One or two entries, current first; EMPTY when no pepper is configured (check <see cref="CanHash"/>
    /// first — an unprovisioned deployment answers 503 rather than treat every caller as anonymous).
    /// </returns>
    public IReadOnlyList<(int Version, byte[] Hash)> Candidates(ReadOnlySpan<byte> data)
    {
        if (_current is not { } current)
        {
            return Array.Empty<(int, byte[])>();
        }

        if (_previous is not { } previous)
        {
            // ONE candidate, and it is the HASH: `_current` holds the pepper (version, key), which is key
            // material, not a lookup value. Returning the key here made every lookup on a deployment that
            // never rotated compare the pepper itself against `session.cookie_hmac`, so no live session
            // authenticated (and no forwarded cookie was recognised as a predecessor).
            return [(current.Version, HMACSHA256.HashData(current.Key, data))];
        }

        return
        [
            (current.Version, HMACSHA256.HashData(current.Key, data)),
            (previous.Version, HMACSHA256.HashData(previous.Key, data)),
        ];
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
/// Resolves the session peppers through <see cref="ISecretProvider"/> after the binder has run
/// (10.1.1-T2), following <c>CiAuthOptionsSecretPostConfigure</c> and
/// <c>GoogleLoginOptionsSecretPostConfigure</c>. The properties have internal setters, so
/// <c>BindConfiguration</c> skips them and this is their only writer; validation runs after
/// post-configuration, so <see cref="SessionOptionsValidator"/> still sees the resolved values and still
/// names only the keys.
/// </summary>
internal sealed class SessionOptionsSecretPostConfigure : IPostConfigureOptions<SessionOptions>
{
    private readonly ISecretProvider _secrets;

    /// <summary>Binds the secret provider the peppers are resolved from.</summary>
    /// <param name="secrets">The one secret seam of the platform (10.1.1-T2).</param>
    public SessionOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Pepper = _secrets.GetSecret(SecretNames.SessionPepper);

        // 02.3.2-T1.1: the retirement half of a pepper rotation. Absent in a deployment that never rotated,
        // which is why it is optional and why its absence (with a null version) is not a validation failure.
        options.PreviousPepper = _secrets.GetSecret(SecretNames.SessionPreviousPepper);
    }
}

/// <summary>
/// Boot validation of the session options: a present pepper must be base64 of at least
/// <see cref="SessionSecrets.MinKeyBytes"/> bytes, the pepper version at least 1, the idle lifetime inside the
/// absolute one (itself at most a year), and the rotation pair consistent — a previous pepper needs a
/// version that differs from the current one, and a previous version without a pepper is refused because
/// nothing would ever verify a hash made with it.
/// </summary>
/// <remarks>
/// Null, empty and whitespace are one case here: ABSENT. A deployment that injects an empty value for the
/// pepper leaves session creation off (login answers 503) instead of failing the host at boot. A message
/// names the configuration key only — never a value, because the peppers travel through this object.
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

        CheckPepper(failures, "Pepper", options.Pepper);
        CheckPepper(failures, "PreviousPepper", options.PreviousPepper);

        if (options.PepperVersion < 1)
        {
            failures.Add("EVIDGATE:Session:PepperVersion must be at least 1");
        }

        CheckRotationPair(failures, options);

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

    /// <summary>
    /// The rotation window's pair (D4): a previous pepper needs a version that is at least 1 and differs from
    /// the current one — the same version on two peppers would make a hash ambiguous — its VALUE must differ
    /// from the current pepper's (an identical pair is a window that retires nothing), and a previous version
    /// without a pepper is refused rather than kept as an inert setting.
    /// </summary>
    private static void CheckRotationPair(List<string> failures, SessionOptions options)
    {
        var hasPreviousPepper = !string.IsNullOrWhiteSpace(options.PreviousPepper);

        if (!hasPreviousPepper)
        {
            if (options.PreviousPepperVersion is not null)
            {
                failures.Add(
                    "EVIDGATE:Session:PreviousPepperVersion is set but EVIDGATE:Session:PreviousPepper is absent; "
                    + "remove the version or provide the pepper it belongs to");
            }

            return;
        }

        if (options.PreviousPepperVersion is not { } previousVersion)
        {
            failures.Add(
                "EVIDGATE:Session:PreviousPepper is set but EVIDGATE:Session:PreviousPepperVersion is absent, so no "
                + "stored hash could be matched to it");
            return;
        }

        if (previousVersion < 1)
        {
            failures.Add("EVIDGATE:Session:PreviousPepperVersion must be at least 1");
        }

        if (previousVersion == options.PepperVersion)
        {
            failures.Add(
                "EVIDGATE:Session:PreviousPepperVersion must differ from EVIDGATE:Session:PepperVersion, "
                + "otherwise the two peppers' hashes are indistinguishable");
        }

        // A "rotation" whose two peppers are the same value is a no-op window: the previous hash and the
        // current hash are then one hash, so the re-hash never happens and the retirement step retires
        // nothing. The comparison is on the canonical base64 strings, which is exact for this purpose.
        if (string.Equals(
                options.PreviousPepper!.Trim(),
                options.Pepper?.Trim(),
                StringComparison.Ordinal))
        {
            failures.Add(
                "EVIDGATE:Session:PreviousPepper must differ from EVIDGATE:Session:Pepper, "
                + "otherwise the rotation window has nothing to retire");
        }
    }

    private static void CheckPepper(List<string> failures, string property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            if (Convert.FromBase64String(value).Length < SessionSecrets.MinKeyBytes)
            {
                failures.Add($"EVIDGATE:Session:{property} must decode to at least {SessionSecrets.MinKeyBytes} bytes");
            }
        }
        catch (FormatException)
        {
            failures.Add($"EVIDGATE:Session:{property} is not valid base64");
        }
    }
}
