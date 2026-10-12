using System.Text;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1.2 (AC5; 00 §5.7 P1): the media sniff of the T1 table — the XML family, the JSON family and
/// <c>text/html</c>. Every other declared media type answers <see cref="SniffResult.NotSniffed"/>; 06.3.1-T2.2
/// completes the magic-byte table (PNG/JPEG/MP4/zip) and makes <c>NotSniffed</c> fail closed at <c>complete</c>,
/// extending this class.
/// <para>
/// Prefixes are written as Latin-1 strings so a binary prefix (<c>\u0089PNG</c>) is readable inline. The sniff
/// is pure: it never throws, never allocates and never returns a byte of the prefix.
/// </para>
/// </summary>
public sealed class MediaSnifferTests
{
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
        Sniff(declared, prefix).ShouldBe(MediaSniffVerdict.Match);

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
        Sniff(declared, prefix).ShouldBe(MediaSniffVerdict.Mismatch);

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
        Sniff(declared, prefix).ShouldBe(MediaSniffVerdict.Match);

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
        Sniff(declared, prefix).ShouldBe(MediaSniffVerdict.Mismatch);

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
        Sniff(declared, prefix).ShouldBe(MediaSniffVerdict.Match);

    [Theory]
    [InlineData("text/html", "")]
    [InlineData("text/html", "   ")]
    [InlineData("text/html", "{\"a\":1}")]
    [InlineData("text/html", "plain text")]
    [InlineData("text/html", "\u0089PNG\r\n\u001A\n")]
    [InlineData("text/html", "<3")]
    public void DeclaredHtmlRejectsNonMarkupPrefixes(string declared, string prefix) =>
        Sniff(declared, prefix).ShouldBe(MediaSniffVerdict.Mismatch);

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("video/mp4")]
    [InlineData("application/zip")]
    [InlineData("application/octet-stream")]
    [InlineData("text/plain")]
    [InlineData("application/pdf")]
    [InlineData("application/xmlx")]
    [InlineData("application/jsonx")]
    [InlineData("text/xmlish")]
    [InlineData("application/")]
    [InlineData("")]
    [InlineData("   ")]
    public void DeclaredTypesOutsideTheT1TableAreNotSniffed(string declared)
    {
        // NotSniffed is not "accepted": 06.3.1-T2.2 makes it fail closed at `complete`. At T1 the table simply
        // does not know these types yet.
        Sniff(declared, "\u0089PNG\r\n\u001A\n").ShouldBe(MediaSniffVerdict.NotSniffed);
        Sniff(declared, "<?xml version=\"1.0\"?>").ShouldBe(MediaSniffVerdict.NotSniffed);
        Sniff(declared, "").ShouldBe(MediaSniffVerdict.NotSniffed);
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
                     "application/ld+json", "text/html", "image/png", string.Empty,
                 })
        {
            var verdict = new ParsingService().SniffMediaType(declared, hostile);
            verdict.ShouldBeOneOf(MediaSniffVerdict.Mismatch, MediaSniffVerdict.NotSniffed);
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
        parsing.SniffMediaType("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 })
            .ShouldBe(MediaSniffVerdict.NotSniffed);
    }

    /// <summary>Sniffs a Latin-1-encoded prefix, so a binary prefix stays readable in the test data.</summary>
    private static MediaSniffVerdict Sniff(string declared, string prefix) =>
        SniffBytes(declared, Encoding.Latin1.GetBytes(prefix));

    private static MediaSniffVerdict SniffBytes(string declared, byte[] prefix) =>
        new ParsingService().SniffMediaType(declared, prefix);
}
