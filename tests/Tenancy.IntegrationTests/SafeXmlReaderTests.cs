using System.Text;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1.1 (AC1, AC2; 00 §5.7 S4/S5): the XML arm of the parser profile. Every hostile document is an
/// inline payload — the shared corpus lands in 06.3.2 — and every assertion is about a <c>Result</c> value,
/// a bounded allocation and a bounded wall clock, never about an exception escaping the profile.
/// </summary>
public sealed class SafeXmlReaderTests
{
    /// <summary>The kind cap these tests parse against (a JUnit report is the largest XML artifact kind).</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>A well-formed JUnit-shaped report: the shape 04.4.1's mapper walks.</summary>
    private const string WellFormedReport =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites name="suite" tests="2" failures="1">
          <testsuite name="checkout" tests="2" failures="1">
            <testcase classname="Cart" name="adds an item" time="0.012" />
            <testcase classname="Cart" name="rejects a bad coupon" time="0.004">
              <failure message="expected 1 coupon">at Cart.Tests:12</failure>
            </testcase>
          </testsuite>
        </testsuites>
        """;

    /// <summary>
    /// Hostile payloads the profile must refuse at the prologue: DTD declarations, external general entities
    /// (local file, URL), a parameter entity pulling an external DTD subset, a PUBLIC external identifier and an
    /// XInclude-shaped fetch. Every one declares a DTD, so every one is <c>dtd_prohibited</c>.
    /// </summary>
    public static TheoryData<string> HostileDocuments => new()
    {
        // A classic billion-laughs DTD: rejected at the prologue, so no entity is ever expanded.
        """
        <?xml version="1.0"?>
        <!DOCTYPE lolz [
          <!ENTITY lol "lol">
          <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
          <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
          <!ENTITY lol4 "&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;">
        ]>
        <lolz>&lol4;</lolz>
        """,
        // External general entity reading a local file.
        """
        <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><foo>&xxe;</foo>
        """,
        // External general entity reading a URL.
        """
        <!DOCTYPE foo [<!ENTITY xxe SYSTEM "http://127.0.0.1:9/never-called">]><foo>&xxe;</foo>
        """,
        // Parameter entity pulling an external DTD subset.
        """
        <!DOCTYPE foo [<!ENTITY % remote SYSTEM "http://127.0.0.1:9/never-called.dtd"> %remote;]><foo/>
        """,
        // PUBLIC external DTD identifier.
        """
        <!DOCTYPE foo PUBLIC "-//Evidgate//Never Called//EN" "http://127.0.0.1:9/never-called.dtd"><foo/>
        """,
        // XInclude-shaped document whose entity would fetch a file: XmlReader implements no XInclude, so the
        // entity is the only resolution surface and the profile refuses it.
        """
        <!DOCTYPE foo [<!ENTITY xi SYSTEM "file:///etc/passwd">]>
        <foo xmlns:xi="http://www.w3.org/2001/XInclude"><xi:include href="file:///etc/passwd" parse="text"/>&xi;</foo>
        """,
        // An internal-only DTD (no external identifier): still a DTD, so still prohibited.
        """
        <!DOCTYPE foo [<!ELEMENT foo (#PCDATA)>]><foo>hello</foo>
        """,
    };

    [Fact]
    public void ParsesAWellFormedReportAndReopensIt()
    {
        var result = ParseXml(WellFormedReport);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        var report = result.Value;

        // The report is a streaming handle: the same validated bytes can be walked again, which is what
        // 04.4.1 needs when it maps the suite and then re-reads a failure detail.
        var first = ReadAll(report);
        var second = ReadAll(report);

        first.ShouldBe(second);
        first.ShouldContain("<testsuites");
        first.ShouldContain("rejects a bad coupon");
    }

    [Fact]
    public void ReaderProfileProhibitsDtdAndCarriesNoResolver()
    {
        var settings = SafeXml.CreateSettings(MaxBytes);

        // AC1/00 §5.7 S4: the four guards are properties of the one settings object the profile builds.
        settings.DtdProcessing.ShouldBe(DtdProcessing.Prohibit);
        // DtdProcessing.Prohibit is the real entity guard: with DTDs prohibited no general entity can be declared
        // or expanded. MaxCharactersFromEntities is a positive cap, and 0 would mean no limit at all.
        settings.MaxCharactersFromEntities.ShouldBe(1);
        settings.MaxCharactersInDocument.ShouldBe(MaxBytes);

        // The caller's stream stays open: the profile owns its own bounded buffer, not the caller's stream.
        settings.CloseInput.ShouldBeFalse();
    }

    [Fact]
    public void ProfileSurfaceExposesNoWayToEnableDtdOrResolver()
    {
        // Acceptance: the objects the profile hands back cannot loosen a guard. ParsedXmlReport exposes a reader
        // factory only, and IParsing names no resolver, DTD or entity member at all.
        typeof(ParsedXmlReport).GetProperties()
            .ShouldNotContain(property => property.PropertyType == typeof(XmlReaderSettings));

        typeof(IParsing).GetMembers()
            .ShouldNotContain(member =>
                member.Name.Contains("Resolver", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Dtd", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Entity", StringComparison.OrdinalIgnoreCase));

        // Only the report's own methods count: object's Equals/GetHashCode/ToString take parameters and cannot
        // loosen anything, while a declared method with a parameter would be a way to pass settings in.
        typeof(ParsedXmlReport).GetMethods()
            .Where(method => method.DeclaringType == typeof(ParsedXmlReport))
            .ShouldNotContain(method => method.GetParameters().Length > 0);
    }

    [Theory]
    [MemberData(nameof(HostileDocuments))]
    public void RejectsExternalEntities(string payload)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Environment.TickCount64;

        var result = ParseXml(payload);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        // The profile takes no resolver and builds settings with XmlResolver = null, so every payload's DTD is
        // refused at the prologue and no external resource is ever fetched: the cost is bounded by the payload,
        // not by the DTD.
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.DtdProhibited);

        allocated.ShouldBeLessThan(4 * 1024 * 1024, "a rejected DTD must not expand into memory");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PredefinedEntitiesAndCharacterReferencesSurviveTheEntityCap()
    {
        // MaxCharactersFromEntities = 1 is a positive cap, but it counts only characters produced by general
        // entity expansion — which the profile prohibits anyway. Predefined entities and numeric character
        // references are not counted, so a legitimate document that uses them must still parse.
        var result = ParseXml("<r>&amp;&lt;&#65;&#x42;</r>");

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    [Fact]
    public void EnforcesDepthCap()
    {
        // The root element is level 1, so 63 nested children under it is the 64th level: the deepest document
        // the profile accepts. One more level is refused with the reason 06.3.2's corpus asserts.
        ParseXml(NestedDocument(63)).IsSuccess.ShouldBeTrue();

        var tooDeep = ParseXml(NestedDocument(64));

        tooDeep.IsSuccess.ShouldBeFalse();
        tooDeep.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(tooDeep).ShouldBe(ParsingFailureReason.DepthExceeded);
    }

    [Theory]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public void DepthCountsElementsOnlySoATextLeafAtTheCapIsAccepted(int levels, bool accepted)
    {
        // A text node inside the deepest element carries XmlReader.Depth one past that element's own, so counting
        // every node's depth would reject the 64th level's own text leaf. Only element nodes count: 64 element
        // levels (the root plus 63 children) with a text leaf are accepted, and the 65th element level is refused.
        var result = ParseXml(NestedDocument(levels, "leaf"));

        result.IsSuccess.ShouldBe(accepted);

        if (!accepted)
        {
            ReasonOf(result).ShouldBe(ParsingFailureReason.DepthExceeded);
        }
    }

    [Fact]
    public void EnforcesSizeCap()
    {
        const long Cap = 64 * 1024;

        // Exactly the cap is allowed.
        ParseXml(ExactSizeDocument(Cap), Cap).IsSuccess.ShouldBeTrue();

        // One byte more is 413 too_large with the size reason.
        var overCap = ParseXml(ExactSizeDocument(Cap + 1), Cap);

        overCap.IsSuccess.ShouldBeFalse();
        overCap.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        overCap.Error.Status.ShouldBe(413);
        ReasonOf(overCap).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void SizeCapHoldsWhenTheStreamLiesAboutItsLength()
    {
        const long Cap = 4 * 1024;

        // A stream that reports Length 0 while holding more than the cap: the profile counts bytes it read,
        // never a length the caller controls.
        using var lying = new LyingLengthStream(Encoding.UTF8.GetBytes(ExactSizeDocument(Cap + 512)));

        var result = SafeXml.Parse(lying, Cap, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void EnforcesParseTimeout()
    {
        // The profile's token is observed between reads, so the payload must take many reads to reach the
        // timeout: ~32 reads of 30 ms against a 120 ms budget. The trailing comment keeps it well-formed.
        var payload = Encoding.UTF8.GetBytes(WellFormedReport + "<!--" + new string('p', 8192) + "-->");
        using var slow = new SlowStream(payload, 256, TimeSpan.FromMilliseconds(30));

        var result = SafeXml.Parse(slow, MaxBytes, TimeSpan.FromMilliseconds(120), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Timeout);
    }

    [Fact]
    public void CallerCancellationIsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // A caller cancellation propagates: only the profile's own timeout becomes a Result value.
        Should.Throw<OperationCanceledException>(() =>
            SafeXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes(WellFormedReport)), MaxBytes, cancelled.Token));
    }

    [Theory]
    [InlineData("<foo>")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<foo/><bar/>")]
    [InlineData("<foo/>trailing")]
    [InlineData("<!-- only a comment -->")]
    [InlineData("<?xml version=\"1.0\"?><foo>&undeclared;</foo>")]
    public void RejectsMalformedDocuments(string payload)
    {
        var result = ParseXml(payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void RejectsAnEmptyStream()
    {
        var result = SafeXml.Parse(Stream.Null, MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void CallerInvalidDataExceptionBecomesMalformedNotSizeExceeded()
    {
        // A caller stream that fails with InvalidDataException (a corrupt GZipStream, say) is malformed content.
        // Only the bounded read's own ContentTooLongException may be mapped to size_exceeded; the caller's
        // InvalidDataException must not be misreported as a breached cap.
        var result = SafeXml.Parse(new InvalidDataStream(), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    /// <summary>Parses a UTF-8 payload against the profile at the test's kind cap.</summary>
    private static Result<ParsedXmlReport> ParseXml(string payload, long maxBytes = MaxBytes) =>
        SafeXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxBytes, CancellationToken.None);

    /// <summary>
    /// 04.1.3-T1 (AC3): the entry cap is exact — a report carrying exactly the cap's entries parses, one more is
    /// refused <c>cap_exceeded</c> with the cap's name, its limit and the counted entries, and the count is the
    /// document's real one rather than an early stop.
    /// </summary>
    [Fact]
    public void EnforcesTheReportCaseCap()
    {
        var atCap = SafeXml.Parse(
            new MemoryStream(Encoding.UTF8.GetBytes(ReportWithCases(3))),
            MaxBytes,
            maxCases: 3,
            CancellationToken.None);
        atCap.IsSuccess.ShouldBeTrue(atCap.Error?.Message);

        var overCap = SafeXml.Parse(
            new MemoryStream(Encoding.UTF8.GetBytes(ReportWithCases(4))),
            MaxBytes,
            maxCases: 3,
            CancellationToken.None);

        overCap.IsSuccess.ShouldBeFalse();
        overCap.Error!.Code.ShouldBe(ApiErrorCode.CapExceeded);
        var detail = overCap.Error.Details.ShouldHaveSingleItem();
        detail.Path.ShouldBe("report_cases");
        detail.Reason.ShouldBe(ApiErrorCode.CapExceeded);
        detail.Limit.ShouldBe(3);
        detail.Actual.ShouldBe(4);
    }

    /// <summary>A JUnit-shaped report carrying exactly <paramref name="cases"/> entries.</summary>
    private static string ReportWithCases(int cases)
    {
        var body = new StringBuilder();
        for (var index = 0; index < cases; index++)
        {
            body.Append("<testcase name=\"case ").Append(index).Append("\"/>");
        }

        return "<testsuites><testsuite>" + body + "</testsuite></testsuites>";
    }

    /// <summary>The generic reason a failure carries in its <c>details</c> (00 §5.7 S3).</summary>
    private static string? ReasonOf(Result result) =>
        result.Error?.Details?.SingleOrDefault()?.Reason;

    /// <summary>Walks a report through a fresh reader, returning the name of every element node.</summary>
    private static string ReadAll(ParsedXmlReport report)
    {
        var text = new StringBuilder();

        using var reader = report.OpenReader();
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    text.Append('<').Append(reader.Name);
                    while (reader.MoveToNextAttribute())
                    {
                        text.Append(' ').Append(reader.Name).Append("=\"").Append(reader.Value).Append('"');
                    }

                    text.Append('>');
                    reader.MoveToElement();
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    text.Append(reader.Value);
                    break;
                default:
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>A document nested <paramref name="levels"/> deep under its root element.</summary>
    /// <param name="levels">How many child elements to nest under the root.</param>
    /// <param name="leaf">Optional text placed inside the innermost element.</param>
    private static string NestedDocument(int levels, string leaf = "") =>
        "<r>" + string.Concat(Enumerable.Repeat("<a>", levels)) + leaf
        + string.Concat(Enumerable.Repeat("</a>", levels)) + "</r>";

    /// <summary>A well-formed ASCII document of exactly <paramref name="bytes"/> bytes.</summary>
    private static string ExactSizeDocument(long bytes)
    {
        const string Head = "<?xml version=\"1.0\"?><r><pad>";
        const string Tail = "</pad></r>";
        var padding = bytes - Head.Length - Tail.Length;

        return Head + new string('a', (int)padding) + Tail;
    }
}
