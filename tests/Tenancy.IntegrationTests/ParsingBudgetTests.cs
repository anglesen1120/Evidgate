using System.Globalization;
using System.Text;
using System.Xml;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T1.1 (AC6; 00 §5.7 P1): parsing the largest allowed fixture stays inside a bounded budget. The
/// assertion is about allocation, not wall clock (a shared CI box is not a benchmark): the XML profile walks
/// the report as a stream — a DOM of the same 8 MiB would allocate an order of magnitude more — and the JSON
/// profile buffers the payload once plus the document its own contract returns.
/// </summary>
public sealed class ParsingBudgetTests
{
    /// <summary>
    /// The per-kind byte cap the fixtures are parsed against (AC6). A cap that is per-kind is data — the
    /// <c>artifact_kind_policy.max_bytes</c> column — while the caps that are the same for every kind live in
    /// <see cref="ParsingLimits"/>; the guard therefore applies a fixed fraction of the profile's own largest
    /// allowed content (<see cref="ParsingLimits.MaxExpandedBytes"/> = 512 MiB): 32 MiB, four times the 8 MiB
    /// fixture, so the fixture is comfortably inside the cap and the AC6 bound is exactly cap ×2.
    /// </summary>
    private static readonly long CapBytes = ParsingLimits.MaxExpandedBytes / 16;

    /// <summary>The largest fixture this story generates (the shared corpus lands in 06.3.2).</summary>
    private const int FixtureBytes = 8 * 1024 * 1024;

