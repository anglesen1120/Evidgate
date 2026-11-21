using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>The bounded literal scanner: every quoting form, the escapes and comments around them, nested
/// substitutions, and each shape it must refuse rather than half-read.</summary>
public sealed class WebTemplateLiteralScannerTests
{
    [Fact]
    public void Scan_SingleQuotedLiteral_IsRead() =>
        Single("const value = 'hello';").Value.ShouldBe("hello");

    [Fact]
    public void Scan_DoubleQuotedLiteral_IsRead() =>
        Single("const value = \"world\";").Value.ShouldBe("world");

    [Fact]
    public void Scan_TemplateLiteral_IsRead() =>
        Single("const value = `plain`;").Value.ShouldBe("plain");

    [Fact]
    public void Scan_EscapedQuote_KeepsTheLiteralOpen() =>
        Single("const value = 'it\\'s fine';").Value.ShouldBe("it's fine");

    [Fact]
    public void Scan_LineComment_HoldsNoLiteral()
    {
        var scan = TemplateLiteralScanner.Scan("// don't read this\nconst value = 'x';");

        scan.Complete.ShouldBeTrue();
        scan.Literals.Select(literal => literal.Value).ShouldBe(["x"]);
    }

    [Fact]
    public void Scan_BlockComment_HoldsNoLiteral()
    {
        var scan = TemplateLiteralScanner.Scan("/* \"nope\" */ const value = \"y\";");

        scan.Complete.ShouldBeTrue();
        scan.Literals.Select(literal => literal.Value).ShouldBe(["y"]);
    }

    [Fact]
    public void Scan_NestedSubstitution_ReadsEachTemplateSegmentAndNoIdentifier()
    {
        var scan = TemplateLiteralScanner.Scan("const value = `a${ {b: `c${d}`} }e`;");

        scan.Complete.ShouldBeTrue();
        var values = scan.Literals.Select(literal => literal.Value).ToList();
        values.ShouldContain("a");
        values.ShouldContain("c");
        values.ShouldContain("e");
        values.ShouldNotContain("d");
    }

    [Theory]
    [InlineData("const value = 'x;")]
    [InlineData("const value = \"x;")]
    [InlineData("const value = `x;")]
    [InlineData("/* x")]
    [InlineData("const value = `x${y;")]
    public void Scan_UnterminatedInput_IsIncomplete(string source)
    {
        var scan = TemplateLiteralScanner.Scan(source);

        scan.Complete.ShouldBeFalse();
    }

    [Fact]
    public void Scan_OversizedInput_IsIncomplete() =>
        TemplateLiteralScanner.Scan(new string('a', TemplateLiteralScanner.MaxChars + 1)).Complete.ShouldBeFalse();

    [Fact]
    public void Scan_TemplateSegment_CarriesTheOpeningBacktick()
    {
        const string Source = "const password = `x${y}z`;";

        var scan = TemplateLiteralScanner.Scan(Source);

        scan.Complete.ShouldBeTrue();
        scan.Literals.Select(literal => literal.Value).ShouldBe(["x", "z"]);
        scan.Literals.Select(literal => literal.Offset).Distinct().ShouldBe([Source.IndexOf('`', StringComparison.Ordinal)]);
    }

    [Theory]
    [InlineData("/\\/\\//;")]
    [InlineData("/'/;")]
    [InlineData("const value = /'/;")]
    public void Scan_PossibleRegexStart_IsIncomplete(string source) =>
        TemplateLiteralScanner.Scan(source).Complete.ShouldBeFalse();

    [Fact]
    public void Scan_Division_KeepsScanning()
    {
        var scan = TemplateLiteralScanner.Scan("const value = a / b; const name = 'hello';");

        scan.Complete.ShouldBeTrue();
        scan.Literals.Single().Value.ShouldBe("hello");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void Scan_LineContinuation_IsDropped(string terminator)
    {
        var scan = TemplateLiteralScanner.Scan("const value = 'ghp_abcdefgh\\" + terminator + "ijklmnopqrstuvwx';");

        scan.Complete.ShouldBeTrue();
        scan.Literals.Single().Value.ShouldBe("ghp_abcdefghijklmnopqrstuvwx");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void Scan_LineComment_EndsAtEveryJavaScriptTerminator(string terminator)
    {
        var scan = TemplateLiteralScanner.Scan("// don't read this" + terminator + "const value = 'x';");

        scan.Complete.ShouldBeTrue();
        scan.Literals.Select(literal => literal.Value).ShouldBe(["x"]);
    }

    private static TemplateLiteralScanner.Literal Single(string source)
    {
        var scan = TemplateLiteralScanner.Scan(source);

        scan.Complete.ShouldBeTrue();
        scan.Literals.Count.ShouldBe(1);
        return scan.Literals[0];
    }
}
