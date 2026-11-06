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
