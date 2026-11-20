using System.Text;
using System.Text.Json;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The push half of the pytest mapping: a child record stream maps exactly what the byte path maps, and every guard
/// the rebuilt document used to enforce (names, namespaces, duplicates, depth, bytes, one balanced root) is still
/// enforced — as a deferred <c>parse_crashed</c> fault, never as an exception out of <c>Accept</c>, because the
/// runner's own verdict and a child <c>rejected</c> record both outrank it.
/// </summary>
public sealed class PytestJUnitRecordMapperTests
{
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";
    private const string EndRecord = """{"t":"xml","e":"end"}""";

    private static readonly JUnitMapContext Context = new(
        Guid.Empty,
        (classname, name) => name == "unmapped" ? null : classname + "::" + name);

    public static TheoryData<string> ParityDocuments => new()
    {
        // A repeated execution: one event, duplicate_case, and the repeat's more severe status decides.
        "<testsuite tests=\"3\"><testcase name=\"same\"/><testcase name=\"unmapped\"/><testcase name=\"same\"><error/></testcase></testsuite>",

        // Nested testsuites under a testsuites root, with the root's own counters winning over the sum of its shards.
        "<testsuites tests=\"2\" failures=\"1\"><testsuite tests=\"9\"><testcase name=\"one\"><failure/></testcase></testsuite><testsuite tests=\"4\"><testcase name=\"two\"/></testsuite></testsuites>",

        // A testsuites root that declares nothing sums its direct testsuite children.
        "<testsuites><testsuite tests=\"2\" errors=\"1\"><testcase name=\"a\"><error/></testcase><testcase name=\"b\"/></testsuite><testsuite tests=\"1\"><testcase name=\"c\"><skipped/></testcase></testsuite></testsuites>",

        // Outcome elements of one testcase, a deeper element named failure (not a status), and an empty testcase.
        "<testsuite><testcase name=\"mixed\"><skipped/><failure/><error/></testcase><testcase name=\"deep\"><system-out><failure/></system-out></testcase><testcase name=\"empty\"/></testsuite>",

        // Escaped attribute text, unusable counters and a fractional duration.
        "<testsuite tests=\"-1\" errors=\"bad\"><testcase classname=\"a &amp; b\" name=\"n&#x9;&#xA;&#xD;&quot;&lt;&gt;\" time=\"0.1234\"/></testsuite>",

        // A testcase inside a testcase is consumed by the outer one and never mapped on its own.
        "<testsuite><testcase name=\"outer\"><testcase name=\"ignored\"><failure/></testcase></testcase></testsuite>",

        // The declared count disagrees with the testcases the document carries.
        "<testsuite tests=\"2\"><testcase name=\"only\"/></testsuite>",

        // A document longer than its own cap is still mapped; the profile decides the cap, not the mapper.
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><testsuite name=\"pytest\" tests=\"1\">\n  <testcase classname=\"tests.test_a\" name=\"t\" time=\"0.5\"><failure message=\"x&#10;y\">trace</failure></testcase>\n</testsuite>",
    };

    [Theory]
    [MemberData(nameof(ParityDocuments))]
    public async Task RecordSink_Document_MapsIdenticallyToBytes(string xml) => await AssertParityAsync(xml);

    [Fact]
    public async Task DeclaredPrefixes_ElementsAndAttributes_MapIdenticallyToBytes() =>
        await AssertParityAsync(
            "<p:testsuites xmlns:p=\"urn:p\" xmlns:q=\"urn:q\"><p:testsuite tests=\"2\">"
            + "<p:testcase q:name=\"not-the-name\" name=\"first\"><p:failure/></p:testcase>"
            + "<testcase xmlns=\"urn:default\" name=\"second\"><q:skipped/></testcase></p:testsuite></p:testsuites>");

    [Fact]
    public async Task NamespaceScope_ReboundAndRestored_MapIdenticallyToBytes() =>
        await AssertParityAsync(
            "<testsuite xmlns:p=\"urn:outer\"><p:testcase xmlns:p=\"urn:inner\" name=\"first\"/>"
            + "<p:testcase name=\"second\" xml:lang=\"en\"/></testsuite>");

