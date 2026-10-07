using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Policy.Domain;

/// <summary>
/// The outcome of the number rule over a body (<see cref="PolicyRevisionRules.ClassifyNumbers"/>): the read path
/// must tell a genuinely inexact number from a body that is not well-formed JSON at all (finding F2).
/// </summary>
internal enum PolicyNumberOutcome
{
    /// <summary>Every number in the body is exactly representable as an IEEE 754 double (I-JSON, RFC 7493 §2.2).</summary>
    Exact,

    /// <summary>The body is well-formed JSON but carries a number only a double can round.</summary>
    InexactNumber,

    /// <summary>The body is not well-formed JSON (malformed, or nested past the canonicalizer's depth cap).</summary>
    NotCanonicalisable,
}

/// <summary>
/// Shape rules of a policy revision (07.1.1-T1): the body is a JSON object (00 §8.6) carrying only text jsonb can
/// store, and every number in it is an exact IEEE 754 double (I-JSON, RFC 7493 §2.2). Both are checked before the
/// store touches the database, so a malformed append never reaches a statement.
/// </summary>
internal static class PolicyRevisionRules
{
    /// <summary>
    /// True when <paramref name="body"/> parses as a JSON object whose text jsonb can store: no U+0000 and no unpaired
    /// UTF-16 surrogate in a member name or string value (the 05.1.1/05.1.2 <c>JsonBody</c> rule).
    /// </summary>
    public static bool IsJsonObject(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && !HasUnstorableText(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when every number in <paramref name="body"/> is exactly representable as an IEEE 754 double (I-JSON,
    /// RFC 7493 §2.2): RFC 8785 §3.2.2.3 canonicalises a number as its shortest round-trip double, so a literal
    /// such as <c>9007199254740993</c> would be rounded to <c>9007199254740992</c> and two different bodies would
    /// share one content address. A body that is not well-formed JSON carries no exact numbers either.
    /// </summary>
    public static bool HasOnlyExactNumbers(string? body) =>
        ClassifyNumbers(body) == PolicyNumberOutcome.Exact;

    /// <summary>
    /// Classifies the numbers of <paramref name="body"/> (I-JSON, RFC 7493 §2.2) for the read path, which must tell
    /// a genuinely inexact number from a body that is not well-formed JSON at all:
    /// <see cref="PolicyNumberOutcome.Exact"/> when every number is exactly representable as an IEEE 754 double,
    /// <see cref="PolicyNumberOutcome.InexactNumber"/> when a well-formed body carries a number only a double can
    /// round, and <see cref="PolicyNumberOutcome.NotCanonicalisable"/> when the body does not parse (malformed, or
    /// nested past the canonicalizer's depth cap) and so carries no exact numbers either.
    /// </summary>
    public static PolicyNumberOutcome ClassifyNumbers(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return PolicyNumberOutcome.NotCanonicalisable;
        }

        try
        {
            // One token pass over the same bytes the canonicalizer reads; no parse tree is materialised.
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(body), new JsonReaderOptions
            {
                MaxDepth = JsonCanonicalizer.MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });

            var inexact = false;
            while (reader.Read())
            {
                if (!inexact && reader.TokenType == JsonTokenType.Number && !IsExactlyRepresentable(reader.ValueSpan))
                {
                    inexact = true;
                }
            }

            return inexact ? PolicyNumberOutcome.InexactNumber : PolicyNumberOutcome.Exact;
        }
        catch (JsonException)
        {
            return PolicyNumberOutcome.NotCanonicalisable;
        }
    }

    /// <summary>
    /// True when a member name or string anywhere in <paramref name="element"/> is no text PostgreSQL can carry: an
    /// unpaired UTF-16 surrogate (<c>"\ud800"</c>) or U+0000 (refused by jsonb). The 05.1.1/05.1.2 <c>JsonBody</c>
    /// walk, kept local because that helper is internal to the Orchestrator module. The command handler runs it
    /// before the schema engine, which throws on such an escape.
    /// </summary>
    internal static bool HasUnstorableText(JsonElement element)
    {
        try
        {
            return Walk(element);
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json refuses to transcode an invalid escape sequence.
            return true;
        }

        static bool Walk(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => Unstorable(element.GetString()!),
            JsonValueKind.Array => element.EnumerateArray().Any(Walk),
            JsonValueKind.Object => element.EnumerateObject().Any(member => Unstorable(member.Name) || Walk(member.Value)),
            _ => false,
        };
    }

    /// <summary>NUL, or a surrogate without its partner.</summary>
    private static bool Unstorable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the JSON number literal <paramref name="token"/> denotes exactly the double it parses to. The two
    /// decimal values are compared as (sign, significant digits, exponent), so <c>1.0</c>, <c>1e2</c>, <c>0.10</c>
    /// and <c>-0</c> are accepted while <c>9007199254740993</c> and <c>0.1000000000000000001</c> are not. A literal
    /// outside the finite double range is left to the canonicalizer, which reports it as
    /// <see cref="JsonCanonicalizationException"/>.
    /// </summary>
    private static bool IsExactlyRepresentable(ReadOnlySpan<byte> token)
    {
        var text = Encoding.UTF8.GetString(token);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return true;
        }

        return Normalise(text) == Normalise(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The exact decimal value of a JSON number literal as (negative, significant digits, exponent), where the value
    /// is <c>±digits × 10^exponent</c> and <c>digits</c> carries no leading or trailing zero. Zero normalises to
    /// (false, "", 0), so <c>-0</c> and <c>0</c> compare equal.
    /// </summary>
    private static (bool Negative, string Digits, long Exponent) Normalise(ReadOnlySpan<char> literal)
    {
        var index = 0;
        var negative = false;
        if (index < literal.Length && literal[index] == '-')
        {
            negative = true;
            index++;
        }

        var integerStart = index;
        while (index < literal.Length && char.IsAsciiDigit(literal[index]))
        {
            index++;
        }

        var integer = literal[integerStart..index];
        var fraction = ReadOnlySpan<char>.Empty;
        var exponent = 0L;

        if (index < literal.Length && literal[index] == '.')
        {
            index++;
            var fractionStart = index;
            while (index < literal.Length && char.IsAsciiDigit(literal[index]))
            {
                index++;
            }

            fraction = literal[fractionStart..index];
            exponent = -fraction.Length;
        }

        if (index < literal.Length && (literal[index] == 'e' || literal[index] == 'E'))
        {
            index++;
            var exponentNegative = false;
            if (index < literal.Length && (literal[index] == '+' || literal[index] == '-'))
            {
                exponentNegative = literal[index] == '-';
                index++;
            }

            var magnitude = 0L;
            while (index < literal.Length && char.IsAsciiDigit(literal[index]))
            {
                magnitude = Math.Min((magnitude * 10) + (literal[index] - '0'), 1_000_000_000L);
                index++;
            }

            exponent += exponentNegative ? -magnitude : magnitude;
        }

        var digits = string.Concat(integer, fraction);
        var start = 0;
        while (start < digits.Length && digits[start] == '0')
        {
            start++;
        }

        var end = digits.Length;
        while (end > start && digits[end - 1] == '0')
        {
            end--;
            exponent++;
        }

        return start == end ? (false, string.Empty, 0L) : (negative, digits[start..end], exponent);
    }
}
