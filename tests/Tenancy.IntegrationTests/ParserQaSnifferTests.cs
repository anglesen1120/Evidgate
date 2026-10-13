using System.Text;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T2.2 QA (AC5; 00 §5.7 P1): hostile declared-versus-actual cases for the media sniff. A declared text
/// family with a foreign magic must be a Mismatch, and a declared type outside the table (an image, an opaque
/// type) must be a Mismatch too — fail closed, never a pass. The T1 <c>NotSniffed</c> verdict and
/// <c>IsSniffed</c> are gone; every verdict is Match or Mismatch.
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
    public void ForeignMagicUnderADeclaredTextFamilyIsAMismatch(string declared)
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

    /// <summary>
    /// 06.3.1-T2.2 (T1 Note 10): the T1 gap is closed — a UTF-16 XML document with its BOM is recognised for the
    /// XML family (04.4.1 AC8), so a wide-encoded JUnit report is not refused at <c>complete</c>.
    /// </summary>
    [Fact]
    public void Utf16LeXmlWithBomMatches()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("<?xml version=\"1.0\"?>")).ToArray();

        MediaSniffer.Check("application/xml", bytes).ShouldBe(SniffResult.Match);
    }

    [Fact]
    public void Utf16LeXmlWithoutAMarkupBodyIsMismatch()
    {
        // The BOM alone is not markup: the bytes after it decide.
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("not markup")).ToArray();

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
        var families = new[] { "application/xml", "application/json", "text/html", "image/png", "application/pdf", "video/mp4" };
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

    /// <summary>
    /// 06.3.1-T2.2 fail-closed: a declared type the profile has no signature for is a Mismatch, never a pass.
    /// <c>image/svg+xml</c> (XML served as an image) and every opaque type are explicitly rejected, so a hostile
    /// SVG sent under that media type can never become evidence.
    /// </summary>
    [Fact]
    public void UnknownOrNonTextDeclarationIsMismatchNotAPass()
    {
        MediaSniffer.Check("image/svg+xml", Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"))
            .ShouldBe(SniffResult.Mismatch);
        MediaSniffer.Check("image/svg+xml", Encoding.ASCII.GetBytes("<script>x</script>"))
            .ShouldBe(SniffResult.Mismatch);
        MediaSniffer.Check("text/plain", Encoding.ASCII.GetBytes("hello"))
            .ShouldBe(SniffResult.Mismatch);
        MediaSniffer.Check("application/octet-stream", PngMagic)
            .ShouldBe(SniffResult.Mismatch);
        MediaSniffer.Check("application/pdf", PdfMagic)
            .ShouldBe(SniffResult.Mismatch);
    }

    /// <summary>
    /// The sniff verdict space is exactly Match/Mismatch: there is no third verdict that a caller could mistake
    /// for a pass (06.3.1-T2.2 removed <c>NotSniffed</c>).
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
    [InlineData("image/svg+xml", "<svg/>")]
    [InlineData("", "")]
    public void EveryVerdictIsMatchOrMismatch(string declared, string prefix)
    {
        var verdict = MediaSniffer.Check(declared, Encoding.Latin1.GetBytes(prefix));

        verdict.ShouldBeOneOf(SniffResult.Match, SniffResult.Mismatch);
    }
}
