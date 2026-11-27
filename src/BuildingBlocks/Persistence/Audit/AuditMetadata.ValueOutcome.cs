namespace Evidgate.BuildingBlocks.Persistence.Audit;

// The per-value half of the allowlist rule, kept beside the main file so that file stays under the size limit.
public static partial class AuditMetadata
{
    /// <summary>
    /// The verdict on one metadata value on its own. <see cref="Validate"/> applies this same rule to every value
    /// it accepts; a command that puts caller text into an audit row calls it up front, so that text is refused as
    /// the request's own violation instead of failing the row after the write.
    /// </summary>
    /// <param name="value">The candidate value.</param>
    /// <returns>The outcome, one of <see cref="MetadataValueOutcome"/>.</returns>
    public static MetadataValueOutcome ValueOutcome(string? value)
    {
        if (value is null)
        {
            return MetadataValueOutcome.Missing;
        }

        if (value.Length > MaxValueLength)
        {
            return MetadataValueOutcome.TooLong;
        }

        return SecretShaped().IsMatch(value)
            ? MetadataValueOutcome.SecretShaped
            : MetadataValueOutcome.Accepted;
    }

    /// <summary>
    /// True for the one value a long unbroken run is legitimate in: the <c>report_hash</c> digest, exactly
    /// <c>sha256:</c> and 64 lowercase hex digits. A digest names content without carrying it, but the credential-shape
    /// rule cannot tell it from a token, so it is judged by its exact form and only under its own key.
    /// </summary>
    private static bool IsContentDigest(string key, string? value)
    {
        const string prefix = "sha256:";
        if (key != "report_hash" || value is null || value.Length != prefix.Length + 64
            || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Why a metadata value would be refused: the rule <see cref="Validate"/> enforces on every value.</summary>
    public enum MetadataValueOutcome
    {
        /// <summary>The value fits the rule.</summary>
        Accepted = 0,

        /// <summary>The value is null.</summary>
        Missing,

        /// <summary>The value is longer than <see cref="MaxValueLength"/> characters.</summary>
        TooLong,

        /// <summary>The value is shaped like a credential.</summary>
        SecretShaped,
    }
}
