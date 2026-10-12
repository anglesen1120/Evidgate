namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>The media-sniff outcome (06.3.1 AC5). <c>NotSniffed</c> fails closed once 06.3.1-T2.2 completes the table.</summary>
internal enum SniffResult
{
    /// <summary>The leading bytes match the declared media type.</summary>
    Match,

    /// <summary>The leading bytes contradict the declared media type.</summary>
    Mismatch,

    /// <summary>The profile does not sniff this declared type yet (06.3.1-T2.2 adds the remaining magic bytes).</summary>
    NotSniffed,
}

/// <summary>
/// The T1 media sniffer (06.3.1 §5, 00 §5.7 P1): the declared media type decides which magic bytes are
/// expected, and the leading bytes of the object must agree. T1 knows the XML family
/// (<c>application/xml</c>, <c>text/xml</c>, <c>application/*+xml</c>), the JSON family
/// (<c>application/json</c>, <c>application/*+json</c>) and <c>text/html</c>; every other declared type
/// answers <see cref="SniffResult.NotSniffed"/> and 06.3.1-T2.2 completes the table (PNG/JPEG/MP4/zip) and
/// makes <c>NotSniffed</c> fail closed at <c>complete</c>.
/// <para>
/// The check is pure and allocation-free: it walks a <see cref="ReadOnlySpan{T}"/> of at most
/// <see cref="ParsingLimits.SniffPrefixBytes"/> bytes, never throws and never puts a byte into anything it
/// returns. HTML is never parsed — this sniff is the only thing the server does with it (06.3.1 §10 T1.2).
/// </para>
/// </summary>
internal static class MediaSniffer
{
    private const string XmlSuffix = "+xml";
    private const string JsonSuffix = "+json";
    private const string XmlType = "application/xml";
    private const string TextXmlType = "text/xml";
    private const string HtmlType = "text/html";
    private const string ApplicationPrefix = "application/";

    /// <summary>
    /// Checks the leading bytes of one object against its declared media type.
    /// </summary>
    /// <param name="declaredMediaType">The declared <c>media_type</c>; parameters after <c>;</c> are ignored.</param>
    /// <param name="prefix">The object's leading bytes.</param>
    /// <returns>The sniff verdict; <see cref="SniffResult.NotSniffed"/> for a type outside the T1 table.</returns>
    public static SniffResult Check(string declaredMediaType, ReadOnlySpan<byte> prefix)
    {
        if (string.IsNullOrWhiteSpace(declaredMediaType))
        {
            return SniffResult.NotSniffed;
        }

        var bare = BareType(declaredMediaType);

        if (IsXml(bare))
        {
            return CheckXml(prefix);
        }

        if (IsJson(bare))
        {
            return CheckJson(prefix);
        }

        return bare.Equals(HtmlType, StringComparison.OrdinalIgnoreCase)
            ? CheckHtml(prefix)
            : SniffResult.NotSniffed;
    }

