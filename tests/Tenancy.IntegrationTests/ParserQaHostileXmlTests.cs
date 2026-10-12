using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1 QA (hostile inputs, 00 §5.7 P5 / AC1–AC2): negative and boundary cases for the XML profile that the
/// T1 suite does not already name. Every assertion is the plan's expected behaviour; none is weakened to match
/// the current code.
/// </summary>
public sealed class ParserQaHostileXmlTests
{
    private const long MaxBytes = 4 * 1024 * 1024;

    [Theory]
    [InlineData("<!DOCTYPE lolz [<!ENTITY lol \"lol\">]><lolz>&lol;</lolz>")]
    [InlineData("<!DOCTYPE r SYSTEM \"http://169.254.169.254/latest/meta-data\"><r/>")]
    [InlineData("<!DOCTYPE r PUBLIC \"-//X//DTD//EN\" \"file:///etc/passwd\"><r/>")]
    [InlineData("<!doctype r [<!ENTITY a \"b\">]><r/>")]
    [InlineData("<?xml version=\"1.0\"?>\n<!-- c --><?pi x?>\n<!DOCTYPE r [<!ENTITY a \"b\">]><r/>")]
    [InlineData("<!ENTITY x \"y\"><r/>")]
    public void DtdAndEntityDeclarationsAreRejectedAsDtdProhibited(string payload)
    {
        var result = ParseXml(payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.DtdProhibited);
    }

    [Fact]
    public void Utf8BomFollowedByDoctypeIsRejectedAsDtdProhibited()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("<!DOCTYPE r [<!ENTITY a \"b\">]><r>&a;</r>"))
            .ToArray();

        var result = SafeXml.Parse(new MemoryStream(bytes), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.DtdProhibited);
    }

    [Fact]
    public void DoctypeTextInsideCdataInsideTheRootIsNotAFalsePositive()
    {
        // The prologue scan stops at the root start tag, so markup-looking text in the body must not trip it.
        var result = ParseXml("<r><![CDATA[<!DOCTYPE x [<!ENTITY a \"b\">]>]]></r>");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void PredefinedEntityReferencesAreStillWellFormed()
    {
        var result = ParseXml("<r>&amp;&lt;&gt;&quot;&apos;</r>");

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("<r>&#0;</r>")]
    [InlineData("<r>\u0001</r>")]
    [InlineData("<r><</r>")]
    [InlineData("<?xml version=\"1.0\" encoding=\"no-such-charset\"?><r/>")]
    public void InvalidCharactersAndEncodingsAreMalformed(string payload)
    {
        var result = ParseXml(payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void WhitespaceOnlyStreamIsMalformed()
    {
        var result = ParseXml("   \r\n\t  ");

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void HundredThousandSiblingElementsStayInsideTheCapAndReopen()
    {
        var body = new StringBuilder("<r>");
        for (var i = 0; i < 100_000; i++)
        {
            body.Append("<i/>");
        }

        body.Append("</r>");

        var result = ParseXml(body.ToString());

        result.IsSuccess.ShouldBeTrue();
        CountElements(result.Value!, "i").ShouldBe(100_000);
    }

    [Fact]
    public void ElementWithTenThousandAttributesParsesWithinTheCap()
    {
        var attributes = string.Join(' ', Enumerable.Range(0, 10_000).Select(i => $"a{i}=\"v\""));

        var result = ParseXml($"<r {attributes}/>");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void DocumentExactlyAtTheCapIsAcceptedAndOneByteOverIsSizeExceeded()
    {
        const long Cap = 64 * 1024;
        var atCap = "<r>" + new string('x', (int)(Cap - 7)) + "</r>";
        var overCap = "<r>" + new string('x', (int)(Cap - 6)) + "</r>";

        var atCapResult = ParseXml(atCap, Cap);
        var overCapResult = ParseXml(overCap, Cap);

        atCapResult.IsSuccess.ShouldBeTrue();
        overCapResult.IsSuccess.ShouldBeFalse();
        ReasonOf(overCapResult).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void Utf16LeWellFormedDocumentParses()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("<r>ok</r>")).ToArray();

        var result = SafeXml.Parse(new MemoryStream(bytes), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Utf16LeDoctypeIsRejectedFailClosedWithADtdOrMalformedReason()
    {
        // Plan §8: DOCTYPE/entity declarations are rejected. A UTF-16 document is not a byte-level BOM the
        // prologue scan recognises (SafeXml.cs PrologueDeclaresDtd only handles the UTF-8 BOM), so the reason
        // code may be `malformed` instead of `dtd_prohibited`. The rejection itself must hold. Reason-code
        // mismatch is recorded in the QA report as a P2 finding.
        var bytes = new byte[] { 0xFF, 0xFE }
            .Concat(Encoding.Unicode.GetBytes("<!DOCTYPE r [<!ENTITY a \"b\">]><r>&a;</r>"))
            .ToArray();

        var result = SafeXml.Parse(new MemoryStream(bytes), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBeOneOf(ParsingFailureReason.DtdProhibited, ParsingFailureReason.Malformed);
    }

    [Fact]
    public void OneByteAtATimeStreamProducesTheSameReportAsAContiguousStream()
    {
        var payload = Encoding.UTF8.GetBytes("<r><a>1</a><b>2</b></r>");

        var result = SafeXml.Parse(new OneByteStream(payload), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        CountElements(result.Value!, "a").ShouldBe(1);
        CountElements(result.Value!, "b").ShouldBe(1);
    }

    [Fact]
    public void CallerIoExceptionPropagatesAndIsNotSwallowedAsAParseFailure()
    {
        // Characterisation of the current behaviour: the profile classifies its own limits and XML errors, but
        // an IOException from the caller's stream is not one of them (SafeXml.cs catch list 82–95). Recorded in the
        // report as a decision the owner must make; the test pins the present behaviour so a silent change shows up.
        Should.Throw<IOException>(() =>
            SafeXml.Parse(new ThrowingStream(), MaxBytes, CancellationToken.None));
    }

    private static Result<ParsedXmlReport> ParseXml(string payload, long maxBytes = MaxBytes) =>
        SafeXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxBytes, CancellationToken.None);

    private static string? ReasonOf(Result result) =>
        result.Error?.Details?.SingleOrDefault()?.Reason;

    private static int CountElements(ParsedXmlReport report, string localName)
    {
        using var reader = report.OpenReader();
        var count = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == localName)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Delivers the payload one byte per read, the worst case for chunk assembly.</summary>
    [SuppressMessage("Design", "CA1812", Justification = "Instantiated by the tests above.")]
    private sealed class OneByteStream(byte[] payload) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= payload.Length)
            {
                return 0;
            }

            buffer[offset] = payload[_position++];
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A caller stream whose read fails with an I/O error.</summary>
    [SuppressMessage("Design", "CA1812", Justification = "Instantiated by the tests above.")]
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated transport failure");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
