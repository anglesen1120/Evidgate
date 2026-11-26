namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>
/// The media-sniff outcome (06.3.1 AC5). There are exactly two: a declared type either matches the leading
/// bytes or it does not. 06.3.1-T2.2 removed the T1 <c>NotSniffed</c> verdict — a declared type the profile has
/// no signature for is a <see cref="Mismatch"/> (fail closed), so no artifact can become <c>ready</c> on a type
/// the sniffer cannot check.
/// </summary>
internal enum SniffResult
{
    /// <summary>The leading bytes match the declared media type.</summary>
    Match,

    /// <summary>The leading bytes contradict the declared media type, or the profile holds no signature for it.</summary>
    Mismatch,
}

/// <summary>
/// The media sniffer (06.3.1 §5, 00 §5.7 P1): the declared media type decides which magic bytes are expected, and
/// the leading bytes of the object must agree. The table is complete for every media type the product accepts
/// (06.3.1-T2.2) — the XML family (<c>application/xml</c>, <c>text/xml</c>, <c>application/*+xml</c> in UTF-8,
/// UTF-16 or UTF-32 by BOM), the JSON family (<c>application/json</c>, <c>application/*+json</c>),
/// <c>text/html</c>, <c>application/zip</c>, <c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>,
/// <c>video/mp4</c>, <c>application/pdf</c> and the DOCX type — and a declared type outside it is a
/// <see cref="SniffResult.Mismatch"/> (fail closed). <c>image/svg+xml</c>, <c>text/plain</c>,
/// <c>application/octet-stream</c> and every other type are therefore explicitly rejected rather than waved
/// through.
/// <para>
/// The check is pure and allocation-free: it walks a <see cref="ReadOnlySpan{T}"/> of at most
/// <see cref="ParsingLimits.SniffPrefixBytes"/> bytes, never throws and never puts a byte into anything it
/// returns. HTML is never parsed — this sniff is the only thing the server does with it (06.3.1 §10 T1.2).
/// </para>
/// </summary>
internal static partial class MediaSniffer
{
    private const string XmlSuffix = "+xml";
    private const string JsonSuffix = "+json";
    private const string XmlType = "application/xml";
    private const string TextXmlType = "text/xml";
    private const string HtmlType = "text/html";
    private const string ZipType = "application/zip";
    private const string PngType = "image/png";
    private const string JpegType = "image/jpeg";
    private const string Mp4Type = "video/mp4";
    private const string WebpType = "image/webp";
    private const string PdfType = "application/pdf";
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string ApplicationPrefix = "application/";