    /// <summary>The bare type: the parameters after the first <c>;</c> are dropped, surrounding whitespace trimmed.</summary>
    private static string BareType(string declaredMediaType)
    {
        var semicolon = declaredMediaType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon < 0 ? declaredMediaType : declaredMediaType[..semicolon]).Trim();
    }

    private static bool IsXml(string bare) =>
        bare.Equals(XmlType, StringComparison.OrdinalIgnoreCase)
        || bare.Equals(TextXmlType, StringComparison.OrdinalIgnoreCase)
        || (bare.StartsWith(ApplicationPrefix, StringComparison.OrdinalIgnoreCase)
            && bare.EndsWith(XmlSuffix, StringComparison.OrdinalIgnoreCase));

    private static bool IsJson(string bare) =>
        bare.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || (bare.StartsWith(ApplicationPrefix, StringComparison.OrdinalIgnoreCase)
            && bare.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// XML must start with <c>&lt;</c> then the declaration (<c>?xml</c>), a comment (<c>!--</c>), a
    /// <c>DOCTYPE</c> declaration (a legitimate prologue of an XHTML report; the parser profile refuses the
    /// DTD itself, so the sniff only has to know the bytes are markup) or an element name start.
    /// </summary>
    private static SniffResult CheckXml(ReadOnlySpan<byte> prefix)
    {
        var rest = Trim(prefix);
        if (rest.Length < 2 || rest[0] != (byte)'<')
        {
            return SniffResult.Mismatch;
        }

        var marker = rest[1];

        if (marker == (byte)'?')
        {
            return rest.Length >= 5 && AsciiStartsWith(rest[2..], "xml")
                ? SniffResult.Match
                : SniffResult.Mismatch;
        }

        if (marker == (byte)'!')
        {
            return IsCommentStart(rest) || IsDoctypeStart(rest) ? SniffResult.Match : SniffResult.Mismatch;
        }

        return IsNameStart(marker) ? SniffResult.Match : SniffResult.Mismatch;
    }

    /// <summary>
    /// HTML must start with <c>&lt;</c> then a tag start (a letter, <c>!</c> for a doctype/comment or
    /// <c>?</c> for a processing instruction); the markup is never parsed, only sniffed.
    /// </summary>
    private static SniffResult CheckHtml(ReadOnlySpan<byte> prefix)
    {
        var rest = Trim(prefix);
        if (rest.Length < 2 || rest[0] != (byte)'<')
        {
            return SniffResult.Mismatch;
        }

        var marker = rest[1];

        return marker == (byte)'!' || marker == (byte)'?' || IsAsciiLetter(marker)
            ? SniffResult.Match
            : SniffResult.Mismatch;
    }

    /// <summary>
    /// JSON must start with <c>{</c>, <c>[</c>, <c>"</c>, a number or one of the three literals; a literal
    /// only counts when it is a whole token (the prefix ends there or the next byte closes it), so
    /// <c>falsey</c> contradicts a declared JSON type.
    /// </summary>
    private static SniffResult CheckJson(ReadOnlySpan<byte> prefix)
    {
        var rest = Trim(prefix);
        if (rest.Length == 0)
        {
            return SniffResult.Mismatch;
        }

        var first = rest[0];

        if (first is (byte)'{' or (byte)'[' or (byte)'"' or (byte)'-' || IsAsciiDigit(first))
        {
            return SniffResult.Match;
        }

        return MatchesLiteral(rest, "true") || MatchesLiteral(rest, "false") || MatchesLiteral(rest, "null")
            ? SniffResult.Match
            : SniffResult.Mismatch;
    }

    /// <summary>True when the prefix starts with the whole literal token <paramref name="literal"/>.</summary>
    private static bool MatchesLiteral(ReadOnlySpan<byte> rest, string literal) =>
        AsciiStartsWith(rest, literal)
        && (rest.Length == literal.Length || IsTokenBoundary(rest[literal.Length]));

    /// <summary>
    /// A byte that may follow a JSON literal: whitespace, or one of the closing/separating characters
    /// (<c>,</c>, <c>]</c>, <c>}</c>, <c>)</c>).
    /// </summary>
    private static bool IsTokenBoundary(byte value) =>
        IsAsciiWhitespace(value) || value is (byte)',' or (byte)']' or (byte)'}' or (byte)')';

    /// <summary>True when the markup starts an XML comment (<c>&lt;!--</c>).</summary>
    private static bool IsCommentStart(ReadOnlySpan<byte> rest) =>
        rest.Length >= 4 && rest[2] == (byte)'-' && rest[3] == (byte)'-';

    /// <summary>
    /// True when the markup starts a <c>&lt;!DOCTYPE</c> declaration, case-insensitively. A prefix that ends
    /// inside the keyword is accepted: the object's first bytes agree with the declared type, and the parser
    /// profile — not the sniff — is what refuses the DTD.
    /// </summary>
    private static bool IsDoctypeStart(ReadOnlySpan<byte> rest)
    {
        ReadOnlySpan<byte> keyword = "DOCTYPE"u8;
        var available = Math.Min(rest.Length - 2, keyword.Length);

        return available > 0 && AsciiStartsWithIgnoreCase(rest[2..(2 + available)], keyword[..available]);
    }

    /// <summary>Drops a leading UTF-8 BOM and the ASCII whitespace before the first significant byte.</summary>
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> prefix)
    {
        var start = 0;
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            start = 3;
        }

        while (start < prefix.Length && IsAsciiWhitespace(prefix[start]))
        {
            start++;
        }

        return prefix[start..];
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsAsciiLetter(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static bool IsAsciiDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    /// <summary>An XML name start: an ASCII letter, <c>_</c>, <c>:</c> or any non-ASCII byte (a UTF-8 lead byte).</summary>
    private static bool IsNameStart(byte value) =>
        IsAsciiLetter(value) || value is (byte)'_' or (byte)':' || value >= 0x80;

    private static bool AsciiStartsWith(ReadOnlySpan<byte> value, string expected)
    {
        if (value.Length < expected.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (value[index] != (byte)expected[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Case-insensitive ASCII prefix comparison, for the one XML keyword that is not lowercase.</summary>
    private static bool AsciiStartsWithIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            if (ToAsciiUpper(value[index]) != ToAsciiUpper(expected[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToAsciiUpper(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;
}
