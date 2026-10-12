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
    /// <summary>The kind cap the fixtures are parsed against: a report kind's policy limit, four times the fixture.</summary>
    private const long CapBytes = 32 * 1024 * 1024;

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

        // AC6: bounded by the cap, and — the real guard — bounded by the input, not by its node count.
        allocated.ShouldBeLessThan(2 * CapBytes, "the parse must not allocate proportionally to the cap");
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
        allocated.ShouldBeLessThan(2 * CapBytes, "the parse must not allocate proportionally to the cap");
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
}
