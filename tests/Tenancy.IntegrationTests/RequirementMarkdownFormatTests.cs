using System.Text;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class RequirementMarkdownFormatTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Read_StoryAndTwoCriteria_PreservesHierarchy(string newline)
    {
        var parsed = Read(string.Join(newline, "# Checkout", "Story body", "", "- First criterion", "- Second criterion"));
        parsed.IsSuccess.ShouldBeTrue();
        parsed.Rows.Select(row => row.NodeKind).ShouldBe(["user_story", "acceptance_criterion", "acceptance_criterion"]);
        parsed.Rows.Select(row => row.ParentKey).ShouldBe([null, "s1", "s1"]);
        parsed.Rows.Select(row => row.Description).ShouldBe(["Story body", "First criterion", "Second criterion"]);
    }

    [Theory]
    [InlineData("- Given a cart\n  When I pay\n  Then it completes", "Given a cart\nWhen I pay\nThen it completes")]
    [InlineData("```gherkin\nGiven a cart\n\nWhen I pay\nThen it completes\n```", "Given a cart\n\nWhen I pay\nThen it completes")]
    [InlineData("```\nScenario: Payment\nGiven a cart\nWhen I pay\nThen it completes\n```", "Scenario: Payment\nGiven a cart\nWhen I pay\nThen it completes")]
    [InlineData("Given a cart\nWhen I pay\nThen it completes", "Given a cart\nWhen I pay\nThen it completes")]
    public void Read_GherkinBlock_ProducesOneVerbatimCriterion(string block, string expected)
    {
        var parsed = Read("# Checkout\n" + block);
        parsed.IsSuccess.ShouldBeTrue();
        parsed.Rows.Count.ShouldBe(2);
        parsed.Rows[1].Description.ShouldBe(expected);
    }

    [Fact]
    public void Read_NestedItemsAndFencedContinuation_PreservesItemBoundaries()
    {
        var parsed = Read("# Story\n- Parent\n  ```gherkin\n  Given one\n  Then two\n  ```\n  - Child\n    continuation");
        parsed.IsSuccess.ShouldBeTrue();
        parsed.Rows.Count.ShouldBe(3);
        parsed.Rows[1].Description.ShouldBe("Parent\n```gherkin\nGiven one\nThen two\n```");
        parsed.Rows[2].Description.ShouldBe("Child\ncontinuation");
    }

    [Fact]
    public void Read_OrphanList_ReportsSourceLine()
    {
        var parsed = Read("\n- orphan\n# Story\n- valid");
        parsed.Issues.ShouldContain(new ImportIssue(2, "/row", "orphan_row"));
    }

    [Fact]
    public void Read_LongCriterionTitle_TruncatesCodePointsNotBody()
    {
        var body = string.Concat(Enumerable.Repeat("😀", 201));
        var parsed = Read("# Story\n- " + body);
        parsed.IsSuccess.ShouldBeTrue();
        parsed.Rows[1].Title!.EnumerateRunes().Count().ShouldBe(200);
        parsed.Rows[1].Description.ShouldBe(body);
    }

    [Theory]
    [InlineData(20000, true)]
    [InlineData(20001, false)]
    public void Read_BodyCap_UsesNormalizedCodePoints(int length, bool accepted)
    {
        var parsed = Read("# Story\n- " + string.Concat(Enumerable.Repeat("e\u0301", length)));
        parsed.IsSuccess.ShouldBe(accepted);
        if (!accepted)
        {
            parsed.Issues.ShouldContain(new ImportIssue(2, "/description", "length"));
        }
    }

    [Fact]
    public void Read_Bom_IsTolerated()
    {
        MarkdownFormat.Read([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("# Story\n- criterion")]).Rows.Count.ShouldBe(2);
    }

    [Fact]
    public void Read_Nul_IsRefusedWithLine()
    {
        Read("# Story\n- bad\0body").Issues.ShouldContain(new ImportIssue(2, "/row", "invalid_text"));
    }

    [Fact]
    public void Read_InvalidUtf8_IsRefused()
    {
        MarkdownFormat.Read([0xed, 0xa0, 0x80]).Issues[0].Reason.ShouldBe("invalid_text");
    }

    [Fact]
    public void Read_OverRowCap_IsTooLarge()
    {
        Read(string.Join('\n', Enumerable.Repeat("# Story", ImportLimits.MaxDataRows + 1))).TooLarge.ShouldBeTrue();
    }

    [Fact]
    public void Read_SetextHeading_DoesNotCreateStory()
    {
        Read("Not an ATX heading\n===\n- orphan").Issues.ShouldContain(new ImportIssue(3, "/row", "orphan_row"));
    }

    private static ImportParse Read(string text) => MarkdownFormat.Read(Encoding.UTF8.GetBytes(text));
}
