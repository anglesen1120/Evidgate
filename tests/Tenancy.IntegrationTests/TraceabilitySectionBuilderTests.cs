using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Reports;
using Evidgate.Modules.Authoring.Application.Reports.Sections;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The traceability matrix: both sides of the relation, with uncovered requirements, unlinked cases and stale links kept apart.</summary>
public sealed class TraceabilitySectionBuilderTests
{
    private readonly TraceabilitySectionBuilder _builder = new();

    [Theory]
    [InlineData("en", "Traceability matrix", "Uncovered requirements", "Unlinked cases", "Stale links")]
    [InlineData("vi", "Ma trận truy vết", "Yêu cầu chưa có kiểm thử", "Ca kiểm thử chưa liên kết yêu cầu", "Liên kết lỗi thời")]
    public void Build_FlagsUncoveredRequirementsUnlinkedCasesAndStaleLinksSeparately(
        string language, string title, string uncoveredLabel, string unlinkedLabel, string staleLabel)
    {
        var section = _builder.Build(ReportSectionFixture.Source(language));

        section["title"]!.GetValue<string>().ShouldBe(title);
        section["labels"]!["uncovered_requirements"]!.GetValue<string>().ShouldBe(uncoveredLabel);
        section["labels"]!["unlinked_cases"]!.GetValue<string>().ShouldBe(unlinkedLabel);
        section["labels"]!["stale_links"]!.GetValue<string>().ShouldBe(staleLabel);

        var counts = section["counts"]!;
        counts["requirements"]!.GetValue<int>().ShouldBe(2);
        counts["covered_requirements"]!.GetValue<int>().ShouldBe(1);
        counts["uncovered_requirements"]!.GetValue<int>().ShouldBe(1);
        counts["cases"]!.GetValue<int>().ShouldBe(3);
        counts["unlinked_cases"]!.GetValue<int>().ShouldBe(1);
        counts["stale_links"]!.GetValue<int>().ShouldBe(1);

        var uncovered = section["uncovered_requirements"]!.AsArray().Single()!;
        uncovered["requirement"]!["title"]!.GetValue<string>().ShouldBe("Recover");
        uncovered["has_stale_link"]!.GetValue<bool>().ShouldBeTrue();
        section["unlinked_cases"]!.AsArray().Single()!["code"]!.GetValue<string>().ShouldBe("TC-3");

        var stale = section["stale_links"]!.AsArray().Single()!;
        stale["pinned_version_no"]!.GetValue<int>().ShouldBe(1);
        stale["current_version_no"]!.GetValue<int>().ShouldBe(2);
        stale["case"]!["code"]!.GetValue<string>().ShouldBe("TC-2");
    }