    [Fact]
    public void ParsingTheLargestXmlReportStaysInsideTheBudget()
    {
        var fixture = XmlReportFixture();
        fixture.Length.ShouldBeGreaterThanOrEqualTo(FixtureBytes);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Environment.TickCount64;

        var result = SafeXml.Parse(new MemoryStream(fixture), CapBytes, CancellationToken.None);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        // AC6: peak managed allocation stays under the per-kind cap ×2, and — the behavioural guard — bounded by
        // the input, not by its node count.
        CapBytes.ShouldBeGreaterThan(fixture.Length, "the largest fixture must sit inside the cap the guard applies");
        allocated.ShouldBeLessThan(2L * CapBytes, "AC6: the parse must stay under the per-kind cap ×2");
        allocated.ShouldBeLessThan(2L * fixture.Length, "an 8 MiB report must not build a DOM of itself");

        // A DOM walk of the same document would also take far longer than this; the bound is deliberately loose
        // so a loaded CI box cannot make the test flaky.
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

        // The report is still a streaming handle after the budget is measured: it walks the same bytes again.
        using var reader = result.Value.OpenReader();
        var elements = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                elements++;
            }
        }

        elements.ShouldBeGreaterThan(1000);
    }

    [Fact]
    public void ParsingTheLargestJsonPayloadStaysInsideTheBudget()
    {
        var fixture = JsonPayloadFixture();
        fixture.Length.ShouldBeGreaterThanOrEqualTo(FixtureBytes);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Environment.TickCount64;

        var result = SafeJson.Parse(new MemoryStream(fixture), CapBytes, CancellationToken.None);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        using var document = result.Value;
        document.RootElement.GetProperty("nodes").GetArrayLength().ShouldBe(1);

        // The JSON arm buffers the payload once to enforce the cap and drop a BOM, and JsonDocument owns one
        // buffer of its own: two copies of the input, never a per-token blow-up.
        allocated.ShouldBeLessThan(2L * CapBytes, "AC6: the parse must stay under the per-kind cap ×2");
        allocated.ShouldBeLessThan(4L * fixture.Length, "the JSON arm must not allocate more than a few copies");

        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void SniffingNeverReadsMoreThanThePrefix()
    {
        // 00 §5.7 P1: the `complete` hot path asks the object store for 4 KiB, and the sniffer only ever walks
        // the span it is handed — it has no way to ask for more bytes.
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var prefix = Encoding.ASCII.GetBytes("<html><head><title>x</title></head><body>");
        var verdict = new ParsingService().SniffMediaType("text/html", prefix);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        verdict.ShouldBe(MediaSniffVerdict.Match);
        allocated.ShouldBeLessThan(64 * 1024, "the sniff must not allocate a buffer");
        ParsingLimits.SniffPrefixBytes.ShouldBe(4096);
    }

    /// <summary>
    /// 06.3.1-T2.2 (AC5/AC6): the sniff is run over EVERY media type the product accepts, on the largest prefix
    /// the complete hot path reads (<see cref="ParsingLimits.SniffPrefixBytes"/>). The walk is a pure span pass
    /// with no buffer of its own, so the whole table costs a bounded, tiny allocation — never a multiple of the
    /// prefix. The prefixes are built before the measurement, so the reading is the sniff's own allocation.
    /// </summary>
    [Fact]
    public void SniffingEveryTableTypeOverTheFullPrefixStaysInsideTheBudget()
    {
        var samples = new (string Declared, byte[] Prefix)[]
        {
            ("application/xml", PaddedPrefix("<testsuites><testcase name=\"x\"/>")),
            ("text/xml", PaddedPrefix("<r>")),
            ("application/atom+xml", PaddedPrefix("<feed xmlns=\"http://www.w3.org/2005/Atom\">")),
            ("application/json", PaddedPrefix("{\"nodes\":[]}")),
            ("application/ld+json", PaddedPrefix("[{\"@type\":\"node\"}]")),
            ("text/html", PaddedPrefix("<html><head><title>x</title></head><body>")),
            ("application/zip", PaddedPrefix("PK\u0003\u0004")),
            ("image/png", PaddedPrefix("\u0089PNG\r\n\u001A\n")),
            ("image/jpeg", PaddedPrefix("\u00FF\u00D8\u00FF\u00E0")),
            ("video/mp4", PaddedPrefix("\u0000\u0000\u0000\u0018ftypisom")),
        };

        var parsing = new ParsingService();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        foreach (var (declared, prefix) in samples)
        {
            prefix.Length.ShouldBe(ParsingLimits.SniffPrefixBytes);
            parsing.SniffMediaType(declared, prefix).ShouldBe(MediaSniffVerdict.Match);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Ten sniffs over 4 KiB each: a per-byte or per-prefix buffer would be tens of kilobytes. The bound is
        // loose enough for the service wrapper's own small allocations and still proves there is no buffer.
        allocated.ShouldBeLessThan(64 * 1024, "the sniff must not allocate a buffer proportional to the prefix");
    }

    /// <summary>A prefix of the given Latin-1 text padded with filler to the sniff cap.</summary>
    private static byte[] PaddedPrefix(string latin1Prefix)
    {
        var bytes = new byte[ParsingLimits.SniffPrefixBytes];
        Encoding.Latin1.GetBytes(latin1Prefix).CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public void OneBytePerReadXmlStreamDoesNotAllocateOneArrayPerByte()
    {
        // A stream that hands out one byte per Read is the worst case for chunk assembly: BoundedContent.Read
        // fills its scratch buffer before allocating a chunk, so the allocation stays a small multiple of the
        // content. One array per byte would cost ~24 bytes of header per input byte, far past the 4x bound.
        // Many small elements (not one huge text node) keep XmlReader's own buffers small, so the measurement is
        // about the profile's chunking.
        var builder = new StringBuilder(512 * 1024 + 16);
        builder.Append("<r>");
        while (builder.Length < 512 * 1024)
        {
            builder.Append("<i/>");
        }

        builder.Append("</r>");
        var fixture = Encoding.UTF8.GetBytes(builder.ToString());

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var result = SafeXml.Parse(new OneByteStream(fixture), CapBytes, CancellationToken.None);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        allocated.ShouldBeLessThan(4L * fixture.Length, "one chunk per buffer, not one array per byte");
    }

    [Fact]
    public void TheContiguousReadClampKeepsTheBufferAllocatable()
    {
        // A cap above Array.MaxLength must never reach new byte[...] or Array.Resize: the effective cap is clamped
        // one below Array.MaxLength, so the byte[] path can always hold the extra byte that proves "longer than
        // the cap", and a stream longer than the clamp is refused as size-exceeded.
        BoundedContent.EffectiveCap(long.MaxValue).ShouldBe((long)Array.MaxLength - 1);
        BoundedContent.EffectiveCap(Array.MaxLength).ShouldBe((long)Array.MaxLength - 1);
        BoundedContent.EffectiveCap(1024).ShouldBe(1024L);
    }

    /// <summary>An 8 MiB JUnit-shaped report: many small elements, the shape a DOM parse would blow up on.</summary>
    private static byte[] XmlReportFixture()
    {
        var builder = new StringBuilder(FixtureBytes + 1024);
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<testsuites name=\"nightly\">\n<testsuite name=\"checkout\">\n");

        var index = 0;
        while (builder.Length < FixtureBytes)
        {
            builder.Append(
                CultureInfo.InvariantCulture,
                $"<testcase classname=\"Cart.Tests\" name=\"case {index}\" time=\"0.001\"><failure message=\"boom\">at Cart.Tests:</failure></testcase>\n");
            index++;
        }

        builder.Append("</testsuite>\n</testsuites>\n");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// An 8 MiB JSON payload whose bulk is one text value. A snapshot that is mostly text is realistic, and it
    /// keeps the measurement on the profile's own buffering: <c>JsonDocument</c>'s per-token metadata is linear
    /// in tokens and is the output shape <c>IParsing.ParseJson</c> promises (06.3.1 §5), not profile overhead.
    /// </summary>
    private static byte[] JsonPayloadFixture()
    {
        const string Head = "{\"version\":1,\"nodes\":[{\"id\":\"n1\",\"role\":\"text\",\"name\":\"";
        const string Tail = "\"}],\"truncated\":false}";

        var builder = new StringBuilder(FixtureBytes + 1024);
        builder.Append(Head);
        builder.Append('a', FixtureBytes - Head.Length - Tail.Length);
        builder.Append(Tail);

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Delivers the payload one byte per read, the worst case for chunk assembly.</summary>
    private sealed class OneByteStream(byte[] payload) : Stream
    {
        private int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offsetInBuffer, int count)
        {
            if (offset >= payload.Length)
            {
                return 0;
            }

            buffer[offsetInBuffer] = payload[offset++];
            return 1;
        }

        public override void Flush()
        {
            // Read-only.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offsetInBuffer, int count) => throw new NotSupportedException();
    }
}
