using Evidgate.Modules.Authoring.Application.Reports;
using Evidgate.Modules.Authoring.Application.Reports.Sections;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The test cases section: steps, data and expected results grouped by feature, with the requirements each case reaches.</summary>
public sealed class TestCasesSectionBuilderTests
{
    private readonly TestCasesSectionBuilder _builder = new();

    [Theory]
    [InlineData("en", "Test cases", "Expected result", "Data", "Precondition")]
    [InlineData("vi", "Ca kiểm thử", "Kết quả mong đợi", "Dữ liệu", "Điều kiện tiên quyết")]
    public void Build_GroupsCasesByFeatureWithStepsDataAndExpectedResults(
        string language, string title, string expected, string data, string precondition)
    {
        var section = _builder.Build(ReportSectionFixture.Source(language));

        section["title"]!.GetValue<string>().ShouldBe(title);
        section["labels"]!["expected"]!.GetValue<string>().ShouldBe(expected);
        section["labels"]!["data"]!.GetValue<string>().ShouldBe(data);
        section["labels"]!["precondition"]!.GetValue<string>().ShouldBe(precondition);

        var group = section["groups"]!.AsArray().Single()!;
        group["feature_title"]!.GetValue<string>().ShouldBe("Sign in");
        var cases = group["cases"]!.AsArray();
        cases.Select(row => row!["code"]!.GetValue<string>()).ShouldBe(["TC-1", "TC-2", "TC-3"]);

        var step = cases[0]!["steps"]!.AsArray().Single()!;
        step["no"]!.GetValue<int>().ShouldBe(1);
        step["action"]!.GetValue<string>().ShouldBe("Enter name");
        step["data"]!.GetValue<string>().ShouldBe("Ada");
        step["expected"]!.GetValue<string>().ShouldBe("Welcome Ada");
        cases[0]!["precondition"]!.GetValue<string>().ShouldBe("User exists");
    }

    [Fact]
    public void Build_EachCaseNamesItsCurrentAndItsStaleRequirementsApart()
    {
        var cases = _builder.Build(ReportSectionFixture.Source())["groups"]![0]!["cases"]!.AsArray();

        Titles(cases[0]!["requirements"]).ShouldBe(["Authenticate"]);
        Titles(cases[0]!["stale_requirements"]).ShouldBeEmpty();
        Titles(cases[1]!["requirements"]).ShouldBeEmpty();
        Titles(cases[1]!["stale_requirements"]).ShouldBe(["Recover"]);
        Titles(cases[2]!["requirements"]).ShouldBeEmpty();
        Titles(cases[2]!["stale_requirements"]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("en", "No feature")]
    [InlineData("vi", "Không thuộc tính năng nào")]
    public void Build_ItemWhoseFeatureIsNotInTheSource_GoesToTheNoFeatureGroupLast(string language, string label)
    {
        var source = ReportSectionFixture.Source(language);
        var orphanItem = new ReportItem(ReportSectionFixture.Id(40), ReportSectionFixture.Id(99), "Orphan", null, "low", "approved");
        var orphanCase = source.Cases[0] with { Id = ReportSectionFixture.Id(41), ChecklistItemId = orphanItem.Id, Code = "TC-0" };
        source = source with { Items = [.. source.Items, orphanItem], Cases = [.. source.Cases, orphanCase] };

        var groups = _builder.Build(source)["groups"]!.AsArray();

        groups.Count.ShouldBe(2);
        groups[0]!["feature_title"]!.GetValue<string>().ShouldBe("Sign in");
        groups[1]!["feature_id"].ShouldBeNull();
        groups[1]!["feature_title"]!.GetValue<string>().ShouldBe(label);
        groups[1]!["cases"]![0]!["code"]!.GetValue<string>().ShouldBe("TC-0");
    }

    [Fact]
    public void Build_CaseWithoutAStepsDocument_HasNoStepRows()
    {
        var source = ReportSectionFixture.Source();
        source = source with { Cases = [source.Cases[0] with { Steps = default }] };

        _builder.Build(source)["groups"]![0]!["cases"]![0]!["steps"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public void Build_NestedFeatures_ShowThePathOfTitlesAboveTheCase()
    {
        var source = ReportSectionFixture.Source();
        var parent = new ReportNode(ReportSectionFixture.Id(50), null, "module", "Accounts", 0);
        source = source with { Nodes = [parent, new ReportNode(ReportSectionFixture.Feature, parent.Id, "feature", "Sign in", 0)] };

        _builder.Build(source)["groups"]![0]!["feature_path"]!.GetValue<string>().ShouldBe("Accounts / Sign in");
    }

    [Fact]
    public void SectionId_IsTheStableIdACreateRequestNames() => _builder.SectionId.ShouldBe("test_cases");

    private static IEnumerable<string> Titles(System.Text.Json.Nodes.JsonNode? array) =>
        array!.AsArray().Select(row => row!["title"]!.GetValue<string>());
}