    [Fact]
    public void Build_LinkPinnedToASupersededVersion_IsStaleAndNotCoverage()
    {
        var section = _builder.Build(ReportSectionFixture.Source());

        var recover = Row(section["requirement_rows"], "Recover");
        recover["status"]!.GetValue<string>().ShouldBe("uncovered");
        recover["cases"]!.AsArray().ShouldBeEmpty();
        recover["stale_cases"]!.AsArray().Single()!["code"]!.GetValue<string>().ShouldBe("TC-2");

        var authenticate = Row(section["requirement_rows"], "Authenticate");
        authenticate["status"]!.GetValue<string>().ShouldBe("covered");
        authenticate["cases"]!.AsArray().Single()!["code"]!.GetValue<string>().ShouldBe("TC-1");
        authenticate["stale_cases"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public void Build_CaseSide_ShowsLinkedStaleOnlyAndUnlinkedStatesApart()
    {
        var rows = _builder.Build(ReportSectionFixture.Source())["case_rows"]!.AsArray();

        rows.Select(row => row!["status"]!.GetValue<string>()).ShouldBe(["linked", "stale_only", "unlinked"]);
        rows[0]!["requirements"]!.AsArray().Single()!["title"]!.GetValue<string>().ShouldBe("Authenticate");
        rows[1]!["requirements"]!.AsArray().ShouldBeEmpty();
        rows[1]!["stale_requirements"]!.AsArray().Single()!["title"]!.GetValue<string>().ShouldBe("Recover");
    }

    [Fact]
    public void Build_LinkToAChecklistItem_ReachesEveryCaseOfTheItem()
    {
        var source = ReportSectionFixture.Source();
        source = source with { Links = [new ReportLink(ReportSectionFixture.Id(20), ReportSectionFixture.Covered, ReportSectionFixture.CurrentVersion, 2, ReportSectionFixture.Item, null)] };

        var section = _builder.Build(source);

        section["counts"]!["unlinked_cases"]!.GetValue<int>().ShouldBe(0);
        section["counts"]!["stale_links"]!.GetValue<int>().ShouldBe(0);
        Row(section["requirement_rows"], "Authenticate")["cases"]!.AsArray().Count.ShouldBe(3);
        Row(section["requirement_rows"], "Recover")["status"]!.GetValue<string>().ShouldBe("uncovered");
        section["uncovered_requirements"]!.AsArray().Single()!["has_stale_link"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Build_LinkToAnItemWithNoCases_StillCoversTheRequirementByItem()
    {
        var source = ReportSectionFixture.Source();
        source = source with
        {
            Cases = [],
            Links = [new ReportLink(ReportSectionFixture.Id(20), ReportSectionFixture.Covered, ReportSectionFixture.CurrentVersion, 2, ReportSectionFixture.Item, null)],
        };

        var authenticate = Row(_builder.Build(source)["requirement_rows"], "Authenticate");

        authenticate["status"]!.GetValue<string>().ShouldBe("covered");
        authenticate["cases"]!.AsArray().ShouldBeEmpty();
        authenticate["items"]!.AsArray().Single()!["title"]!.GetValue<string>().ShouldBe("Credentials");
    }

    [Fact]
    public void Build_LinkToARequirementOrCaseTheSourceDoesNotHold_IsDroppedNotCounted()
    {
        var source = ReportSectionFixture.Source();
        source = source with
        {
            Links =
            [
                new ReportLink(ReportSectionFixture.Id(20), ReportSectionFixture.Id(77), ReportSectionFixture.CurrentVersion, 1, null, ReportSectionFixture.CurrentCase),
                new ReportLink(ReportSectionFixture.Id(21), ReportSectionFixture.Covered, ReportSectionFixture.CurrentVersion, 2, null, ReportSectionFixture.Id(78)),
            ],
        };

        var counts = _builder.Build(source)["counts"]!;

        counts["covered_requirements"]!.GetValue<int>().ShouldBe(0);
        counts["unlinked_cases"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Build_WithNoRequirementsOrCases_HasNoGapsAndNoRows()
    {
        var source = ReportSectionFixture.Source() with { Items = [], Cases = [], Requirements = [], Links = [] };

        var section = _builder.Build(source);

        foreach (var key in new[] { "requirements", "covered_requirements", "uncovered_requirements", "cases", "unlinked_cases", "stale_links" })
        {
            section["counts"]![key]!.GetValue<int>().ShouldBe(0);
        }

        section["requirement_rows"]!.AsArray().ShouldBeEmpty();
        section["case_rows"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public void Build_SeveralStaleLinks_AreListedOncePerPairInCanonicalOrder()
    {
        var section = _builder.Build(ReportSectionFixture.SourceWithManyStaleLinks());

        var rows = section["stale_links"]!.AsArray()
            .Select(row => $"{row!["requirement"]!["title"]!.GetValue<string>()}/{row["case"]!["code"]!.GetValue<string>()}/{row["pinned_version_no"]!.GetValue<int>()}");

        // The direct link and the item link both reach TC-2 at version 1: one row, not two.
        rows.ShouldBe(["Recover/TC-1/1", "Recover/TC-2/0", "Recover/TC-2/1", "Recover/TC-3/1", "Authenticate/TC-3/1"]);
        section["counts"]!["stale_links"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public void Build_ReversedLinksCasesAndRequirements_GivesTheSameSection()
    {
        var source = ReportSectionFixture.SourceWithManyStaleLinks();
        var reversed = source with
        {
            Cases = [.. source.Cases.Reverse()],
            Requirements = [.. source.Requirements.Reverse()],
            Links = [.. source.Links.Reverse()],
        };

        _builder.Build(reversed).ToJsonString().ShouldBe(_builder.Build(source).ToJsonString());
    }

    [Fact]
    public void SectionId_IsTheStableIdACreateRequestNames() => _builder.SectionId.ShouldBe("traceability");

    private static JsonNode Row(JsonNode? rows, string requirementTitle) =>
        rows!.AsArray().Single(row => row!["requirement"]!["title"]!.GetValue<string>() == requirementTitle)!;
}
