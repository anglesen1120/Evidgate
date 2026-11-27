using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Reports;
using Evidgate.Modules.Authoring.Application.Reports.Sections;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The execution section: a non-release scope never shows a decision, a release scope shows the recorded one or its absence, and an empty run is never zero failures.</summary>
public sealed class ExecutionSectionBuilderTests
{
    private readonly ExecutionSectionBuilder _builder = new();

    [Theory]
    [InlineData("en", "no release decision", "No manual execution recorded")]
    [InlineData("vi", "không có quyết định phát hành", "Chưa ghi nhận lượt thực thi thủ công nào")]
    public void Build_NonReleaseBeforeAnyManualResult_SaysNoManualExecutionAndShowsNoCounts(string language, string noDecision, string noManual)
    {
        var section = _builder.Build(ReportSectionFixture.Source(language));

        section["release_decision"]!["state"]!.GetValue<string>().ShouldBe("not_applicable");
        section["release_decision"]!["note"]!.GetValue<string>().ShouldBe(noDecision);
        section["manual"]!["state"]!.GetValue<string>().ShouldBe("none");
        section["manual"]!["note"]!.GetValue<string>().ShouldBe(noManual);
        // No counts member at all: nothing in the payload can be read as "0 failed".
        section["manual"]!["counts"].ShouldBeNull();
        section.ToJsonString().ShouldNotContain("\"count\"");
    }

    [Theory]
    [InlineData("en", "Passed", "Failed", "Blocked", "Skipped", "Not executed")]
    [InlineData("vi", "Đạt", "Không đạt", "Bị chặn", "Bỏ qua", "Chưa thực thi")]
    public void Build_NonReleaseWithResults_CountsByStatusKeepsNotExecutedApartAndIgnoresAStrayDecision(
        string language, string passed, string failed, string blocked, string skipped, string notExecuted)
    {
        var source = ReportSectionFixture.Source(language) with
        {
            ManualResults =
            [
                new ReportManualResult(ReportSectionFixture.CurrentCase, "passed"),
                new ReportManualResult(ReportSectionFixture.StaleCase, "failed"),
            ],
            ReleaseDecision = new ReportReleaseDecision("FAIL", ["manual_case_not_executed"]),
        };

        var section = _builder.Build(source);

        section["release_decision"]!["state"]!.GetValue<string>().ShouldBe("not_applicable");
        section["release_decision"]!["decision"].ShouldBeNull();
        section["manual"]!["state"]!.GetValue<string>().ShouldBe("recorded");
        section["manual"]!["total_cases"]!.GetValue<int>().ShouldBe(3);

        var counts = section["manual"]!["counts"]!.AsArray();
        counts.Select(row => row!["key"]!.GetValue<string>()).ShouldBe(["passed", "failed", "blocked", "skipped", "not_executed"]);
        counts.Select(row => row!["count"]!.GetValue<int>()).ShouldBe([1, 1, 0, 0, 1]);
        counts.Select(row => row!["label"]!.GetValue<string>()).ShouldBe([passed, failed, blocked, skipped, notExecuted]);
    }

    [Theory]
    [InlineData("en", "PASS")]
    [InlineData("vi", "FAIL")]
    [InlineData("en", "INCONCLUSIVE")]
    public void Build_Release_ShowsTheBoundDecisionWithItsReasonsAndTheSeparateManualEmptyState(string language, string decision)
    {
        var source = ReportSectionFixture.Source(language, ReportScopeKinds.Release) with
        {
            ReleaseDecision = new ReportReleaseDecision(decision, ["manual_evidence_missing", "missing_result"]),
        };

        var section = _builder.Build(source);

        var shown = section["release_decision"]!;
        shown["state"]!.GetValue<string>().ShouldBe("recorded");
        shown["decision"]!.GetValue<string>().ShouldBe(decision);
        shown["reasons"]!.AsArray().Select(reason => reason!.GetValue<string>()).ShouldBe(["manual_evidence_missing", "missing_result"]);
        shown["reasons_label"]!.GetValue<string>().ShouldBe(language == "vi" ? "Lý do" : "Reasons");
        // Automated evidence can decide a release while no manual run exists: both facts are shown, apart.
        section["manual"]!["state"]!.GetValue<string>().ShouldBe("none");
    }

