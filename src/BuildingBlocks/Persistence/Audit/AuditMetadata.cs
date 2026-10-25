using System.Text.RegularExpressions;

namespace Evidgate.BuildingBlocks.Persistence.Audit;

/// <summary>
/// The metadata allowlist (02.7.1-T2.2, 00 §5.7 S3): an audit row never carries a secret.
/// Keys must be on the allowlist, values are short plain strings, and anything shaped like a
/// credential is refused when the entry is built — a programming error, not user input.
/// </summary>
public static partial class AuditMetadata
{
    /// <summary>Most keys an entry may carry.</summary>
    public const int MaxKeys = 16;

    /// <summary>Longest value, in characters.</summary>
    public const int MaxValueLength = 256;

    /// <summary>The keys handlers may record. Adding one is a review decision.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "email_domain",
        "role_code",
        "role_id",
        "membership_id",
        "slug",
        "status",
        "fields",
        "count",
        "format",
        "range_days",
        "reason",
        "outcome",
        // 07.1.1-T4.1: the activation records which revision it moved the pointer from and the mode it stored.
        "previous_id",
        "mode",
        // 02.3.1-T2: login/link/bind rows record which provider authenticated the identity and how an invite bound (names, never an address or subject).
        "provider",
        "via",
    };

    /// <summary>Throws if <paramref name="metadata"/> breaks the allowlist; returns it otherwise.</summary>
    /// <param name="metadata">The candidate metadata.</param>
    public static IReadOnlyDictionary<string, string>? Validate(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        if (metadata.Count > MaxKeys)
        {
            throw new ArgumentException($"audit metadata carries more than {MaxKeys} keys", nameof(metadata));
        }

        foreach (var (key, value) in metadata)
        {
            if (!AllowedKeys.Contains(key))
            {
                throw new ArgumentException($"audit metadata key '{key}' is not on the allowlist", nameof(metadata));
            }

            if (value is null || value.Length > MaxValueLength)
            {
                throw new ArgumentException($"audit metadata value of '{key}' is missing or too long", nameof(metadata));
            }

            if (SecretShaped().IsMatch(value))
            {
                throw new ArgumentException($"audit metadata value of '{key}' looks like a credential", nameof(metadata));
            }
        }

        return metadata;
    }

    // A long unbroken run of token characters, a JWT, or an authorization scheme prefix.
    [GeneratedRegex(@"(?i)(bearer\s+\S+|eyJ[\w-]{10,}\.|[A-Za-z0-9+/_\-]{40,}={0,2}|(secret|password|token)\s*[=:])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretShaped();
}
