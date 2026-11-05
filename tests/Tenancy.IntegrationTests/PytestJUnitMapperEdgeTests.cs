using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The untrusted edges of one report: the <c>time</c> attribute (whose text the producer controls), names that
/// carry unicode, and every way the document can be refused. A refusal must be the declared, typed one — a fault
/// anywhere else may never read as "malformed", or a broken mapper would look like an honest rejection.
/// </summary>
public sealed class PytestJUnitMapperEdgeTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("-1", null)]
    [InlineData("-0.001", null)]
    [InlineData("abc", null)]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("-Infinity", null)]
    [InlineData("1e309", null)]
    [InlineData("0,010", null)]
    [InlineData("0.0004", 0L)]
    [InlineData("0.0005", 1L)]
    [InlineData("0.0015", 2L)]
    [InlineData("0.010", 10L)]
    [InlineData("0.081", 81L)]
    [InlineData("1", 1000L)]
    [InlineData("1e-3", 1L)]
    public void Time_MapsToWholeMillisecondsOrIsAbsent(string? time, long? expected)
    {
        var attribute = time is null ? string.Empty : $"time=\"{time}\"";
        var report = $"<testsuite name=\"pytest\" tests=\"1\"><testcase classname=\"M\" name=\"t\" {attribute} /></testsuite>";

        var result = PytestJUnitMapperReplay.Map(report);

        result.Cases.ShouldHaveSingleItem().DurationMs.ShouldBe(expected);
    }

    [Fact]
    public void Time_MissingAttributeIsAbsent()
    {
        const string Report = "<testsuite name=\"pytest\" tests=\"1\"><testcase classname=\"M\" name=\"t\" /></testsuite>";

        PytestJUnitMapperReplay.Map(Report).Cases.ShouldHaveSingleItem().DurationMs.ShouldBeNull();
    }

    [Fact]
    public void Time_AbsurdSecondsAreAbsentRatherThanOverflowing()
    {
        const string Report =
            "<testsuite name=\"pytest\" tests=\"1\"><testcase classname=\"M\" name=\"t\" time=\"9223372036854776\" /></testsuite>";

        PytestJUnitMapperReplay.Map(Report).Cases.ShouldHaveSingleItem().DurationMs.ShouldBeNull();
    }

    [Fact]
    public void UnicodeAndControlCharacters_ArePreservedWithoutLoss()
    {
        // The tab is a character reference on purpose: XML attribute normalization rewrites a literal
        // tab/newline to a space, so only a reference can carry the character into the reader.
        const string Report =
            """
            <testsuite name="pytest" tests="1">
              <testcase classname="tests.emoji.&#x1F600;&#9;suite" name="test_ünïcode_for_😀_and_&#9;_tab" time="0.010" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        var mapped = result.Cases.ShouldHaveSingleItem();
        mapped.Classname.ShouldBe("tests.emoji.\U0001F600\tsuite");
        mapped.Name.ShouldBe("test_ünïcode_for_\U0001F600_and_\t_tab");
    }

    [Fact]
    public void IllegalXmlControlCharacter_IsADeclaredRefusal()
    {
        var report = "<testsuite name=\"pytest\" tests=\"1\"><testcase classname=\"a\u0001b\" name=\"t\" /></testsuite>";

        var refusal = Should.Throw<JUnitParseRefusedException>(() => PytestJUnitMapperReplay.Map(report));

        refusal.Reason.ShouldBe("malformed");
    }

    [Fact]
    public void TruncatedDocument_IsADeclaredMalformedRefusal()
    {
        const string Report =
            """
            <testsuites>
              <testsuite name="pytest" tests="2">
                <testcase classname="tests.test_a" name="test_one" time="0.010" />
                <testcase classname="tests.test_a" name="test_two" time="0.020">
            """;

        var refusal = Should.Throw<JUnitParseRefusedException>(() => PytestJUnitMapperReplay.Map(Report));

        refusal.Reason.ShouldBe("malformed");
    }

    [Fact]
    public void DoctypeDeclaration_IsRefusedByName()
    {
        const string Report =
            """
            <?xml version="1.0"?>
            <!DOCTYPE testsuite [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <testsuite name="pytest" tests="1">
              <testcase classname="M" name="t" />
            </testsuite>
            """;

        var refusal = Should.Throw<JUnitParseRefusedException>(() => PytestJUnitMapperReplay.Map(Report));

        refusal.Reason.ShouldBe("dtd_prohibited");
    }

    [Fact]
    public void NonXmlBody_IsADeclaredMalformedRefusal()
    {
        const string Report = "not xml at all";

        var refusal = Should.Throw<JUnitParseRefusedException>(() => PytestJUnitMapperReplay.Map(Report));

        refusal.Reason.ShouldBe("malformed");
    }

    [Fact]
    public void ByteCap_IsRefusedAsSizeExceeded()
    {
        const string Report = "<testsuite name=\"pytest\" tests=\"1\"><testcase classname=\"M\" name=\"t\" /></testsuite>";

        var refusal = Should.Throw<JUnitParseRefusedException>(
            () => PytestJUnitMapperReplay.Map(Report, maxBytes: 16));

        refusal.Reason.ShouldBe("size_exceeded");
    }

    [Fact]
    public void CaseCap_IsRefusedAsCapExceeded()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="2">
              <testcase classname="M" name="one" />
              <testcase classname="M" name="two" />
            </testsuite>
            """;

        var refusal = Should.Throw<JUnitParseRefusedException>(
            () => PytestJUnitMapperReplay.Map(Report, maxCases: 1));

        refusal.Reason.ShouldBe("cap_exceeded");
    }

    [Fact]
    public void ResolverFault_PropagatesInsteadOfReadingAsMalformed()
    {
        const string Report = "<testsuite name=\"pytest\" tests=\"1\"><testcase classname=\"M\" name=\"t\" /></testsuite>";

        Should.Throw<InvalidOperationException>(
            () => PytestJUnitMapperReplay.Map(Report, (_, _) => throw new InvalidOperationException("resolver bug")));
    }

    [Fact]
    public void NullArguments_AreRejectedBeforeAnyParse()
    {
        var mapper = new PytestJUnitMapper(new ParsingService(PytestJUnitMapperReplay.MaxCases));

        Should.Throw<ArgumentNullException>(
            () => mapper.Map((Stream)null!, new JUnitMapContext(PytestJUnitMapperReplay.ReportId, PytestJUnitMapperReplay.NodeId)));
        Should.Throw<ArgumentNullException>(
            () => mapper.Map(new MemoryStream(), null!));
        Should.Throw<ArgumentNullException>(
            () => mapper.Map((IReadOnlyList<ParseRecord>)null!, new JUnitMapContext(PytestJUnitMapperReplay.ReportId, PytestJUnitMapperReplay.NodeId)));
    }

    [Fact]
    public void DtdTextInsideCdata_IsNotADoctype()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="1">
              <testcase classname="M" name="mentions">
                <failure message="docs"><![CDATA[<!DOCTYPE x> is not a DTD here]]></failure>
              </testcase>
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.ShouldHaveSingleItem().Normalized.ShouldBe("failed");
    }
}
