using System.Collections;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The faults the push mapper must decide the way the rebuilt document did, without letting one escape the sink: an
/// undecodable rejection record, the reserved <c>xml:space</c> value, a structural fault behind a foreign root, and
/// namespaces compared by identity rather than by a URI's bytes.
/// </summary>
public sealed class PytestJUnitRecordMapperFaultTests
{
    private const string EndRecord = """{"t":"xml","e":"end"}""";
    private const string DoneRecord = """{"t":"done"}""";
    private const string RejectedRecord = """{"t":"rejected","reason":"dtd_prohibited"}""";
    private static readonly JUnitMapContext Context = new(Guid.Empty, (classname, name) => classname + "::" + name);

    [Theory]
    [InlineData("""{"t":"rejected","reason":"\uD800"}""")]
    [InlineData("""{"t":"\ud800","e":"start","n":"rejected"}""")]
    public void RejectionSniff_InvalidSurrogate_DefersCrashed(string record)
    {
        ChildRejectionRecord.ReasonOf(Encoding.UTF8.GetBytes(record)).ShouldBeNull();
        AssertCrashed(record);

        var sink = Open();
        Feed(sink, record, RejectedRecord);
        sink.RejectedReason.ShouldBe("dtd_prohibited");
        Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(ParseRunRejection.Crashed);
    }

    [Theory]
    [InlineData("dtd_prohibited", "dtd_prohibited")]
    [InlineData("bad reason", "parse_crashed")]
    [InlineData("", "parse_crashed")]
    public void RejectionReason_ValidJson_PreservesBufferedConsumerRules(string reason, string expected)
    {
        var record = JsonSerializer.Serialize(new { t = "rejected", reason });
        ChildRejectionRecord.ReasonOf(Encoding.UTF8.GetBytes(record)).ShouldBe(expected);
        var sink = Open();
        Feed(sink, Start("testsuite"), EndRecord, record);
        sink.RejectedReason.ShouldBe(expected);
        sink.Finish().Cases.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("Preserve")]
    [InlineData("\u00a0preserve\u00a0")]
    public void XmlSpace_InvalidReservedValue_DefersCrashed(string value)
    {
        AssertAccepts(Start("testsuite", ["xml:space", "preserve"]), EndRecord);
        AssertCrashed(Start("testsuite", ["xml:space", value]), EndRecord);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("preserve")]
    [InlineData(" preserve ")]
    [InlineData("\t\r\ndefault\n\r\t")]
    public void XmlSpace_ValidTrimmedReservedValue_Accepts(string value) =>
        AssertAccepts(Start("testsuite", ["xml:space", value]), EndRecord);

    [Fact]
    public void ForeignRoot_Unclosed_DefersCrashed()
    {
        AssertForeignRefusal(Start("foreign"), EndRecord);
        AssertCrashed(Start("foreign"));
    }

    [Fact]
    public void ForeignRoot_DoneRecordWithoutClose_DefersCrashed() =>
        AssertCrashed(Start("foreign"), DoneRecord);

    [Fact]
    public void ForeignRoot_SecondRoot_DefersCrashed()
    {
        AssertForeignRefusal(Start("foreign"), EndRecord);
        AssertCrashed(Start("foreign"), EndRecord, Start("testsuite"), EndRecord);
    }

    [Fact]
    public void ForeignRoot_MismatchedEnd_DefersCrashed()
    {
        AssertForeignRefusal(Start("foreign"), EndRecord);
        AssertCrashed(Start("foreign"), """{"t":"xml","e":"end","n":"other"}""");
    }

