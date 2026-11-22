using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class DogfoodReportTests
{
    private const string FailingReport =
        "<testsuites tests=\"2\" failures=\"1\" errors=\"0\" skipped=\"0\"><testsuite name=\"Suite\" tests=\"2\" failures=\"1\" errors=\"0\" skipped=\"0\">"
        + "<testcase classname=\"Suite\" name=\"Passes\" time=\"0.1\" />"
        + "<testcase classname=\"Suite\" name=\"Fails\" time=\"0.1\"><failure message=\"boom\">trace</failure></testcase>"
        + "</testsuite></testsuites>";

    /// <summary>The committed own-CI reports are a green run: no failure or error element, and the mapper counts none failed.</summary>
    [Theory]
    [MemberData(nameof(Producers))]
    public void Dogfood_OwnCiReport_HasNoFailingCases(string producer) =>
        AssertNoFailingCases(File.ReadAllText(Path.Combine(TestData, producer + ".junit.xml")));

    /// <summary>The pin above can fail: a report with one failing case maps as failed and trips it.</summary>
    [Fact]
    public void Dogfood_ReportWithFailure_MapsAsFailedAndTripsTheGreenPin()
    {
        var result = PytestJUnitMapperReplay.Map(FailingReport);

        result.Discrepancies.ShouldBeEmpty();
        result.Cases.Count(c => c.Normalized == "failed").ShouldBe(1);
        result.Declared.Failures.ShouldBe(1);
        Should.Throw<ShouldAssertException>(() => AssertNoFailingCases(FailingReport));
    }

    /// <summary>An error element trips the green pin as well, not only a failure element.</summary>
    [Fact]
    public void Dogfood_ReportWithErrorElement_TripsTheGreenPin()
    {
        var report = FailingReport
            .Replace("<failure message=\"boom\">trace</failure>", "<error message=\"boom\">trace</error>", StringComparison.Ordinal)
            .Replace("failures=\"1\" errors=\"0\"", "failures=\"0\" errors=\"1\"", StringComparison.Ordinal);

        Should.Throw<ShouldAssertException>(() => AssertNoFailingCases(report));
    }

    /// <summary>A namespaced or counterless error element still trips the pin, because it is counted by local name and mapped outcome.</summary>
    [Fact]
    public void Dogfood_NamespacedErrorWithoutCounters_TripsTheGreenPin()
    {
        const string Report = "<testsuites xmlns:j=\"urn:junit\"><testsuite name=\"Suite\"><testcase classname=\"Suite\" name=\"Errors\"><j:error\n message=\"x\"/></testcase></testsuite></testsuites>";

        Should.Throw<ShouldAssertException>(() => AssertNoFailingCases(Report));
    }

    private static void AssertNoFailingCases(string xml)
    {
        CountElements(xml, "failure", "error")
            .ShouldBe(0, "report has a failure or error element");
        var result = PytestJUnitMapperReplay.Map(xml);
        result.Discrepancies.ShouldBeEmpty();
        result.Cases.Count(c => c.Normalized is "failed" or "error").ShouldBe(0);
        (result.Declared.Failures ?? 0).ShouldBe(0);
        (result.Declared.Errors ?? 0).ShouldBe(0);
    }
}
