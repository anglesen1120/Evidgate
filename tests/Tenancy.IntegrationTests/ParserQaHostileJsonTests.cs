using System.Diagnostics.CodeAnalysis;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1 QA (hostile inputs, 00 §5.7 P5 / AC2): negative and boundary cases for the JSON profile that the
/// T1 suite does not already name. Assertions are the plan's expected behaviour; none is weakened to fit the code.
/// </summary>
public sealed class ParserQaHostileJsonTests
{
    private const long MaxBytes = 4 * 1024 * 1024;

    [Fact]
    public void NestingExactlyAtTheDepthCapIsAccepted()
    {
        // ParsingLimits.MaxDepth = 64: 64 nested arrays are at the limit and must parse.
        var payload = new string('[', 64) + new string(']', 64);

        var result = ParseJson(payload);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void NestingOneBeyondTheDepthCapIsDepthExceeded()
    {
        var payload = new string('[', 65) + new string(']', 65);

        var result = ParseJson(payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.DepthExceeded);
    }

    [Theory]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,2,]")]
    [InlineData("/* c */ {\"a\":1}")]
    [InlineData("{\"a\":1} // trailing")]
    [InlineData("{\"a\":01}")]
    [InlineData("NaN")]
    [InlineData("{\"a\":\"\\x41\"}")]
    public void StrictSyntaxViolationsAreMalformed(string payload)
    {
        var result = ParseJson(payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void HugeNumberLiteralsAreWellFormedAndDoNotOverflowTheParser()
    {
        // JsonDocument stores numbers as raw text; syntactically valid numbers outside double range must parse.
        var result = ParseJson("{\"big\":1e400,\"small\":-1e-400,\"int\":123456789012345678901234567890}");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void EncodedSurrogateBytesAreRejectedAsMalformed()
    {
        // 0xED 0xA0 0x80 is the UTF-8-encoded surrogate U+D800: invalid UTF-8, never valid JSON text.
        var bytes = Encoding.UTF8.GetBytes("{\"a\":\"")
            .Concat(new byte[] { 0xED, 0xA0, 0x80 })
            .Concat(Encoding.UTF8.GetBytes("\"}"))
            .ToArray();

        var result = SafeJson.Parse(new MemoryStream(bytes), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void UnpairedEscapedSurrogateIsAcceptedAsJsonText()
    {
        // RFC 8259 allows a lone \uD800 escape in a string; the profile must not treat it as a limit breach.
        var result = ParseJson("{\"a\":\"\\uD800\"}");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void StringExactlyAtTheSizeCapIsAcceptedAndOneByteOverIsSizeExceeded()
    {
        const long Cap = 64 * 1024;
        var atCap = "\"" + new string('s', (int)(Cap - 2)) + "\"";
        var overCap = "\"" + new string('s', (int)(Cap - 1)) + "\"";

        var atCapResult = ParseJson(atCap, Cap);
        var overCapResult = ParseJson(overCap, Cap);

        atCapResult.IsSuccess.ShouldBeTrue();
        overCapResult.IsSuccess.ShouldBeFalse();
        ReasonOf(overCapResult).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void Utf8ByteOrderMarkIsDroppedAndTheDocumentParses()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}")).ToArray();

        var result = SafeJson.Parse(new MemoryStream(bytes), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Utf16LeJsonIsRejectedFailClosed()
    {
        // The profile handles only the UTF-8 BOM. UTF-16 JSON must fail closed as malformed, never parse
        // half-decoded bytes.
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("{\"a\":1}")).ToArray();

        var result = SafeJson.Parse(new MemoryStream(bytes), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void CallerIoExceptionPropagatesAndIsNotSwallowedAsAParseFailure()
    {
        // Characterisation: an IOException from the caller's stream is not classified by SafeJson (catch list
        // 77–92). Recorded in the QA report as a decision for the owner; pinned so a silent change shows.
        Should.Throw<IOException>(() =>
            SafeJson.Parse(new ThrowingStream(), MaxBytes, CancellationToken.None));
    }

    private static Result<System.Text.Json.JsonDocument> ParseJson(string payload, long maxBytes = MaxBytes) =>
        SafeJson.Parse(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxBytes, CancellationToken.None);

    private static string? ReasonOf(Result result) =>
        result.Error?.Details?.SingleOrDefault()?.Reason;

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