    [Fact]
    public async Task LongIdentities_EchoIsBounded_MapIdenticallyToBytes() =>
        await AssertParityAsync(
            $"<testsuite><testcase classname=\"{new string('c', 400)}\" name=\"{new string('n', 400)}\"/></testsuite>");

    [Fact]
    public async Task GoldenFixtures_RecordsVsBytes_Parity()
    {
        var reports = Directory.GetFiles(Fixtures("junit-pytest"), "report.xml", SearchOption.AllDirectories);
        reports.Length.ShouldBeGreaterThanOrEqualTo(2, "the pytest corpus must keep a mappable and a refused report");

        foreach (var report in reports)
        {
            await AssertParityAsync(await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void UnbalancedEnd_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite"), EndRecord);
        AssertCrashed(EndRecord);
        AssertCrashed(Start("testsuite"), EndRecord, EndRecord);

        // Nothing is left open: a stream that stops mid-tree is refused when the run's verdict is read.
        AssertCrashed(Start("testsuite"), Start("child"));
    }

    [Fact]
    public void MismatchedEndName_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite"), """{"t":"xml","e":"end","n":"testsuite"}""");
        AssertCrashed(Start("testsuite"), """{"t":"xml","e":"end","n":"testcase"}""");
    }

    [Fact]
    public void DuplicateAttribute_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["tests", "1"], ["failures", "2"]), EndRecord);
        AssertCrashed(Start("testsuite", ["tests", "1"], ["tests", "2"]), EndRecord);
    }

    [Fact]
    public void DuplicateAttributeThroughPrefixAliasing_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["xmlns:a", "urn:a"], ["xmlns:b", "urn:b"], ["a:n", "1"], ["b:n", "1"]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:a", "urn:same"], ["xmlns:b", "urn:same"], ["a:n", "1"], ["b:n", "2"]), EndRecord);
    }

    [Fact]
    public void UndeclaredElementPrefix_Deferred_Crashed()
    {
        AssertAccepts(Start("p:testsuite", ["xmlns:p", "urn:p"]), EndRecord);
        AssertCrashed(Start("p:testsuite"), EndRecord);
    }

    [Fact]
    public void UndeclaredAttributePrefix_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["xmlns:p", "urn:p"], ["p:tests", "1"]), EndRecord);
        AssertCrashed(Start("testsuite", ["p:tests", "1"]), EndRecord);
    }

    [Fact]
    public void PrefixDeclarationScope_EndsWithItsElement_Deferred_Crashed()
    {
        AssertAccepts(
            Start("testsuite"),
            Start("child", ["xmlns:p", "urn:p"]),
            Start("p:grandchild"),
            EndRecord,
            EndRecord,
            EndRecord);
        AssertCrashed(
            Start("testsuite"),
            Start("child", ["xmlns:p", "urn:p"]),
            EndRecord,
            Start("p:child"),
            EndRecord,
            EndRecord);
    }

    [Fact]
    public void RecordAfterTheRootClosed_SecondRoot_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite"), EndRecord);
        AssertCrashed(Start("testsuite"), EndRecord, Start("testsuite"), EndRecord);
    }

    [Fact]
    public void StartBeyondTheRebuiltDepthCap_Deferred_Crashed()
    {
        AssertAccepts(DeepStream(ChildXmlRecords.MaxRebuiltDepth - 1));
        AssertCrashed(DeepStream(ChildXmlRecords.MaxRebuiltDepth));
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("a:b:c")]
    [InlineData(":testsuite")]
    [InlineData("testsuite:")]
    [InlineData("1testsuite")]
    public void InvalidName_Deferred_Crashed(string name)
    {
        AssertAccepts(Start("testsuite"), EndRecord);
        AssertCrashed(Start(name), EndRecord);
    }

    [Fact]
    public void InvalidAttributeName_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["xmlns:a", "urn:a"], ["a:b", "x"]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:a", "urn:a"], ["a:b:c", "x"]), EndRecord);
    }

    [Fact]
    public void InvalidCharInValue_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["name", "ok"]), EndRecord);
        AssertCrashed(Start("testsuite", ["name", "\u0001"]), EndRecord);
    }

    [Fact]
    public void ReservedNamespaceDeclaration_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["xmlns:xml", XmlNamespace]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:xml", "urn:wrong"]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:xmlns", "urn:wrong"]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:p", ""]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:p", XmlNamespace]), EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns", XmlnsNamespace]), EndRecord);
        AssertCrashed(Start("xmlns:testsuite"), EndRecord);
    }

    [Fact]
    public void DefaultNamespaceUndeclared_Accepted()
    {
        AssertAccepts(Start("testsuite", ["xmlns", "urn:outer"]), Start("nested", ["xmlns", ""]), EndRecord, EndRecord);
    }

    [Fact]
    public void TextRecordBadShape_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite"), Text("x"), EndRecord);
        AssertCrashed(Start("testsuite"), """{"t":"xml","e":"text","v":3}""", EndRecord);
        AssertCrashed(Start("testsuite"), """{"t":"xml","e":"text"}""", EndRecord);
        AssertCrashed(Start("testsuite"), """{"t":"xml","e":"text","v":{}}""", EndRecord);
    }

    [Fact]
    public void TextOutsideTheRoot_IsIgnored()
    {
        var sink = Open();
        Feed(sink, """{"t":"xml","e":"text","v":"not whitespace"}""", Start("testsuite"), EndRecord,
            """{"t":"xml","e":"text","v":"after the root"}""");
        sink.Finish().Cases.ShouldBeEmpty();
    }

    [Fact]
    public void NotAnXmlRecord_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite"), EndRecord);
        AssertCrashed("""{"t":"json","e":"start","n":"testsuite"}""");
        AssertCrashed("""{"t":"xml","e":"other"}""");
    }

    [Fact]
    public void AttributeListShape_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite", ["k", "v"]), EndRecord);
        AssertCrashed("""{"t":"xml","e":"start","n":"testsuite","a":[["k"]]}""");
        AssertCrashed("""{"t":"xml","e":"start","n":"testsuite","a":[["k","v","x"]]}""");
        AssertCrashed("""{"t":"xml","e":"start","n":"testsuite","a":{}}""");
    }

    [Fact]
    public void NotOneJsonObject_Deferred_Crashed()
    {
        AssertAccepts(Start("testsuite"), EndRecord);
        AssertCrashed("[1,2]");
        AssertCrashed("{");
    }

    [Fact]
    public void RebuiltByteBudget_CountsTheRebuildsEscapedUtf8()
    {
        StartTagBytes(Start("testsuite", ["k", "&"])).ShouldBe(21);
        StartTagBytes(Start("testsuite", ["k", "é"])).ShouldBe(18);
        StartTagBytes(Start("testsuite", ["k", "日"])).ShouldBe(19);
        StartTagBytes(Start("testsuite", ["k", "😀"])).ShouldBe(20);
        StartTagBytes(Start("testsuite", ["k", "\t"])).ShouldBe(21);
        ChildXmlRecords.EndTagBytes("testsuite").ShouldBe(12);
    }

    [Fact]
    public void RebuiltByteBudget_OverTheCap_Deferred_Crashed()
    {
        var start = Start("testsuite", ["name", new string('&', 220_000)]);
        var cost = StartTagBytes(start);
        var end = ChildXmlRecords.EndTagBytes("testsuite");
        var slack = ChildXmlRecords.DocumentSlackBytes;
        cost.ShouldBeGreaterThan(slack);

        // The rebuild refused a tag that would take the document past the cap, so an exact fit is accepted while a
        // cap one byte lower refused the very tag that ends the document - and a cap under the start tag refuses it.
        AssertBudget(cost + end - slack, accepts: true);
        AssertBudget(cost + end - slack - 1, accepts: false);
        AssertBudget(cost - slack - 1, accepts: false);

        void AssertBudget(long maxReportBytes, bool accepts)
        {
            var sink = new PytestJUnitMapper(new ParsingService(), maxReportBytes).OpenRecordMapper(Context);
            Feed(sink, start, EndRecord);

            if (accepts)
            {
                sink.Finish().Cases.ShouldBeEmpty();
                return;
            }

            Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(ParseRunRejection.Crashed);
        }
    }

    [Fact]
    public void RootRefusal_Deferred_KeepsTheDialectsReason()
    {
        AssertAccepts(Start("testsuites"), EndRecord);

        var sink = Open();
        Feed(sink, Start("foreign"), EndRecord);

        Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(PytestJUnitMapper.RootRefusalReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectionRecordWins_MapperFaultStillSniffed(bool rejectionFirst)
    {
        const string Rejected = """{"t":"rejected","code":"artifact_invalid","reason":"dtd_prohibited"}""";
        var sink = Open();

        // Accept must never throw for content: the pump would lose the runner's verdict to that exception.
        Feed(sink, rejectionFirst ? Rejected : EndRecord, rejectionFirst ? EndRecord : Rejected);

        sink.RejectedReason.ShouldBe("dtd_prohibited");
        Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(ParseRunRejection.Crashed);
    }

    private static async Task AssertParityAsync(string xml)
    {
        var parsing = new ParsingService();
        var mapper = new PytestJUnitMapper(parsing);
        var sink = mapper.OpenRecordMapper(Context);

        // The emulated child parses the same bytes through the real profile, so the record stream under test is the
        // one the production child writes, including a document the profile refuses.
        var run = (ParseRunSucceeded)await new EmulatedChildRunner(parsing).RunAsync(
            new ParseRunRequest(
                ParseRunnerProfile.Xml,
                ParsingLimits.MaxReportBytes,
                "application/xml",
                new MemoryStream(Encoding.UTF8.GetBytes(xml)),
                sink: sink),
            TestContext.Current.CancellationToken);

        run.Records.ShouldBeEmpty("the request named a sink, so the runner keeps no records");

        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        if (sink.RejectedReason is { } reason)
        {
            Should.Throw<JUnitParseRefusedException>(() => mapper.Map(bytes, Context)).Reason.ShouldBe(reason);
            return;
        }

        var expected = mapper.Map(bytes, Context);
        var actual = sink.Finish();

        actual.Cases.ShouldBe(expected.Cases);
        actual.Declared.ShouldBe(expected.Declared);
        actual.Discrepancies.ShouldBe(expected.Discrepancies);
    }

    private static void AssertAccepts(params string[] records)
    {
        var sink = Open();
        Feed(sink, records);
        sink.Finish().Cases.ShouldBeEmpty();
    }

    private static void AssertCrashed(params string[] records)
    {
        var sink = Open();
        Feed(sink, records);
        Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(ParseRunRejection.Crashed);
    }

    private static string[] DeepStream(int nested)
    {
        var records = new List<string> { Start("testsuite") };
        records.AddRange(Enumerable.Repeat(Start("nested"), nested));
        records.AddRange(Enumerable.Repeat(EndRecord, nested));
        records.Add(EndRecord);

        return [.. records];
    }

    private static IJUnitRecordMapper Open() =>
        new PytestJUnitMapper(new ParsingService()).OpenRecordMapper(Context);

    private static void Feed(IJUnitRecordMapper sink, params string[] records)
    {
        foreach (var record in records)
        {
            sink.Accept(Encoding.UTF8.GetBytes(record));
        }
    }

    private static long StartTagBytes(string record) =>
        ChildXmlRecords.StartTagBytes(ChildXmlRecords.Decode(Encoding.UTF8.GetBytes(record)));

    private static string Text(string value) => JsonSerializer.Serialize(new { t = "xml", e = "text", v = value });

    private static string Start(string name, params string[][] attributes) =>
        JsonSerializer.Serialize(new { t = "xml", e = "start", n = name, a = attributes });

    private static string Fixtures(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the repository root was not found"),
            "fixtures",
            relativePath);
    }
}
