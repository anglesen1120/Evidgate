using Evidgate.Modules.Ingestion.Contracts;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The argument bounds of the registry, shared with the migration's CHECKs through
/// <see cref="ProducerSlotLimits"/> so the two cannot drift (00 §5.5 C6).
/// </summary>
internal static class ProducerSlotArguments
{
    /// <summary>True when <paramref name="producerId"/> matches the column's charset and length bound.</summary>
    internal static bool IsValidProducerId(string? producerId) =>
        producerId is not null && ProducerIdRegex.IsMatch(producerId);

    /// <summary>
    /// True when <paramref name="value"/> is 1..<paramref name="maxLength"/> characters and carries no
    /// control character or lone surrogate: PostgreSQL rejects U+0000 in text (22021) and a lone
    /// surrogate cannot be encoded, so either would surface as an unhandled exception instead of a
    /// result (00 §5.5 C6).
    /// </summary>
    internal static bool IsWithin(string? value, int maxLength) =>
        value is not null
        && value.Length >= 1
        && value.Length <= maxLength
        && IsStorableText(value);

    /// <summary>True when every character is storable: no control character and no lone surrogate.</summary>
    private static bool IsStorableText(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (char.IsControl(character))
            {
                return false;
            }

            if (char.IsSurrogate(character))
            {
                // A surrogate pair is fine; a lone surrogate is not.
                if (!char.IsHighSurrogate(character)
                    || index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
        }

        return true;
    }

    /// <summary>
    /// The migration's <c>producer_slot_producer_id_check</c> charset, built from the single shared
    /// constant: the trailing <c>$</c> of <see cref="ProducerSlotLimits.ProducerIdPattern"/> becomes
    /// <c>\z</c>, because .NET's <c>$</c> also matches before a trailing newline while PostgreSQL's
    /// <c>~</c> does not. Built at runtime (not <c>[GeneratedRegex]</c>) precisely so the shared
    /// constant stays the one source of the charset.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ProducerIdRegex = new(
        ProducerSlotLimits.ProducerIdPattern[..^1] + @"\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
}
