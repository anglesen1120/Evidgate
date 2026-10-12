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
    /// Hostile payloads and whether a permissive reader (DTDs allowed, the spy resolver installed) actually
    /// reaches out for an external resource. The profile must refuse every one of them, and the flag proves the
    /// external-entity payloads are genuine XXE attempts rather than strings the reader would ignore anyway.
    /// </summary>
    public static TheoryData<string, bool> HostileDocuments => new()
    {
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
            false
        },
        {
            // External general entity reading a local file.
            """
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><foo>&xxe;</foo>
            """,
            true
        },
        {
            // External general entity reading a URL.
            """
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "http://127.0.0.1:9/never-called">]><foo>&xxe;</foo>
            """,
            true
        },
        {
            // Parameter entity pulling an external DTD subset.
            """
            <!DOCTYPE foo [<!ENTITY % remote SYSTEM "http://127.0.0.1:9/never-called.dtd"> %remote;]><foo/>
            """,
            true
        },
        {
            // PUBLIC external DTD identifier.
            """
            <!DOCTYPE foo PUBLIC "-//Evidgate//Never Called//EN" "http://127.0.0.1:9/never-called.dtd"><foo/>
            """,
            true
        },
        {
            // XInclude-shaped document whose entity would fetch a file: XmlReader implements no XInclude, so
            // the entity is the only resolution surface and the profile refuses it.
            """
            <!DOCTYPE foo [<!ENTITY xi SYSTEM "file:///etc/passwd">]>
            <foo xmlns:xi="http://www.w3.org/2001/XInclude"><xi:include href="file:///etc/passwd" parse="text"/>&xi;</foo>
            """,
            true
        },
        {
            // An internal-only DTD (no external identifier): still a DTD, so still prohibited.
            """
            <!DOCTYPE foo [<!ELEMENT foo (#PCDATA)>]><foo>hello</foo>
            """,
            false
        },
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
        // XmlReaderSettings.XmlResolver has no get accessor, so a resolver can never be read back out of the
        // profile's settings; the spy test below proves the profile never reaches one either.
        typeof(XmlReaderSettings).GetProperty(nameof(XmlReaderSettings.XmlResolver))!.CanRead.ShouldBeFalse();
        settings.MaxCharactersFromEntities.ShouldBe(0);
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
    public void RejectsExternalEntities(string payload, bool resolvesWhenPermissive)
    {
        // The spy resolver is installed on a permissive reader the test owns — it can never be installed into the
        // profile, which takes no resolver parameter and builds settings with XmlResolver = null. The permissive
        // read proves the payload really is a resolution attempt; the profile then refuses it with zero calls.
        var spy = new CountingXmlResolver();

        if (resolvesWhenPermissive)
        {
            ReadPermissively(payload, spy).ShouldBeGreaterThan(0, "the payload must be a genuine resolution attempt");
        }

        var callsBeforeProfile = spy.EntityCalls;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Environment.TickCount64;

        var result = ParseXml(payload);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.DtdProhibited);

        // Zero resolution calls, zero file or HTTP access, and a cost bounded by the payload — not by the DTD.
        spy.EntityCalls.ShouldBe(callsBeforeProfile);
        allocated.ShouldBeLessThan(4 * 1024 * 1024, "a rejected DTD must not expand into memory");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SpyResolverIsNeverReachedByTheProfile()
    {
        var spy = new CountingXmlResolver();
        const string Payload = """<!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><foo>&xxe;</foo>""";

        // The profile builds its own settings and takes no resolver parameter, and XmlResolver is write-only,
        // so no resolver can be handed to the profile or read back from it: the spy below can only ever reach
        // the permissive reader this test owns.
        ReadPermissively(Payload, spy).ShouldBeGreaterThan(0);

        var callsAfterPermissiveRead = spy.EntityCalls;

        ParseXml(Payload).IsSuccess.ShouldBeFalse();

        spy.EntityCalls.ShouldBe(callsAfterPermissiveRead);
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

    [Fact]
    public void EnforcesSizeCap()
    {
        const long Cap = 64 * 1024;

        // Exactly the cap is allowed.
        ParseXml(ExactSizeDocument(Cap), Cap).IsSuccess.ShouldBeTrue();

        // One byte more is 413 too_large with the size reason.
        var overCap = ParseXml(ExactSizeDocument(Cap + 1), Cap);

        overCap.IsSuccess.ShouldBeFalse();
        overCap.Error!.Code.ShouldBe(ParsingErrorCode.TooLarge);
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

    /// <summary>Parses a UTF-8 payload against the profile at the test's kind cap.</summary>
    private static Result<ParsedXmlReport> ParseXml(string payload, long maxBytes = MaxBytes) =>
        SafeXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxBytes, CancellationToken.None);

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

    /// <summary>
    /// Reads a payload with DTDs allowed and <paramref name="resolver"/> installed, returning how many times the
    /// resolver was asked for an external resource. Used only to prove a hostile payload is genuine: the resolver
    /// performs no I/O (it returns null), and the profile itself can never be configured this way.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA3075:Insecure DTD processing in XML",
        Justification = "Deliberate: a permissive reader with a non-I/O spy resolver is the only way to prove the "
            + "test's hostile payloads are genuine resolution attempts. The production profile is the opposite of "
            + "this reader (DtdProcessing.Prohibit, XmlResolver = null) and never reaches it.")]
    private static int ReadPermissively(string payload, CountingXmlResolver resolver)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = resolver,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(payload), settings);
            while (reader.Read())
            {
                // Walking the document is what triggers the external resolution.
            }
        }
        catch (XmlException)
        {
            // A permissive reader without a working resolver fails on the unresolvable entity; the count is the point.
        }

        return resolver.EntityCalls;
    }

    /// <summary>A document nested <paramref name="levels"/> deep under its root element.</summary>
    private static string NestedDocument(int levels) =>
        "<r>" + string.Concat(Enumerable.Repeat("<a>", levels)) + string.Concat(Enumerable.Repeat("</a>", levels)) + "</r>";

    /// <summary>A well-formed ASCII document of exactly <paramref name="bytes"/> bytes.</summary>
    private static string ExactSizeDocument(long bytes)
    {
        const string Head = "<?xml version=\"1.0\"?><r><pad>";
        const string Tail = "</pad></r>";
        var padding = bytes - Head.Length - Tail.Length;

        return Head + new string('a', (int)padding) + Tail;
    }

    /// <summary>An <see cref="XmlResolver"/> that counts external resolutions and never performs I/O.</summary>
    private sealed class CountingXmlResolver : XmlResolver
    {
        /// <summary>How many times an external entity or DTD was requested.</summary>
        public int EntityCalls { get; private set; }

        /// <inheritdoc />
        public override System.Net.ICredentials? Credentials
        {
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            EntityCalls++;
            return null;
        }

        /// <inheritdoc />
        public override Task<object> GetEntityAsync(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            EntityCalls++;
            return Task.FromResult<object>(new MemoryStream());
        }
    }
}