    [Fact]
    public void ForeignRoot_OverByteBudget_DefersCrashed()
    {
        var root = Start("foreign");
        var child = Start("nested", ["name", new string('&', 220_000)]);
        var cost = StartTagBytes(root) + StartTagBytes(child)
            + ChildXmlRecords.EndTagBytes("nested") + ChildXmlRecords.EndTagBytes("foreign");
        var reportCap = cost - ChildXmlRecords.DocumentSlackBytes;
        var control = Open(reportCap);
        Feed(control, root, child, EndRecord, EndRecord);
        Should.Throw<JUnitParseRefusedException>(() => control.Finish()).Reason.ShouldBe(PytestJUnitMapper.RootRefusalReason);
        var sink = Open(reportCap - 1);
        Feed(sink, root, child, EndRecord, EndRecord);
        Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(ParseRunRejection.Crashed);
    }

    [Fact]
    public void ForeignRoot_OverDepthBudget_DefersCrashed()
    {
        AssertForeignRefusal(DeepStream(ChildXmlRecords.MaxRebuiltDepth - 1));
        AssertCrashed(DeepStream(ChildXmlRecords.MaxRebuiltDepth));
    }

    [Fact]
    public void ForeignRoot_BadRecord_DefersCrashed()
    {
        AssertForeignRefusal(Start("foreign"), Start("testcase", ["name", "not-mapped"]), EndRecord, EndRecord);
        AssertCrashed(Start("foreign"), """{"t":"xml","e":"text","v":3}""", EndRecord);
    }

    [Fact]
    public void NamespaceIdentity_LargeUriWithDistinctAttributes_Accepts()
    {
        var uri = "urn:" + new string('n', 1024 * 1024);
        var attributes = new List<string[]> { new[] { "xmlns:p", uri } };
        attributes.AddRange(Enumerable.Range(0, 5_000).Select(index => new[] { "p:a" + index, "v" }));
        AssertAccepts(Start("testsuite", [.. attributes]), EndRecord);
    }

    [Fact]
    public void NamespaceIdentity_AliasesAndReboundScope_RefusesOnlyDuplicates()
    {
        AssertAccepts(
            Start("testsuite", ["xmlns:a", "urn:outer"], ["xmlns:b", "urn:outer"]),
            Start("nested", ["xmlns:b", "urn:inner"], ["a:n", "1"], ["b:n", "2"]), EndRecord,
            Start("nested", ["a:first", "1"], ["b:second", "2"]), EndRecord, EndRecord);
        AssertCrashed(
            Start("testsuite", ["xmlns:a", "urn:outer"], ["xmlns:b", "urn:outer"]),
            Start("nested", ["xmlns:b", "urn:inner"], ["a:n", "1"], ["b:n", "2"]), EndRecord,
            Start("nested", ["a:n", "1"], ["b:n", "2"]), EndRecord, EndRecord);
        AssertCrashed(Start("testsuite", ["xmlns:a", "urn:same"], ["xmlns:b", "urn:same"],
            ["a:n", "1"], ["b:n", "2"]), EndRecord);
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
        sink.RejectedReason.ShouldBeNull();
        Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(ParseRunRejection.Crashed);
    }

    private static void AssertForeignRefusal(params string[] records)
    {
        var sink = Open();
        Feed(sink, records);
        Should.Throw<JUnitParseRefusedException>(() => sink.Finish()).Reason.ShouldBe(PytestJUnitMapper.RootRefusalReason);
    }

    private static string[] DeepStream(int nested) =>
        [Start("foreign"), .. Enumerable.Repeat(Start("nested"), nested), .. Enumerable.Repeat(EndRecord, nested + 1)];

    private static IJUnitRecordMapper Open(long maxReportBytes = ParsingLimits.MaxReportBytes) =>
        new PytestJUnitMapper(new ParsingService(), maxReportBytes).OpenRecordMapper(Context);

    private static void Feed(IJUnitRecordMapper sink, params string[] records)
    {
        foreach (var record in records)
        {
            sink.Accept(Encoding.UTF8.GetBytes(record));
        }
    }

    private static long StartTagBytes(string record) =>
        ChildXmlRecords.StartTagBytes(ChildXmlRecords.Decode(Encoding.UTF8.GetBytes(record)));

    private static string Start(string name, params string[][] attributes) =>
        JsonSerializer.Serialize(new { t = "xml", e = "start", n = name, a = attributes });
}
