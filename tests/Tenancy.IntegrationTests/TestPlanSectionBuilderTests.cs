using Evidgate.Modules.Authoring.Application.Reports;
using Evidgate.Modules.Authoring.Application.Reports.Sections;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The test plan section, built from a source alone with no database, in both report languages.</summary>
public sealed class TestPlanSectionBuilderTests
{
    private readonly TestPlanSectionBuilder _builder = new();

    [Theory]
    [InlineData("en", "Test plan", "Scope", "Feature nodes", "Strategy", "Environment")]
    [InlineData("vi", "Kế hoạch kiểm thử", "Phạm vi", "Nút tính năng", "Chiến lược kiểm thử", "Môi trường")]
    public void Build_ListsScopeRequirementsPrioritiesStrategyAndEnvironment(
        string language, string title, string scope, string kind, string strategy, string environment)
    {
        var section = _builder.Build(ReportSectionFixture.Source(language));

        section["title"]!.GetValue<string>().ShouldBe(title);
        section["scope"]!["label"]!.GetValue<string>().ShouldBe(scope);
        section["scope"]!["kind_label"]!.GetValue<string>().ShouldBe(kind);
        section["scope"]!["selected"]![0]!["title"]!.GetValue<string>().ShouldBe("Sign in");
        section["strategy"]!["label"]!.GetValue<string>().ShouldBe(strategy);
        section["environment"]!["label"]!.GetValue<string>().ShouldBe(environment);
        // The author's text is kept as written: a renderer treats it as plain text, never as markup or a formula.
        section["strategy"]!["text"]!.GetValue<string>().ShouldBe("Smoke <b>only</b>");
        section["environment"]!["text"]!.GetValue<string>().ShouldBe("Linux =SUM(A1)");
        section["strategy"]!["empty_note"].ShouldBeNull();

        var requirements = section["requirements"]!["items"]!.AsArray();
        requirements.Select(row => row!["title"]!.GetValue<string>()).ShouldBe(["Recover", "Authenticate"]);
        requirements[1]!["ref"]!.GetValue<string>().ShouldBe("REQ-1");
        requirements[1]!["version_no"]!.GetValue<int>().ShouldBe(2);

        var priority = section["priorities"]!["items"]!.AsArray().Single()!;
        priority["priority"]!.GetValue<string>().ShouldBe("high");
        priority["checklist_items"]!.GetValue<int>().ShouldBe(1);
        priority["cases"]!.GetValue<int>().ShouldBe(3);
    }

    [Theory]
    [InlineData("en", "Not provided")]
    [InlineData("vi", "Chưa cung cấp")]
    public void Build_WithoutStrategyOrEnvironment_SaysNotProvidedInThePinnedLanguage(string language, string note)
    {
        var source = ReportSectionFixture.Source(language);
        source = source with { Request = source.Request with { Strategy = null, Environment = null } };

        var section = _builder.Build(source);

        section["strategy"]!["text"].ShouldBeNull();
        section["strategy"]!["empty_note"]!.GetValue<string>().ShouldBe(note);
        section["environment"]!["empty_note"]!.GetValue<string>().ShouldBe(note);
    }

    [Fact]
    public void Build_WithNoRequirementsOrCases_ListsNothingRatherThanInventingRows()
    {
        var source = ReportSectionFixture.Source() with { Items = [], Cases = [], Requirements = [], Links = [] };

        var section = _builder.Build(source);

        section["requirements"]!["items"]!.AsArray().ShouldBeEmpty();
        section["priorities"]!["items"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public void Build_SelectedIdWithoutARow_KeepsTheIdWithNoTitle()
    {
        var source = ReportSectionFixture.Source(scope: ReportScopeKinds.Release);

        var selected = _builder.Build(source)["scope"]!["selected"]!.AsArray().Single()!;

        selected["id"]!.GetValue<string>().ShouldBe(ReportSectionFixture.Release.ToString("D"));
        selected["title"].ShouldBeNull();
    }

    [Fact]
    public void SectionId_IsTheStableIdACreateRequestNames() => _builder.SectionId.ShouldBe("test_plan");
}