    /// <summary>
    /// Checks the leading bytes of one object against its declared media type. A declared type the table has no
    /// signature for is a <see cref="SniffResult.Mismatch"/>: the profile fails closed, so a type outside the
    /// table can never become evidence (06.3.1-T2.2).
    /// </summary>
    /// <param name="declaredMediaType">The declared <c>media_type</c>; parameters after <c>;</c> are ignored.</param>
    /// <param name="prefix">The object's leading bytes.</param>
    /// <returns><see cref="SniffResult.Match"/> or <see cref="SniffResult.Mismatch"/>.</returns>
    public static SniffResult Check(string declaredMediaType, ReadOnlySpan<byte> prefix)
    {
        if (string.IsNullOrWhiteSpace(declaredMediaType))
        {
            return SniffResult.Mismatch;
        }

        var bare = BareType(declaredMediaType.AsSpan());

        if (IsXml(bare))
        {
            return CheckXml(prefix);
        }

        if (IsJson(bare))
        {
            return CheckJson(prefix);
        }

        if (bare.Equals(HtmlType.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return CheckHtml(prefix);
        }

        return CheckBinary(bare, prefix);
    }

    /// <summary>
    /// The binary types. Anything not listed is a mismatch (fail closed): image/svg+xml, text/plain,
    /// application/octet-stream and every policy allow-list entry with no signature land here.
    /// </summary>
    private static SniffResult CheckBinary(ReadOnlySpan<char> bare, ReadOnlySpan<byte> prefix)
    {
        if (Is(bare, ZipType))
        {
            return CheckZip(prefix);
        }

        if (Is(bare, DocxType))
        {
            return CheckDocx(prefix);
        }

        if (Is(bare, PdfType))
        {
            return CheckPdf(prefix);
        }

        if (Is(bare, PngType))
        {
            return CheckPng(prefix);
        }

        if (Is(bare, JpegType))
        {
            return CheckJpeg(prefix);
        }

        if (Is(bare, WebpType))
        {
            return CheckWebp(prefix);
        }

        return Is(bare, Mp4Type) ? CheckMp4(prefix) : SniffResult.Mismatch;
    }

    private static bool Is(ReadOnlySpan<char> bare, string type) =>
        bare.Equals(type.AsSpan(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the profile holds a signature for <paramref name="declaredMediaType"/> (06.3.1-T2.2), so
    /// <see cref="Check"/> can decide a Match or a Mismatch from the leading bytes. A declared type with no
    /// signature is a Mismatch by definition, so the caller can refuse the artifact without reading any bytes at
    /// all (00 §5.7 P1: the <c>complete</c> hot path never issues a ranged GET it cannot use). The normalisation
    /// is the one <see cref="Check"/> applies — parameters after <c>;</c> dropped, surrounding whitespace trimmed,
    /// case-insensitive — so the two members always agree on which type is in the table.
    /// </summary>
    /// <param name="declaredMediaType">The declared <c>media_type</c>.</param>
    /// <returns>True when the table holds a signature for the bare type.</returns>
    public static bool HasSignature(string declaredMediaType)
    {
        if (string.IsNullOrWhiteSpace(declaredMediaType))
        {
            return false;
        }

        var bare = BareType(declaredMediaType.AsSpan());

        return IsXml(bare)
            || IsJson(bare)
            || bare.Equals(HtmlType.AsSpan(), StringComparison.OrdinalIgnoreCase)
            || Is(bare, ZipType)
            || Is(bare, DocxType)
            || Is(bare, PdfType)
            || Is(bare, PngType)
            || Is(bare, JpegType)
            || Is(bare, WebpType)
            || Is(bare, Mp4Type);
    }

    /// <summary>The bare type: the parameters after the first <c>;</c> are dropped, surrounding whitespace trimmed.</summary>
    private static ReadOnlySpan<char> BareType(ReadOnlySpan<char> declaredMediaType)
    {
        var semicolon = declaredMediaType.IndexOf(';');
        return (semicolon < 0 ? declaredMediaType : declaredMediaType[..semicolon]).Trim();
    }

    private static bool IsXml(ReadOnlySpan<char> bare) =>
        bare.Equals(XmlType.AsSpan(), StringComparison.OrdinalIgnoreCase)
        || bare.Equals(TextXmlType.AsSpan(), StringComparison.OrdinalIgnoreCase)
        || (bare.StartsWith(ApplicationPrefix.AsSpan(), StringComparison.OrdinalIgnoreCase)
            && bare.EndsWith(XmlSuffix.AsSpan(), StringComparison.OrdinalIgnoreCase));

    private static bool IsJson(ReadOnlySpan<char> bare) =>
        bare.Equals("application/json".AsSpan(), StringComparison.OrdinalIgnoreCase)
        || (bare.StartsWith(ApplicationPrefix.AsSpan(), StringComparison.OrdinalIgnoreCase)
            && bare.EndsWith(JsonSuffix.AsSpan(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// XML must start with <c>&lt;</c> then the declaration (<c>?xml</c>), a comment (<c>!--</c>), a
    /// <c>DOCTYPE</c> declaration (a legitimate prologue of an XHTML report; the parser profile refuses the DTD
    /// itself, so the sniff only has to know the bytes are markup) or an element name start. The XML family is
    /// recognised in UTF-8 (with or without a BOM) and in UTF-16/UTF-32 named by their BOM (06.3.1-T2.2): a JUnit
    /// report may be UTF-16 (04.4.1 AC8), and its markup is read in the encoding the BOM names, so a declared
    /// <c>application/xml</c> still matches.
    /// </summary>
    private static SniffResult CheckXml(ReadOnlySpan<byte> prefix)
    {
        var bom = DetectBom(prefix);

        if (bom.Width != 1)
        {
            return CheckEncodedXml(prefix[bom.Offset..], bom.Width, bom.LittleEndian);
        }

        var rest = Trim(prefix[bom.Offset..]);
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
    /// The XML prologue check for a UTF-16/UTF-32 document whose BOM named the width and endianness: the code
    /// units are read in that encoding, so a wide-encoded <c>&lt;?xml</c>, comment, <c>DOCTYPE</c> or root element
    /// start matches exactly as its UTF-8 form does. Bounded: only the leading bytes of the prefix are ever read,
    /// and a prefix cut mid-code-unit is a mismatch (fail closed).
    /// </summary>
    private static SniffResult CheckEncodedXml(ReadOnlySpan<byte> body, int width, bool littleEndian)
    {
        var unit = ReadUnit(ref body, width, littleEndian);
        while (unit >= 0 && IsWhitespace(unit))
        {
            unit = ReadUnit(ref body, width, littleEndian);
        }

        if (unit != '<')
        {
            return SniffResult.Mismatch;
        }

        var marker = ReadUnit(ref body, width, littleEndian);

        if (marker == '?')
        {
            return MatchesWord(body, width, littleEndian, "xml") ? SniffResult.Match : SniffResult.Mismatch;
        }

        if (marker == '!')
        {
            return IsEncodedCommentStart(body, width, littleEndian) || IsEncodedDoctypeStart(body, width, littleEndian)
                ? SniffResult.Match
                : SniffResult.Mismatch;
        }

        return IsNameStartUnit(marker) ? SniffResult.Match : SniffResult.Mismatch;
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
    /// <c>falsey</c> contradicts a declared JSON type. Only UTF-8 (with or without a BOM) is recognised for the
    /// JSON family: the profile's JSON parser reads UTF-8 bytes (<c>SafeJson</c>, 06.3.1-T1), so a wide-encoded
    /// body is a <see cref="SniffResult.Mismatch"/> rather than a pass the parser could not honour — unlike the
    /// XML family, whose parser accepts a BOM-named UTF-16/UTF-32 document.
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

    /// <summary>
    /// A zip must start with a local file header (<c>PK\x03\x04</c>) or an end-of-central-directory record
    /// (<c>PK\x05\x06</c>, an empty archive). Both are exact four-byte signatures; the sniffer never parses the
    /// archive (<c>SafeZipArchive</c> owns that, 06.3.1-T2.1).
    /// </summary>
    private static SniffResult CheckZip(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 4
        && prefix[0] == (byte)'P'
        && prefix[1] == (byte)'K'
        && ((prefix[2] == 0x03 && prefix[3] == 0x04) || (prefix[2] == 0x05 && prefix[3] == 0x06))
            ? SniffResult.Match
            : SniffResult.Mismatch;

    /// <summary>The PNG signature: <c>89 50 4E 47 0D 0A 1A 0A</c>, all eight bytes exact.</summary>
    private static SniffResult CheckPng(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 8
        && prefix[0] == 0x89 && prefix[1] == 0x50 && prefix[2] == 0x4E && prefix[3] == 0x47
        && prefix[4] == 0x0D && prefix[5] == 0x0A && prefix[6] == 0x1A && prefix[7] == 0x0A
            ? SniffResult.Match
            : SniffResult.Mismatch;

    /// <summary>JPEG must start with the SOI marker (<c>FF D8</c>) followed by another marker byte (<c>FF</c>).</summary>
    private static SniffResult CheckJpeg(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 3 && prefix[0] == 0xFF && prefix[1] == 0xD8 && prefix[2] == 0xFF
            ? SniffResult.Match
            : SniffResult.Mismatch;

    /// <summary>
    /// A DOCX is a zip that begins with a local file header: <c>PK\x03\x04</c> only. The empty-archive record
    /// (<c>PK\x05\x06</c>) is no document, so unlike <c>application/zip</c> it is a mismatch.
    /// </summary>
    private static SniffResult CheckDocx(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 4 && prefix[0] == (byte)'P' && prefix[1] == (byte)'K' && prefix[2] == 0x03 && prefix[3] == 0x04
            ? SniffResult.Match
            : SniffResult.Mismatch;

    /// <summary>A PDF starts with the five bytes <c>%PDF-</c> at offset 0 (no leading junk is tolerated).</summary>
    private static SniffResult CheckPdf(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 5
        && prefix[0] == (byte)'%' && prefix[1] == (byte)'P' && prefix[2] == (byte)'D' && prefix[3] == (byte)'F'
        && prefix[4] == (byte)'-'
            ? SniffResult.Match
            : SniffResult.Mismatch;

    /// <summary>WebP is a RIFF container: <c>RIFF</c>, a four-byte size, then <c>WEBP</c> at offset 8.</summary>
    private static SniffResult CheckWebp(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 12
        && prefix[0] == (byte)'R' && prefix[1] == (byte)'I' && prefix[2] == (byte)'F' && prefix[3] == (byte)'F'
        && prefix[8] == (byte)'W' && prefix[9] == (byte)'E' && prefix[10] == (byte)'B' && prefix[11] == (byte)'P'
            ? SniffResult.Match
            : SniffResult.Mismatch;

    /// <summary>
    /// An ISO-BMFF/MP4 file starts with a <c>ftyp</c> box: a big-endian 32-bit box size, the four bytes
    /// <c>ftyp</c> at offset 4 and a four-byte major brand. The size must be plausible for a box that carries its
    /// own header (>= 8) and not absurd (> 16 MiB), and the brand must be printable ASCII, so a random 12-byte
    /// prefix is not accepted (no over-match).
    /// </summary>
    private static SniffResult CheckMp4(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 12
            || prefix[4] != (byte)'f' || prefix[5] != (byte)'t' || prefix[6] != (byte)'y' || prefix[7] != (byte)'p')
        {
            return SniffResult.Mismatch;
        }

        var size = ((uint)prefix[0] << 24) | ((uint)prefix[1] << 16) | ((uint)prefix[2] << 8) | prefix[3];
        if (size < 8u || size > 0x0100_0000u)
        {
            return SniffResult.Mismatch;
        }

        for (var index = 8; index < 12; index++)
        {
            if (prefix[index] is < 0x20 or > 0x7E)
            {
                return SniffResult.Mismatch;
            }
        }

        return SniffResult.Match;
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
}
