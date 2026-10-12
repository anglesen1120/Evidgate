using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1.1 (AC2; 00 §5.7 S5): the JSON arm of the parser profile. JSON has no entity or DTD surface, so
/// the depth cap, the byte cap and the wall clock are the whole guard, and every refusal must arrive as a
/// <c>Result</c> value with a generic reason — never as an escaping <c>JsonException</c>.
/// </summary>
public sealed class SafeJsonTests
{
    /// <summary>The kind cap these tests parse against.</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>A payload shaped like a serialised DOM/accessibility snapshot.</summary>
    private const string WellFormedPayload =
        """{"version":1,"nodes":[{"id":"n1","role":"button","name":"Checkout"},{"id":"n2","role":"text","name":"Total"}],"truncated":false}""";

    [Fact]
    public void ParsesAWellFormedPayload()
    {
        var result = ParseJson(WellFormedPayload);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        using var document = result.Value;
        document.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
        document.RootElement.GetProperty("nodes").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void ToleratesUtf8Bom()
    {
        // The media sniffer already accepts a BOM-prefixed JSON object, so the parser must accept the same bytes:
        // otherwise a BOM would make an artifact sniff-match and then fail to parse.
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes(WellFormedPayload));

        var result = SafeJson.Parse(new MemoryStream(bytes.ToArray()), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    [Fact]
    public void RejectsDuplicateKeysAsMalformed()
    {
        // AllowDuplicateProperties = false: a repeated key is refused rather than silently shadowing the value an
        // earlier key carried.
        var result = ParseJson("""{"a":1,"a":2}""");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void EnforcesMaxDepth()
    {
        // 64 levels is the profile's cap; the 65th container is refused with the depth reason.
        ParseJson(NestedJson(64)).IsSuccess.ShouldBeTrue();

        var tooDeep = ParseJson(NestedJson(65));

        tooDeep.IsSuccess.ShouldBeFalse();
        tooDeep.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(tooDeep).ShouldBe(ParsingFailureReason.DepthExceeded);
    }

    [Fact]
    public void ReportsSyntaxErrorsAsMalformedEvenWhenTheyAreDeep()
    {
        // A payload that nests as deep as the profile allows and then breaks: the reason must be the syntax, not
        // the depth, because the depth cap is checked only when another container is pushed.
        var result = ParseJson(NestedJson(64, "1 2"));

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void EnforcesSizeCap()
    {
        const long Cap = 64 * 1024;

        ParseJson(ExactSizeJson(Cap), Cap).IsSuccess.ShouldBeTrue();

        var overCap = ParseJson(ExactSizeJson(Cap + 1), Cap);

        overCap.IsSuccess.ShouldBeFalse();
        overCap.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        overCap.Error.Status.ShouldBe(413);
        ReasonOf(overCap).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void SizeCapHoldsWhenTheStreamLiesAboutItsLength()
    {
        const long Cap = 4 * 1024;

        using var lying = new LyingLengthStream(Encoding.UTF8.GetBytes(ExactSizeJson(Cap + 512)));

        var result = SafeJson.Parse(lying, Cap, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void EnforcesParseTimeout()
    {
        // The profile's token is observed between reads, so the payload must take many reads to reach the
        // timeout: 128 reads of 30 ms against a 120 ms budget.
        var payload = Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('p', 8192) + "\"}");
        using var slow = new SlowStream(payload, 64, TimeSpan.FromMilliseconds(30));

        var result = SafeJson.Parse(slow, MaxBytes, TimeSpan.FromMilliseconds(120), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Timeout);
    }

    [Fact]
    public void CallerCancellationIsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            SafeJson.Parse(new MemoryStream(Encoding.UTF8.GetBytes(WellFormedPayload)), MaxBytes, cancelled.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[1,2")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{'a':1}")]
    [InlineData("{\"a\":1} trailing")]
    [InlineData("{\"a\":01}")]
    [InlineData("nul")]
    [InlineData("// comment\n{}")]
    [InlineData("{/* comment */\"a\":1}")]
    public void RejectsMalformedPayloads(string payload)
    {
        var result = ParseJson(payload);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void RejectsAnEmptyStream()
    {
        var result = SafeJson.Parse(Stream.Null, MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void CallerInvalidDataExceptionBecomesMalformedNotSizeExceeded()
    {
        // A caller stream that fails with InvalidDataException (a corrupt GZipStream, say) is malformed content.
        // Only the bounded read's own ContentTooLongException may be mapped to size_exceeded; the caller's
        // InvalidDataException must not be misreported as a breached cap.
        var result = SafeJson.Parse(new InvalidDataStream(), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    /// <summary>Parses a UTF-8 payload against the profile at the test's kind cap.</summary>
    private static Result<JsonDocument> ParseJson(string payload, long maxBytes = MaxBytes) =>
        SafeJson.Parse(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxBytes, CancellationToken.None);

    /// <summary>The generic reason a failure carries in its <c>details</c> (00 §5.7 S3).</summary>
    private static string? ReasonOf(Result result) =>
        result.Error?.Details?.SingleOrDefault()?.Reason;

    /// <summary>A payload of <paramref name="levels"/> nested arrays around <paramref name="inner"/>.</summary>
    private static string NestedJson(int levels, string inner = "1") =>
        new string('[', levels) + inner + new string(']', levels);

    /// <summary>A well-formed ASCII payload of exactly <paramref name="bytes"/> bytes.</summary>
    private static string ExactSizeJson(long bytes)
    {
        const string Head = "{\"pad\":\"";
        const string Tail = "\"}";
        var padding = bytes - Head.Length - Tail.Length;

        return Head + new string('a', (int)padding) + Tail;
    }
}
