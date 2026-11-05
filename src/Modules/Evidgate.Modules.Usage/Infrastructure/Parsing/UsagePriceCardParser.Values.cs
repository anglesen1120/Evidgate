using System.Text.Json;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Infrastructure.Parsing;

/// <summary>
/// The value readers of <see cref="UsagePriceCardParser"/>: one JSON string, money token or instant decoded into the
/// shape a row needs. Each either answers the parsed value or earns the refusal itself, so a caller never inspects a
/// raw element twice and text the column cannot store is refused here instead of failing at the write.
/// </summary>
internal static partial class UsagePriceCardParser
{
    /// <summary>
    /// Reads a JSON string only when it is text the database can store: a lone surrogate or a U+0000 has no encoding
    /// the column accepts, and letting it through would turn a refusal into a server error on write.
    /// </summary>
    private static string? ReadStorableString(JsonElement value, string path, ViolationSink violations)
    {
        if (value.ValueKind == JsonValueKind.String
            && TryDecodeString(value, out var text)
            && PostgresText.IsStorable(text))
        {
            return text;
        }

        violations.Add(path, UsagePriceRefusalReasons.InvalidValue);
        return null;
    }

    /// <summary>
    /// Decodes a JSON string, or answers false for an escape that names an unpaired surrogate.
    /// System.Text.Json reports that case by throwing from <c>GetString</c> — the only signal it gives — so the
    /// exception is caught here and becomes the card's own named refusal instead of escaping the parser.
    /// </summary>
    private static bool TryDecodeString(JsonElement value, out string text)
    {
        try
        {
            if (value.GetString() is { } decoded)
            {
                text = decoded;
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // The string's escapes cannot be decoded to UTF-16; the caller reports it as an invalid value.
        }

        text = string.Empty;
        return false;
    }

    private static decimal? ReadAmount(JsonElement value, string path, ViolationSink violations)
    {
        // A JSON number is read from its raw token: converting through a floating-point type would round the money.
        string? token;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (!TryDecodeString(value, out var text) || !PostgresText.IsStorable(text))
                {
                    violations.Add(path, UsagePriceRefusalReasons.InvalidAmount);
                    return null;
                }

                token = text;
                break;
            case JsonValueKind.Number:
                token = value.GetRawText();
                break;
            default:
                violations.Add(path, UsagePriceRefusalReasons.InvalidAmount);
                return null;
        }

        if (!UsagePriceCardRules.TryParseAmount(token, out var amount))
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidAmount);
            return null;
        }

        return amount;
    }

    private static decimal? ReadNullableAmount(JsonElement value, string path, ViolationSink violations) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadAmount(value, path, violations);

    private static DateTimeOffset? ReadInstant(JsonElement value, string path, ViolationSink violations)
    {
        var text = ReadStorableString(value, path, violations);
        if (text is null)
        {
            return null;
        }

        if (!UsagePriceCardRules.TryParseInstant(text, out var instant))
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidValue);
            return null;
        }

        // The column stores microseconds; a finer instant would be rounded, which can move a window boundary or even
        // collapse a window to nothing, so the card is refused instead of silently shifted.
        if (!UsagePriceCardRules.IsMicrosecondAligned(instant))
        {
            violations.Add(path, UsagePriceRefusalReasons.InvalidInstant);
            return null;
        }

        return instant;
    }

    private static DateTimeOffset? ReadNullableInstant(
        JsonElement value,
        string path,
        ViolationSink violations) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadInstant(value, path, violations);

    private static int CodePointLength(string value)
    {
        var length = 0;

        foreach (var _ in value.EnumerateRunes())
        {
            length++;
        }

        return length;
    }
}