    [Theory]
    [InlineData("en", "No release decision is recorded: aggregation is pending or has not produced one")]
    [InlineData("vi", "Chưa có quyết định phát hành: việc tổng hợp đang chờ hoặc chưa tạo ra quyết định")]
    public void Build_ReleaseWithoutADecision_ShowsTheAbsenceNotAVerdict(string language, string note)
    {
        var section = _builder.Build(ReportSectionFixture.Source(language, ReportScopeKinds.Release));

        var shown = section["release_decision"]!;
        shown["state"]!.GetValue<string>().ShouldBe("absent");
        shown["note"]!.GetValue<string>().ShouldBe(note);
        shown["decision"].ShouldBeNull();
        shown["reasons"].ShouldBeNull();
        section.ToJsonString().ShouldNotContain("\"PASS\"");
    }

    [Fact]
    public void Build_ReleaseWithManualResults_ShowsTheDecisionAndTheCountsTogether()
    {
        var source = ReportSectionFixture.Source(scope: ReportScopeKinds.Release) with
        {
            ReleaseDecision = new ReportReleaseDecision("INCONCLUSIVE", ["manual_case_not_executed"]),
            ManualResults = [new ReportManualResult(ReportSectionFixture.CurrentCase, "blocked")],
        };

        var section = _builder.Build(source);

        section["release_decision"]!["decision"]!.GetValue<string>().ShouldBe("INCONCLUSIVE");
        Count(section, "blocked").ShouldBe(1);
        Count(section, "not_executed").ShouldBe(2);
    }

    [Fact]
    public void Build_ResultsOfCasesOutsideTheScope_AreNotCountedAndLeaveTheEmptyState()
    {
        var source = ReportSectionFixture.Source() with
        {
            ManualResults = [new ReportManualResult(ReportSectionFixture.Id(90), "failed")],
        };

        var manual = _builder.Build(source)["manual"]!;

        manual["state"]!.GetValue<string>().ShouldBe("none");
        manual["counts"].ShouldBeNull();
    }

    [Fact]
    public void Build_TwoResultsForOneCase_CountsTheLaterOne()
    {
        var source = ReportSectionFixture.Source() with
        {
            ManualResults =
            [
                new ReportManualResult(ReportSectionFixture.CurrentCase, "failed"),
                new ReportManualResult(ReportSectionFixture.CurrentCase, "passed"),
            ],
        };

        var section = _builder.Build(source);

        Count(section, "passed").ShouldBe(1);
        Count(section, "failed").ShouldBe(0);
        Count(section, "not_executed").ShouldBe(2);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("not_executed")]
    [InlineData("")]
    public void Build_ManualResultWithAnUnknownStatus_FailsTheBuildInsteadOfMiscounting(string status)
    {
        var source = ReportSectionFixture.Source() with
        {
            ManualResults = [new ReportManualResult(ReportSectionFixture.CurrentCase, status)],
        };

        Should.Throw<ReportBuildException>(() => _builder.Build(source)).Message.ShouldStartWith(ReportBuildException.Failed + ":");
    }

    [Fact]
    public void Build_ReleaseDecisionWithAnUnknownValue_FailsTheBuildInsteadOfShowingIt()
    {
        var source = ReportSectionFixture.Source(scope: ReportScopeKinds.Release) with
        {
            ReleaseDecision = new ReportReleaseDecision("pass", []),
        };

        Should.Throw<ReportBuildException>(() => _builder.Build(source)).Message.ShouldStartWith(ReportBuildException.Failed + ":");
    }

    [Fact]
    public void SectionId_IsTheStableIdACreateRequestNames() => _builder.SectionId.ShouldBe("execution");

    private static int Count(JsonNode section, string status) =>
        section["manual"]!["counts"]!.AsArray().Single(row => row!["key"]!.GetValue<string>() == status)!["count"]!.GetValue<int>();
}