/// <summary>
/// A stream that holds more bytes than the cap while reporting <c>Length</c> 0: shared by the 06.3.1-T1
/// profile tests to prove the size cap counts bytes actually read, never a length the caller controls.
/// </summary>
internal sealed class LyingLengthStream(byte[] content) : Stream
{
    private int offset;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => 0;

    public override long Position
    {
        get => offset;
        set => offset = (int)value;
    }

    public override int Read(byte[] buffer, int offsetInBuffer, int count)
    {
        var take = Math.Min(count, content.Length - offset);
        if (take <= 0)
        {
            return 0;
        }

        content.AsSpan(offset, take).CopyTo(buffer.AsSpan(offsetInBuffer));
        offset += take;

        return take;
    }

    public override void Flush()
    {
        // Read-only.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offsetInBuffer, int count) => throw new NotSupportedException();
}

/// <summary>
/// A stream that hands out small reads slowly, so the profile's wall-clock guard can be proven without waiting
/// for the real timeout: shared by the 06.3.1-T1 profile tests.
/// </summary>
internal sealed class SlowStream(byte[] content, int chunkBytes, TimeSpan delayPerRead) : Stream
{
    private int offset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => content.Length;

    public override long Position
    {
        get => offset;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offsetInBuffer, int count)
    {
        Thread.Sleep(delayPerRead);

        var take = Math.Min(Math.Min(count, chunkBytes), content.Length - offset);
        if (take <= 0)
        {
            return 0;
        }

        content.AsSpan(offset, take).CopyTo(buffer.AsSpan(offsetInBuffer));
        offset += take;

        return take;
    }

    public override void Flush()
    {
        // Read-only.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offsetInBuffer, int count) => throw new NotSupportedException();
}
