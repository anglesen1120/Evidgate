using System.Text;
using System.Text.Json;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Which documents the dialect accepts at all: a root whose local name is neither <c>testsuite</c> nor
/// <c>testsuites</c> is refused as malformed before a single testcase is read, while a namespaced suite root is
/// still the suite root. Both entry points decide on the same depth-0 element, so the record path cannot be the
/// lenient one.
/// </summary>
public sealed class PytestJUnitMapperRootTests
{
    [Fact]
    public void Map_ForeignRootWithTestcases_IsRefusedBeforeAnyCaseIsMapped()
    {
        const string Report =
            """
            <results>
              <testcase classname="M" name="one" />
              <testcase classname="M" name="two" />
            </results>
            """;
        var resolutions = 0;

        var refusal = Should.Throw<JUnitParseRefusedException>(
            () => PytestJUnitMapperReplay.Map(Report, (classname, name) =>
            {
                resolutions++;
                return classname + "::" + name;
            }));

        refusal.Reason.ShouldBe("malformed");
        resolutions.ShouldBe(0);
    }

    [Fact]
    public void Map_ForeignRootWithoutTestcases_IsStillRefused()
    {
        var refusal = Should.Throw<JUnitParseRefusedException>(() => PytestJUnitMapperReplay.Map("<results />"));

        refusal.Reason.ShouldBe(PytestJUnitMapper.RootRefusalReason);
    }

    [Fact]
    public void Map_ForeignRootRefusal_DoesNotEchoTheRootName()
    {
        var refusal = Should.Throw<JUnitParseRefusedException>(
            () => PytestJUnitMapperReplay.Map("<results><testcase classname=\"M\" name=\"one\" /></results>"));

        refusal.Message.ShouldNotContain("results");
    }

    [Fact]
    public void Map_NestedTestsuitesUnderForeignRoot_IsRefused()
    {
        const string Report =
            """
            <results>
              <testsuites tests="1">
                <testsuite name="pytest" tests="1">
                  <testcase classname="M" name="one" />
                </testsuite>
              </testsuites>
            </results>
            """;

        var refusal = Should.Throw<JUnitParseRefusedException>(() => PytestJUnitMapperReplay.Map(Report));

        refusal.Reason.ShouldBe(PytestJUnitMapper.RootRefusalReason);
    }

    [Fact]
    public void Map_DefaultNamespacedTestsuiteRoot_IsMapped()
    {
        const string Report =
            """
            <testsuite xmlns="urn:x" name="pytest" tests="1">
              <testcase classname="M" name="one" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe("M::one");
    }

    [Fact]
    public void Map_PrefixedTestsuitesRoot_IsMapped()
    {
        const string Report =
            """
            <ns:testsuites xmlns:ns="urn:x">
              <ns:testsuite name="pytest" tests="1">
                <ns:testcase classname="M" name="one" />
              </ns:testsuite>
            </ns:testsuites>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe("M::one");
        result.Declared.ShouldBe(new JUnitDeclaredCounters(Tests: 1, Failures: null, Errors: null, Skipped: null));
    }

    [Fact]
    public void Map_BomPrefixedForeignRoot_IsRefused()
    {
        var bytes = WithUtf8Bom("<results><testcase classname=\"M\" name=\"one\" /></results>");

        var refusal = Should.Throw<JUnitParseRefusedException>(() => MapBytes(bytes));

        refusal.Reason.ShouldBe(PytestJUnitMapper.RootRefusalReason);
    }

    [Fact]
    public void Map_ValidTestsuitesRoot_MapsEveryCase()
    {
        const string Report =
            """
            <testsuites>
              <testsuite name="shard-a" tests="1">
                <testcase classname="M" name="one" />
              </testsuite>
              <testsuite name="shard-b" tests="1">
                <testcase classname="M" name="two" />
              </testsuite>
            </testsuites>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Select(mapped => mapped.ExecutionKey).ShouldBe(["M::one", "M::two"]);
        result.Declared.Tests.ShouldBe(2);
    }

    [Fact]
    public void Map_ValidTestsuiteRoot_MapsEveryCase()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="2">
              <testcase classname="M" name="one" />
              <testcase classname="M" name="two" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Count.ShouldBe(2);
        result.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public void MapRecords_ForeignRoot_IsRefusedBeforeAnyCaseIsMapped()
    {
        var resolutions = 0;

        var refusal = Should.Throw<JUnitParseRefusedException>(
            () => MapRecords(
                [
                    Start("results"),
                    Start("testcase", ("classname", "M"), ("name", "one")),
                    End("testcase"),
                    End("results"),
                ],
                (classname, name) =>
                {
                    resolutions++;
                    return classname + "::" + name;
                }));

        refusal.Reason.ShouldBe(PytestJUnitMapper.RootRefusalReason);
        resolutions.ShouldBe(0);
    }

    [Fact]
    public void MapRecords_NestedTestsuitesUnderForeignRoot_IsRefused()
    {
        var refusal = Should.Throw<JUnitParseRefusedException>(
            () => MapRecords(
                [
                    Start("results"),
                    Start("testsuites"),
                    Start("testsuite", ("tests", "1")),
                    Start("testcase", ("classname", "M"), ("name", "one")),
                    End("testcase"),
                    End("testsuite"),
                    End("testsuites"),
                    End("results"),
                ]));

        refusal.Reason.ShouldBe(PytestJUnitMapper.RootRefusalReason);
    }

    [Fact]
    public void MapRecords_ValidTestsuiteRoot_MapsEveryCase()
    {
        var result = MapRecords(
            [
                Start("testsuite", ("tests", "1")),
                Start("testcase", ("classname", "M"), ("name", "one")),
                End("testcase"),
                End("testsuite"),
            ]);

        result.Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe("M::one");
        result.Declared.Tests.ShouldBe(1);
    }

    private static JUnitMapResult MapBytes(byte[] xml) =>
        Mapper().Map(
            new MemoryStream(xml),
            new JUnitMapContext(PytestJUnitMapperReplay.ReportId, PytestJUnitMapperReplay.NodeId));

    private static JUnitMapResult MapRecords(
        IReadOnlyList<ParseRecord> records,
        Func<string, string, string?>? resolve = null) =>
        Mapper().Map(
            records,
            new JUnitMapContext(PytestJUnitMapperReplay.ReportId, resolve ?? PytestJUnitMapperReplay.NodeId));

    private static PytestJUnitMapper Mapper() =>
        new(new ParsingService(PytestJUnitMapperReplay.MaxCases), PytestJUnitMapperReplay.MaxBytes);

    private static byte[] WithUtf8Bom(string xml) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(xml)];

    private static ParseRecord Start(string name, params (string Key, string Value)[] attributes) =>
        Record(writer =>
        {
            writer.WriteString("t", "xml");
            writer.WriteString("e", "start");
            writer.WriteString("n", name);
            writer.WriteStartArray("a");
            foreach (var (key, value) in attributes)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(key);
                writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        });

    private static ParseRecord End(string name) =>
        Record(writer =>
        {
            writer.WriteString("t", "xml");
            writer.WriteString("e", "end");
            writer.WriteString("n", name);
        });

    private static ParseRecord Record(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return new ParseRecord(stream.ToArray());
    }
}
