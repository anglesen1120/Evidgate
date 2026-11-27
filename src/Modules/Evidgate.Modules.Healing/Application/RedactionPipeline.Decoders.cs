using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace Evidgate.Modules.Healing.Application;

internal sealed partial class RedactionPipeline
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Seven characters is the base64 of five bytes, the shortest text a rule matches ("key=a", "a@b.c"). Only tokens
    // that decode to valid UTF-8 count against the budget, which a word of prose rarely does.
    private static readonly Regex Base64Token = new(
        @"[A-Za-z0-9+/_\-]{7,}={0,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // Ten digits are five bytes, the same shortest match. Runs are read from both digit alignments.
    private static readonly Regex HexRun = new(
        "[0-9A-Fa-f]{10,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // \uXXXX, \u{X..}, \UXXXXXXXX and \xXX, the spellings of JavaScript, JSON, Python, C and Rust.
    private static readonly Regex CharacterEscape = new(
        @"\\(?:u([0-9a-fA-F]{4})|u\{([0-9a-fA-F]{1,8})\}|U([0-9a-fA-F]{8})|x([0-9a-fA-F]{2}))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex JsonEscape = new(
        @"\\([""\\/nrtbf])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // Each decoder returns null when it changes nothing. The same list feeds the pass-two scan and the seam check, so
    // a form the scan reads is a form the window cut cannot split unseen.
    private static readonly (string Label, Func<string, string?> Decode)[] TextDecoders =
    [
        ("residual_urlencoded", DecodePercent),
        ("residual_unicode_escape", DecodeCharacterEscapes),
        ("residual_json_escape", DecodeJsonEscapes),
        ("residual_plus_space", DecodePlus),
        ("residual_html_entity", DecodeEntities),
        ("residual_normalised", text => Normalised(text, InvisibleReading.Strip)),
        ("residual_normalised", text => Normalised(text, InvisibleReading.SpaceRuns)),
        ("residual_normalised", text => Normalised(text, InvisibleReading.Mixed)),
    ];

    /// <summary>The forms of <paramref name="text"/> that differ from it after one decoding step.</summary>
    private static IEnumerable<(string Text, string Label)> DecodedVariants(string text)
    {
        foreach (var (label, decode) in TextDecoders)
        {
            if (decode(text) is { } decoded)
            {
                yield return (decoded, label);
            }
        }
    }

    /// <summary>True when one more decoding step (any decoder, a base64 or a hex token) would change <paramref name="text"/>.</summary>
    private static bool DecodesFurther(string text, ScanBudget budget) =>
        DecodedVariants(text).Any() || Base64Texts(text, budget).Count > 0 || HexTexts(text, budget).Count > 0;

    private static string? Changed(string decoded, string text) =>
        string.Equals(decoded, text, StringComparison.Ordinal) ? null : decoded;

    private static string? DecodePercent(string text) =>
        text.Contains('%') ? Changed(Uri.UnescapeDataString(text), text) : null;

    private static string? DecodeCharacterEscapes(string text) =>
        text.Contains('\\') ? Changed(CharacterEscape.Replace(text, DecodeCharacterEscape), text) : null;

    private static string? DecodeJsonEscapes(string text) =>
        text.Contains('\\')
            ? Changed(
                JsonEscape.Replace(text, match => match.Groups[1].Value[0] switch
                {
                    'n' => "\n",
                    't' => "\t",
                    'r' => "\r",
                    'b' => "\b",
                    'f' => "\f",
                    var literal => literal.ToString(),
                }),
                text)
            : null;

    // A form-encoded body writes a space as '+', and so does a query string, where "Bearer+token" is a header value.
    private static string? DecodePlus(string text) => text.Contains('+') ? text.Replace('+', ' ') : null;

    private static string? DecodeEntities(string text) =>
        text.Contains('&') ? Changed(WebUtility.HtmlDecode(text), text) : null;

    // A surrogate half spelled \uD83D stays one char so a pair spelled \uD83D\uDE00 joins; a braced, long or
    // out-of-range value that is not a scalar is kept as written.
    private static string DecodeCharacterEscape(Match match)
    {
        var groups = match.Groups;
        if (groups[1].Success)
        {
            return ((char)int.Parse(groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)).ToString();
        }

        if (groups[4].Success)
        {
            return ((char)int.Parse(groups[4].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)).ToString();
        }

        var digits = groups[2].Success ? groups[2].Value : groups[3].Value;
        var value = long.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return value is > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) ? match.Value : char.ConvertFromUtf32((int)value);
    }

    /// <summary>
    /// A prompt text has its ampersands entity-escaped, and a value rule may stop at the <c>;</c> of an entity and leave
    /// the rest of the value. The window read with entities decoded must mask the same spans as the masked window does.
    /// (The entity-decoded form is also a decoded variant, so it composes under the other layers.)
    /// </summary>
    private static void ScanEntities(string original, string masked, ScanBudget budget, Dictionary<string, int> hits)
    {
        if (budget.Check() || !original.Contains('&'))
        {
            return;
        }

        var decoded = WebUtility.HtmlDecode(original);
        if (string.Equals(decoded, original, StringComparison.Ordinal))
        {
            return;
        }

        var maskedDecoded = WebUtility.HtmlDecode(masked);
        var expected = RedactionRules.Apply(decoded, new Dictionary<string, int>());
        if (!string.Equals(expected, maskedDecoded, StringComparison.Ordinal))
        {
            AddHit(hits, "residual_html_entity", 1);
        }
    }

    /// <summary>The tokens of <paramref name="text"/> that decode to valid UTF-8 text, within the window's budget.</summary>
    private static List<string> Base64Texts(string text, ScanBudget budget)
    {
        var texts = new List<string>();
        foreach (var match in Base64Token.EnumerateMatches(text))
        {
            if (budget.Check())
            {
                break;
            }

            if (TryDecode(text.AsSpan(match.Index, match.Length)) is not { } decoded)
            {
                continue;
            }

            if (!TryCount(budget))
            {
                break;
            }

            texts.Add(decoded);
        }

        return texts;
    }

    /// <summary>The hex runs of <paramref name="text"/> that decode to valid UTF-8 text, within the window's budget.</summary>
    private static List<string> HexTexts(string text, ScanBudget budget)
    {
        var texts = new List<string>();
        foreach (var match in HexRun.EnumerateMatches(text))
        {
            for (var offset = 0; offset < 2; offset++)
            {
                if (budget.Check())
                {
                    return texts;
                }

                var digits = text.AsSpan(match.Index + offset, match.Length - offset);
                digits = digits[..(digits.Length & ~1)];
                if (digits.Length < 10 || TryDecodeHex(digits) is not { } decoded)
                {
                    continue;
                }

                if (!TryCount(budget))
                {
                    return texts;
                }

                texts.Add(decoded);
            }
        }

        return texts;
    }

    private static bool TryCount(ScanBudget budget)
    {
        if (++budget.DecodedTokens <= MaxBase64TokensPerWindow)
        {
            return true;
        }

        budget.Truncated = true;
        return false;
    }

    private static string? TryDecodeHex(ReadOnlySpan<char> digits)
    {
        var bytes = Convert.FromHexString(digits);
        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static string? TryDecode(ReadOnlySpan<char> token)
    {
        var normalised = new StringBuilder(token.Length + 3);
        foreach (var c in token)
        {
            _ = c switch
            {
                '-' => normalised.Append('+'),
                '_' => normalised.Append('/'),
                '=' => normalised,
                _ => normalised.Append(c),
            };
        }

        if (normalised.Length % 4 == 1)
        {
            return null;
        }

        normalised.Append('=', (4 - (normalised.Length % 4)) % 4);
        var bytes = new byte[(normalised.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(normalised.ToString(), bytes, out var written))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
