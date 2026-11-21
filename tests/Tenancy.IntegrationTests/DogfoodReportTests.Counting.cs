using System.Xml;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    /// <summary>One shared counter for every report assertion: elements by local name from a DTD-prohibited reader, so text, comments and namespace prefixes cannot skew a count.</summary>
    private static int CountElements(string xml, params string[] localNames)
    {
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var count = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && Array.IndexOf(localNames, reader.LocalName) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    private static void AssertMapsCleanly(string producer)
    {
        var xml = File.ReadAllText(Path.Combine(TestData, producer + ".junit.xml"));
        var expected = CountElements(xml, "testcase");

        var result = PytestJUnitMapperReplay.Map(xml);

        result.Discrepancies.ShouldBeEmpty();
        result.Cases.Count.ShouldBe(expected);
        result.Cases.Count.ShouldBeGreaterThan(0);
        result.Declared.Tests.ShouldBe(expected);
        var failing = CountElements(xml, "failure");
        result.Declared.Failures.ShouldBe(failing);
        result.Cases.Count(c => c.Normalized == "failed").ShouldBe(failing);
        result.Cases.Count(c => c.Normalized == "passed").ShouldBe(expected - failing);
    }

    /// <summary>The counter counts elements, not text: a failure mentioned in a comment or text node adds nothing, and a prefixed element counts by local name.</summary>
    [Fact]
    public void Dogfood_Counter_CountsElementsByLocalNameOnly()
    {
        const string Report = "<testsuites xmlns:j=\"urn:junit\"><!-- <failure /> --><testsuite><testcase name=\"a\"><j:failure message=\"x\">&lt;failure &gt;</j:failure></testcase><testcase name=\"b\" /></testsuite></testsuites>";

        CountElements(Report, "failure").ShouldBe(1);
        CountElements(Report, "testcase").ShouldBe(2);
        CountElements(Report, "failure", "testcase").ShouldBe(3);
        CountElements(Report, "error").ShouldBe(0);
        CountElements("<testcase><error/></testcase>", "error").ShouldBe(1);
    }

    /// <summary>The counter counts the failing case of an in-memory report and refuses a DTD instead of expanding it.</summary>
    [Fact]
    public void Dogfood_Counter_CountsFailingCaseAndRefusesDtd()
    {
        CountElements(FailingReport, "failure").ShouldBe(1);
        CountElements(FailingReport, "testcase").ShouldBe(2);
        CountElements("<r/>", "r").ShouldBe(1);
        Should.Throw<XmlException>(() => CountElements("<!DOCTYPE r><r/>", "r"));
    }
}
