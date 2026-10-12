using System.Text;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1 QA (AC5; 00 §5.7 P1): hostile declared-versus-actual cases for the media sniff that the T1 suite does
/// not name. A declared text family with a foreign magic must be a Mismatch, never NotSniffed (a bypass).
/// </summary>
public sealed class ParserQaSnifferTests
{
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] ElfMagic = { 0x7F, 0x45, 0x4C, 0x46 };
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-1.7");

    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/xml")]
    [InlineData("application/vnd.evidgate.report+xml")]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]
    [InlineData("text/html")]
    public void ForeignMagicUnderADeclaredTextFamilyIsAMismatchNeverNotSniffed(string declared)
    {
        foreach (var magic in new[] { PngMagic, ZipMagic, ElfMagic, PdfMagic })
        {
            MediaSniffer.Check(declared, magic).ShouldBe(SniffResult.Mismatch);
        }
    }

    [Theory]
    [InlineData("Application/XML")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("Text/Html")]
    [InlineData("application/xml; charset=utf-8")]
    [InlineData("  application/json \t")]
    public void DeclaredFamilyIsCaseAndParameterInsensitive(string declared)
    {
        MediaSniffer.Check(declared, PngMagic).ShouldBe(SniffResult.Mismatch);
    }

    [Fact]
    public void Utf8BomBeforeXmlDeclarationMatches()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>")).ToArray();

        MediaSniffer.Check("application/xml", bytes).ShouldBe(SniffResult.Match);
    }

    [Fact]
    public void Utf8BomBeforeJsonObjectMatches()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}")).ToArray();

        MediaSniffer.Check("application/json", bytes).ShouldBe(SniffResult.Match);
    }

    [Fact]
    public void Utf16LeXmlIsMismatchFailClosed()
    {
        // Known gap (P3 product question): the sniff trims only the UTF-8 BOM. A UTF-16 XML document is rejected
        // rather than admitted. Fail closed; recorded in the QA report.
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("<?xml version=\"1.0\"?>")).ToArray();

        MediaSniffer.Check("application/xml", bytes).ShouldBe(SniffResult.Mismatch);
    }

    [Theory]
    [InlineData("<!DOCT")]
    public void TruncatedMarkupKeywordsAtThePrefixEdgeMatchTheirFamily(string prefix)
    {
        // The sniff reads at most SniffPrefixBytes, so a keyword can be cut mid-token. A keyword prefix is markup.
        MediaSniffer.Check("application/xml", Encoding.ASCII.GetBytes(prefix)).ShouldBe(SniffResult.Match);
    }

    [Theory]
    [InlineData("nullx")]
    [InlineData("truefalse")]
    [InlineData("falsey")]
    public void JsonLiteralsThatAreOnlyPrefixesOfATokenAreMismatch(string prefix)
    {
        MediaSniffer.Check("application/json", Encoding.ASCII.GetBytes(prefix)).ShouldBe(SniffResult.Mismatch);
    }

    [Fact]
    public void HtmlDoctypeAfterLeadingWhitespaceMatches()
    {
        MediaSniffer.Check("text/html", Encoding.ASCII.GetBytes("\r\n  <!doctype html>")).ShouldBe(SniffResult.Match);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    [InlineData("\uFEFF")]
    public void EmptyOrWhitespaceOnlyPrefixIsMismatchNotAPass(string prefix)
    {
        // A zero-length or whitespace-only object must never sniff as a valid document.
        MediaSniffer.Check("application/xml", Encoding.UTF8.GetBytes(prefix)).ShouldBe(SniffResult.Mismatch);
        MediaSniffer.Check("application/json", Encoding.UTF8.GetBytes(prefix)).ShouldBe(SniffResult.Mismatch);
    }

    [Fact]
    public void EveryFirstByteValueUnderEveryFamilyNeverThrows()
    {
        // Hostile fuzz over the whole first-byte space plus a long tail: the sniff must always return a verdict.
        var families = new[] { "application/xml", "application/json", "text/html", "image/png", "application/pdf" };
        for (var first = 0; first < 256; first++)
        {
            var prefix = new byte[ParsingLimits.SniffPrefixBytes];
            prefix[0] = (byte)first;
            Array.Fill(prefix, (byte)'<', 1, prefix.Length - 1);
            foreach (var family in families)
            {
                Should.NotThrow(() => MediaSniffer.Check(family, prefix));
            }
        }
    }

    [Fact]
    public void UnknownOrNonTextDeclarationIsNotSniffedRatherThanRejected()
    {
        // Out-of-table types are the caller's allow-list decision (AC5 scope). The sniffer returns NotSniffed.
        MediaSniffer.Check("image/png", PngMagic).ShouldBe(SniffResult.NotSniffed);
        MediaSniffer.Check("image/svg+xml", Encoding.ASCII.GetBytes("<svg/>")).ShouldBe(SniffResult.NotSniffed);
    }

    [Fact]
    public void SvgDeclaredAsImageIsNotSniffedAndIsADocumentedGap()
    {
        // image/svg+xml is not in the T1 XML family (IsXml covers application/* +xml and text/xml). A hostile SVG
        // sent under that media type is not checked by T1. Recorded as a P2 gap for the owner: confirm the
        // media-type allow-list excludes it, or add it to the sniff family.
        MediaSniffer.Check("image/svg+xml", Encoding.ASCII.GetBytes("<script>x</script>")).ShouldBe(SniffResult.NotSniffed);
    }

    /// <summary>
    /// 06.3.1-T1 F-P2: <see cref="MediaSniffer.IsSniffed"/> is the exact predicate the completion uses to decide
    /// whether to issue a prefix read, so it must agree with <see cref="MediaSniffer.Check"/>: a read happens iff
    /// the verdict is not <see cref="SniffResult.NotSniffed"/>. Any drift would let a sniffed type skip its check.
    /// </summary>
    [Theory]
    [InlineData("application/xml", "<?xml version=\"1.0\"?>")]
    [InlineData("application/xml", "\u0089PNG\r\n\u001A\n")]
    [InlineData("text/xml", "<r/>")]
    [InlineData("application/atom+xml", "<feed/>")]
    [InlineData("application/json", "{\"a\":1}")]
    [InlineData("application/json", "<html/>")]
    [InlineData("text/html", "<html/>")]
    [InlineData("text/html", "plain")]
    [InlineData("image/png", "\u0089PNG\r\n\u001A\n")]
    [InlineData("application/zip", "PK\u0003\u0004")]
    [InlineData("text/plain", "hello")]
    [InlineData("", "")]
    public void IsSniffedAgreesWithCheckNeverNotSniffed(string declared, string prefix)
    {
        var verdict = MediaSniffer.Check(declared, Encoding.Latin1.GetBytes(prefix));

        MediaSniffer.IsSniffed(declared).ShouldBe(verdict != SniffResult.NotSniffed);
    }
}
