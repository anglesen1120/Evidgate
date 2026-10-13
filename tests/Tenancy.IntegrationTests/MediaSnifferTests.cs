using System.Text;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T2.2 (AC5; 00 §5.7 P1): the media sniff over EVERY media type the product accepts — the XML family
/// (UTF-8, UTF-16 and UTF-32 by BOM), the JSON family, <c>text/html</c>, <c>application/zip</c>,
/// <c>image/png</c>, <c>image/jpeg</c> and <c>video/mp4</c>. Every other declared type is a
/// <see cref="SniffResult.Mismatch"/> (fail closed): <c>image/svg+xml</c>, <c>text/plain</c>,
/// <c>application/octet-stream</c>, <c>application/pdf</c> and everything else can never become evidence. The T1
/// <c>NotSniffed</c> verdict and the <c>IsSniffed</c> skip-the-read optimisation are gone.
/// <para>
/// Prefixes are written as Latin-1 strings so a binary prefix (<c>\u0089PNG</c>) is readable inline. The sniff
/// is pure: it never throws, never allocates and never returns a byte of the prefix.
/// </para>
/// </summary>
public sealed class MediaSnifferTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] EmptyZipMagic = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] Mp4Magic =
        [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

    [Theory]
    [InlineData("application/xml", "<?xml version=\"1.0\"?><testsuites/>")]
    [InlineData("application/xml", "<testsuites name=\"s\">")]
    [InlineData("application/xml", "<!-- a comment --><r/>")]
    [InlineData("application/xml", "  \r\n\t<r/>")]
    [InlineData("application/xml", "\u00EF\u00BB\u00BF<?xml version=\"1.0\"?>")]
    [InlineData("text/xml", "<r/>")]
    [InlineData("application/atom+xml", "<feed xmlns=\"http://www.w3.org/2005/Atom\"/>")]
    [InlineData("application/xhtml+xml", "<!DOCTYPE html><html/>")]
    [InlineData("APPLICATION/XML", "<r/>")]
    [InlineData("application/xml; charset=utf-8", "<?xml version=\"1.0\"?>")]
    [InlineData("text/xml;charset=UTF-8", "<r/>")]
    [InlineData("application/xml", "<_r/>")]
    [InlineData("application/xml", "<\u00E9l\u00E9ment/>")]
    public void DeclaredXmlFamilyMatchesXmlLookingPrefixes(string declared, string prefix) =>
        Sniff(declared, prefix).ShouldBe(SniffResult.Match);

    [Theory]
    [InlineData("application/xml", "{\"a\":1}")]
    [InlineData("application/xml", "\u0089PNG\r\n\u001A\n")]
    [InlineData("application/xml", "")]
    [InlineData("application/xml", "   ")]
    [InlineData("application/xml", "<")]
    [InlineData("application/xml", "<?xm")]
    [InlineData("application/xml", "<3/>")]
    [InlineData("application/xml", "<!ELEMENT r EMPTY>")]
    [InlineData("text/xml", "plain text")]
    [InlineData("application/atom+xml", "\u001F\u008B\u0008")]
    public void DeclaredXmlFamilyRejectsNonXmlPrefixes(string declared, string prefix) =>
        Sniff(declared, prefix).ShouldBe(SniffResult.Mismatch);

    /// <summary>
    /// 06.3.1-T2.2 (T1 Note 10, 04.4.1 AC8): a UTF-16/UTF-32 XML report is recognised by its BOM, so a JUnit
    /// report encoded wide still matches a declared <c>application/xml</c> instead of being refused.
    /// </summary>
    [Fact]
    public void DeclaredXmlWithUtf16OrUtf32BomMatches()
    {
        const string Declaration = "<?xml version=\"1.0\"?><testsuites/>";

        MediaSniffer.Check("application/xml", Utf16(Declaration, littleEndian: true)).ShouldBe(SniffResult.Match);
        MediaSniffer.Check("application/xml", Utf16(Declaration, littleEndian: false)).ShouldBe(SniffResult.Match);
        MediaSniffer.Check("application/xml", Utf32(Declaration, littleEndian: true)).ShouldBe(SniffResult.Match);
        MediaSniffer.Check("application/xml", Utf32(Declaration, littleEndian: false)).ShouldBe(SniffResult.Match);

        // A wide-encoded element start and a wide-encoded comment/DOCTYPE are markup too.
        MediaSniffer.Check("text/xml", Utf16("<r/>", littleEndian: true)).ShouldBe(SniffResult.Match);
        MediaSniffer.Check("text/xml", Utf16("<!DOCTYPE html><html/>", littleEndian: false)).ShouldBe(SniffResult.Match);
    }

    [Fact]
    public void DeclaredXmlWithAWideBomButNonMarkupIsMismatch()
    {
        // The BOM names an encoding, not a media type: the bytes after it must still be XML.
        MediaSniffer.Check("application/xml", Utf16("{\"a\":1}", littleEndian: true)).ShouldBe(SniffResult.Mismatch);
        MediaSniffer.Check("application/xml", Utf16("plain", littleEndian: false)).ShouldBe(SniffResult.Mismatch);

        // A prefix cut inside a code unit is a mismatch (fail closed), never an exception.
        Sniff("application/xml", "\u00FF\u00FE<").ShouldBe(SniffResult.Mismatch);
    }

    [Theory]
    [InlineData("application/json", "{\"nodes\":[]}")]
    [InlineData("application/json", "[1,2,3]")]
    [InlineData("application/json", "\"a string\"")]
    [InlineData("application/json", "42")]
    [InlineData("application/json", "-0.5")]
    [InlineData("application/json", "true")]
    [InlineData("application/json", "false")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "\n\t{\"a\":1}")]
    [InlineData("application/json", "\u00EF\u00BB\u00BF{\"a\":1}")]
    [InlineData("application/ld+json", "{\"@context\":{}}")]
    [InlineData("application/vnd.api+json", "[{\"type\":\"node\"}]")]
    [InlineData("Application/Json", "{}")]
    [InlineData("application/json; charset=utf-8", "{}")]
    public void DeclaredJsonFamilyMatchesJsonLookingPrefixes(string declared, string prefix) =>
        Sniff(declared, prefix).ShouldBe(SniffResult.Match);

    [Theory]
    [InlineData("application/json", "")]
    [InlineData("application/json", "   ")]
    [InlineData("application/json", "<?xml version=\"1.0\"?>")]
    [InlineData("application/json", "<r/>")]
    [InlineData("application/json", "nul")]
    [InlineData("application/json", "tru")]
    [InlineData("application/json", "falsey")]
    [InlineData("application/json", "x")]
    [InlineData("application/ld+json", "\u0089PNG\r\n\u001A\n")]
    public void DeclaredJsonFamilyRejectsNonJsonPrefixes(string declared, string prefix) =>
        Sniff(declared, prefix).ShouldBe(SniffResult.Mismatch);

    [Theory]
    [InlineData("text/html", "<!DOCTYPE html><html lang=\"en\">")]
    [InlineData("text/html", "<html>")]
    [InlineData("text/html", "<head>")]
    [InlineData("text/html", "<body class=\"x\">")]
    [InlineData("text/html", "<!-- saved from the browser --><html/>")]
    [InlineData("text/html", "<script>window.__STATE__={}</script>")]
    [InlineData("text/html", "  <div id=\"root\">")]
    [InlineData("text/html", "\u00EF\u00BB\u00BF<html/>")]
    [InlineData("TEXT/HTML; charset=utf-8", "<html/>")]
    public void DeclaredHtmlMatchesMarkupLookingPrefixes(string declared, string prefix) =>
        Sniff(declared, prefix).ShouldBe(SniffResult.Match);

    [Theory]
    [InlineData("text/html", "")]
    [InlineData("text/html", "   ")]
    [InlineData("text/html", "{\"a\":1}")]
    [InlineData("text/html", "plain text")]
    [InlineData("text/html", "\u0089PNG\r\n\u001A\n")]
    [InlineData("text/html", "<3")]
    public void DeclaredHtmlRejectsNonMarkupPrefixes(string declared, string prefix) =>
        Sniff(declared, prefix).ShouldBe(SniffResult.Mismatch);

    // ---------------------------------------------------------------- zip (PK local header / empty archive)

    [Fact]
    public void DeclaredZipMatchesALocalHeaderOrAnEmptyArchive()
    {
        MediaSniffer.Check("application/zip", ZipMagic).ShouldBe(SniffResult.Match);
        MediaSniffer.Check("APPLICATION/ZIP; version=2", EmptyZipMagic).ShouldBe(SniffResult.Match);

        // A Playwright trace / report bundle begins with the local header followed by the rest of the entry.
        Sniff("application/zip", "PK\u0003\u0004\u0014\u0000\u0000\u0000").ShouldBe(SniffResult.Match);
    }

    [Theory]
    [InlineData("application/zip", "PK\u0003\u0005")]
    [InlineData("application/zip", "PK")]
    [InlineData("application/zip", "P")]
    [InlineData("application/zip", "")]
    [InlineData("application/zip", "AAAA")]
    [InlineData("application/zip", "\u0089PNG\r\n\u001A\n")]
    public void DeclaredZipRejectsNonZipPrefixes(string declared, string prefix) =>
        Sniff(declared, prefix).ShouldBe(SniffResult.Mismatch);

    // ---------------------------------------------------------------- PNG / JPEG / MP4

    [Fact]
    public void DeclaredPngMatchesOnlyTheExactEightByteSignature()
    {
        MediaSniffer.Check("image/png", PngMagic).ShouldBe(SniffResult.Match);
        Sniff("IMAGE/PNG", "\u0089PNG\r\n\u001A\n\u0000\u0000\u0000\r").ShouldBe(SniffResult.Match);

        MediaSniffer.Check("image/png", PngMagic.AsSpan(..7)).ShouldBe(SniffResult.Mismatch);
        Sniff("image/png", "\u0089PNH\r\n\u001A\n").ShouldBe(SniffResult.Mismatch);
        MediaSniffer.Check("image/png", ZipMagic).ShouldBe(SniffResult.Mismatch);
    }

    [Fact]
    public void DeclaredJpegMatchesOnlyTheSoiMarker()
    {
        MediaSniffer.Check("image/jpeg", JpegMagic).ShouldBe(SniffResult.Match);
        Sniff("image/jpeg", "\u00FF\u00D8\u00FF").ShouldBe(SniffResult.Match);

        Sniff("image/jpeg", "\u00FF\u00D8").ShouldBe(SniffResult.Mismatch);
        Sniff("image/jpeg", "\u00FF\u00D8\u0000").ShouldBe(SniffResult.Mismatch);
        MediaSniffer.Check("image/jpeg", PngMagic).ShouldBe(SniffResult.Mismatch);
    }

    [Fact]
    public void DeclaredMp4MatchesAnFtypBoxWithAPlausibleSizeAndBrand()
    {
        MediaSniffer.Check("video/mp4", Mp4Magic).ShouldBe(SniffResult.Match);
        Sniff("VIDEO/MP4; codecs=avc1", "\u0000\u0000\u0000\u0020ftypM4V\u0020").ShouldBe(SniffResult.Match);
    }

    [Theory]
    // 'ftyp' is present but the box size is implausible (0 = to-EOF marker, 4 < the 8-byte header).
    [InlineData("\u0000\u0000\u0000\u0000ftypisom")]
    [InlineData("\u0000\u0000\u0000\u0004ftypisom")]
    // 'ftyp' is not at offset 4.
    [InlineData("\u0000\u0000\u0000\u0018ftYpisom")]
    [InlineData("ftyp\u0000\u0000\u0000\u0018isom")]
    // The major brand is not printable ASCII.
    [InlineData("\u0000\u0000\u0000\u0018ftyp\u0001\u0002\u0003\u0004")]
    public void DeclaredMp4RejectsImplausibleOrMisplacedFtypBoxes(string prefix) =>
        Sniff("video/mp4", prefix).ShouldBe(SniffResult.Mismatch);

    [Fact]
    public void DeclaredMp4RejectsATruncatedPrefix()
    {
        // Fewer than 12 bytes cannot carry size + 'ftyp' + brand: fail closed, never a partial match.
        MediaSniffer.Check("video/mp4", Mp4Magic.AsSpan(..11)).ShouldBe(SniffResult.Mismatch);
        Sniff("video/mp4", "\u0000\u0000\u0000\u0018ftyp").ShouldBe(SniffResult.Mismatch);
    }

    // ---------------------------------------------------------------- every type is sniffed or rejected

    /// <summary>
    /// 06.3.1-T2.2 fail-closed table: a declared type the profile has no signature for is a
    /// <see cref="SniffResult.Mismatch"/>, never a pass. <c>image/svg+xml</c> (XML served as an image) and
    /// <c>text/plain</c> are explicitly rejected, as is every opaque or unknown type.
    /// </summary>
    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/webp")]
    [InlineData("video/webm")]
    [InlineData("application/pdf")]
    [InlineData("application/octet-stream")]
    [InlineData("text/plain")]
    [InlineData("application/xmlx")]
    [InlineData("application/jsonx")]
    [InlineData("text/xmlish")]
    [InlineData("application/")]
    [InlineData("")]
    [InlineData("   ")]
    public void DeclaredTypesOutsideTheSniffTableAreMismatchesNeverAPass(string declared)
    {
        Sniff(declared, "\u0089PNG\r\n\u001A\n").ShouldBe(SniffResult.Mismatch);
        Sniff(declared, "<?xml version=\"1.0\"?>").ShouldBe(SniffResult.Mismatch);
        Sniff(declared, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>x</script></svg>").ShouldBe(SniffResult.Mismatch);
        Sniff(declared, "").ShouldBe(SniffResult.Mismatch);
    }

    /// <summary>
    /// 06.3.1-T2.2: <see cref="MediaSniffer.HasSignature"/> names exactly the declared types
    /// <see cref="MediaSniffer.Check"/> can decide, so the completion can refuse an unsigned declared type without
    /// issuing a prefix GET — and the two members can never disagree about what is in the table. The normalisation
    /// is the one <see cref="MediaSniffer.Check"/> applies (parameters dropped, trimmed, case-insensitive).
    /// </summary>
    [Theory]
    [InlineData("application/xml", true)]
    [InlineData("text/xml", true)]
    [InlineData("application/atom+xml", true)]
    [InlineData("APPLICATION/XML; charset=utf-8", true)]
    [InlineData("application/json", true)]
    [InlineData("application/ld+json", true)]
    [InlineData("text/html", true)]
    [InlineData("TEXT/HTML; charset=utf-8", true)]
    [InlineData("application/zip", true)]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("video/mp4", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/plain", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("application/pdf", false)]
    [InlineData("application/xmlx", false)]
    [InlineData("application/jsonx", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void HasSignatureAgreesWithTheTable(string declared, bool expected) =>
        MediaSniffer.HasSignature(declared).ShouldBe(expected);

    /// <summary>
    /// 06.3.1-T2.2: a declared type with no signature is a <see cref="SniffResult.Mismatch"/> for EVERY prefix, so
    /// skipping the read for it (the completion's fail-closed shortcut) can never let an artifact through.
    /// </summary>
    [Fact]
    public void AnUnsignedDeclaredTypeIsAlwaysAMismatch()
    {
        foreach (var declared in new[]
                 {
                     "image/svg+xml", "text/plain", "application/octet-stream", "application/pdf",
                     "application/xmlx", string.Empty,
                 })
        {
            MediaSniffer.HasSignature(declared).ShouldBeFalse();
            MediaSniffer.Check(declared, Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?>")).ShouldBe(SniffResult.Mismatch);
            MediaSniffer.Check(declared, PngMagic).ShouldBe(SniffResult.Mismatch);
            MediaSniffer.Check(declared, ReadOnlySpan<byte>.Empty).ShouldBe(SniffResult.Mismatch);
        }
    }

    /// <summary>
    /// Cross-type confusion: a declared type never accepts another type's magic. The two markup types
    /// (<c>application/xml</c>, <c>text/html</c>) share the <c>&lt;</c>-markup signature by design and are
    /// excluded from the pairwise check; every other pair, including the archive/image signatures, is a mismatch.
    /// </summary>
    [Fact]
    public void EverySniffedTypeRejectsEveryOtherTypeMagic()
    {
        var magics = new (string Type, byte[] Magic)[]
        {
            ("application/xml", Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?>")),
            ("text/html", Encoding.ASCII.GetBytes("<html/>")),
            ("application/json", Encoding.ASCII.GetBytes("{\"a\":1}")),
            ("application/zip", ZipMagic),
            ("image/png", PngMagic),
            ("image/jpeg", JpegMagic),
            ("video/mp4", Mp4Magic),
        };

        for (var declaredIndex = 0; declaredIndex < magics.Length; declaredIndex++)
        {
            for (var foreignIndex = 0; foreignIndex < magics.Length; foreignIndex++)
            {
                if (declaredIndex == foreignIndex || (IsMarkup(magics[declaredIndex].Type) && IsMarkup(magics[foreignIndex].Type)))
                {
                    continue;
                }

                MediaSniffer.Check(magics[declaredIndex].Type, magics[foreignIndex].Magic).ShouldBe(
                    SniffResult.Mismatch,
                    $"{magics[declaredIndex].Type} must not accept {magics[foreignIndex].Type} magic");
            }
        }

        static bool IsMarkup(string type) => type is "application/xml" or "text/html";
    }

    [Fact]
    public void NeverThrowsOnAHostilePrefix()
    {
        // A prefix of arbitrary bytes must produce a verdict, not an exception, for every declared type.
        var hostile = new byte[ParsingLimits.SniffPrefixBytes];
        Array.Fill(hostile, (byte)0xFF);

        foreach (var declared in new[]
                 {
                     "application/xml", "text/xml", "application/atom+xml", "application/json",
                     "application/ld+json", "text/html", "application/zip", "image/png", "image/jpeg",
                     "video/mp4", "image/svg+xml", "text/plain", string.Empty,
                 })
        {
            var verdict = new ParsingService().SniffMediaType(declared, hostile);
            verdict.ShouldBeOneOf(MediaSniffVerdict.Mismatch, MediaSniffVerdict.Match);
        }
    }

    [Fact]
    public void SniffRunsOnAtMostTheSniffPrefixBytes()
    {
        // The complete hot path reads 4 KiB (00 §5.7 P1): the sniffer is a pure span walk, so the cap is a
        // property of the caller's read, and the constant it uses is the one the object store is asked for.
        ParsingLimits.SniffPrefixBytes.ShouldBe(4096);

        var exactlyOnePage = new byte[ParsingLimits.SniffPrefixBytes];
        Encoding.ASCII.GetBytes("<html><head></head><body>").CopyTo(exactlyOnePage, 0);

        SniffBytes("text/html", exactlyOnePage).ShouldBe(MediaSniffVerdict.Match);
    }

    [Fact]
    public void ParsingServicePublishesTheSniffVerdict()
    {
        var parsing = new ParsingService();

        parsing.SniffMediaType("application/xml", Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?>"))
            .ShouldBe(MediaSniffVerdict.Match);
        parsing.SniffMediaType("application/xml", Encoding.ASCII.GetBytes("{\"a\":1}"))
            .ShouldBe(MediaSniffVerdict.Mismatch);

        // Every table type is published as a match/mismatch; nothing maps to NotSniffed any more (fail closed).
        parsing.SniffMediaType("image/png", PngMagic).ShouldBe(MediaSniffVerdict.Match);
        parsing.SniffMediaType("image/png", ZipMagic).ShouldBe(MediaSniffVerdict.Mismatch);
        parsing.SniffMediaType("application/octet-stream", PngMagic).ShouldBe(MediaSniffVerdict.Mismatch);
    }

    /// <summary>Sniffs a Latin-1-encoded prefix, so a binary prefix stays readable in the test data.</summary>
    private static SniffResult Sniff(string declared, string prefix) =>
        MediaSniffer.Check(declared, Encoding.Latin1.GetBytes(prefix));

    private static MediaSniffVerdict SniffBytes(string declared, byte[] prefix) =>
        new ParsingService().SniffMediaType(declared, prefix);

    private static byte[] Utf16(string text, bool littleEndian)
    {
        var body = (littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode).GetBytes(text);
        byte[] bom = littleEndian ? [0xFF, 0xFE] : [0xFE, 0xFF];
        return [.. bom, .. body];
    }

    private static byte[] Utf32(string text, bool littleEndian)
    {
        var body = Encoding.UTF32.GetBytes(text); // UTF-32 LE on every platform .NET supports
        if (!littleEndian)
        {
            for (var index = 0; index < body.Length; index += 4)
            {
                (body[index], body[index + 3]) = (body[index + 3], body[index]);
                (body[index + 1], body[index + 2]) = (body[index + 2], body[index + 1]);
            }
        }

        byte[] bom = littleEndian ? [0xFF, 0xFE, 0x00, 0x00] : [0x00, 0x00, 0xFE, 0xFF];
        return [.. bom, .. body];
    }
}
