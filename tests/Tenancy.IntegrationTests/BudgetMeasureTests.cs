using System.Text.Json;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The budget contract without a database or a container: the comparison's message carries the numbers a reader needs,
/// and a malformed <c>budget</c> member is refused rather than compared against nothing.
/// </summary>
public sealed class BudgetMeasureTests
{
    private const string FiveLimits =
        "\"parse_wall_ms\":1000,\"reconcile_wall_ms\":1000,\"walk_wall_ms\":1000,"
        + "\"child_peak_working_set_bytes\":1000,\"parent_inflight_retained_bytes\":1000";

    private static readonly BudgetMetrics AtLimit = new(1000, 1000, 1000, 1000, 1000);

    [Fact]
    public void Compare_OverOneLimit_FailsWithMeasuredAndLimit()
    {
        var budget = Budget("{\"status\":\"measured\",\"limits\":{"
            + "\"parse_wall_ms\":900,\"reconcile_wall_ms\":1000,\"walk_wall_ms\":1000,"
            + "\"child_peak_working_set_bytes\":1000,\"parent_inflight_retained_bytes\":1000}}");

        var verdict = budget.Compare(AtLimit);

        verdict.WithinBudget.ShouldBeFalse();
        verdict.Status.ShouldBe(BudgetStatus.Measured);
        verdict.Message.ShouldContain("parse_wall_ms measured=1000 limit=900");
        verdict.Message.ShouldContain("walk_wall_ms measured=1000 limit=1000");
    }

    [Fact]
    public void Compare_EqualToEveryLimit_Passes()
    {
        var verdict = Budget("{\"status\":\"measured\",\"limits\":{" + FiveLimits + "}}").Compare(AtLimit);

        verdict.WithinBudget.ShouldBeTrue(verdict.Message);
        verdict.Message.ShouldStartWith("within budget");
    }

    [Fact]
    public void Compare_BelowEveryLimit_PassesAndQuotesEachMetric()
    {
        var verdict = Budget("{\"status\":\"measured\",\"limits\":{"
            + "\"parse_wall_ms\":2000,\"reconcile_wall_ms\":2000,\"walk_wall_ms\":2000,"
            + "\"child_peak_working_set_bytes\":2048,\"parent_inflight_retained_bytes\":2048}}")
            .Compare(new BudgetMetrics(1, 2, 3, 4, 5));

        verdict.WithinBudget.ShouldBeTrue(verdict.Message);
        verdict.Message.ShouldContain("child_peak_working_set_bytes measured=4 limit=2048");
        verdict.Message.ShouldContain("parent_inflight_retained_bytes measured=5 limit=2048");
    }

    [Fact]
    public void Compare_UnsetBudget_FailsAndAsksForTheNumbers()
    {
        var budget = Budget("{\"status\":\"unset\"}");

        var verdict = budget.Compare(new BudgetMetrics(11, 12, 13, 14, 15));

        verdict.WithinBudget.ShouldBeFalse();
        verdict.Status.ShouldBe(BudgetStatus.Unset);
        verdict.Message.ShouldContain(ReportBudget.RecordTheseNumbers);
        verdict.Message.ShouldContain("parse_wall_ms=11");
        verdict.Message.ShouldContain("parent_inflight_retained_bytes=15");
        budget.Limits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"status\":\"measured\"}", "limits")]
    [InlineData("{\"status\":\"measured\",\"limits\":{\"parse_wall_ms\":1}}", "exactly five")]
    [InlineData("{\"status\":\"measured\",\"limits\":{" + FiveLimits + ",\"extra\":1}}", "exactly five")]
    [InlineData("{\"status\":\"measured\",\"limits\":{\"parse_wall_ms\":1.5,\"reconcile_wall_ms\":1,\"walk_wall_ms\":1,"
        + "\"child_peak_working_set_bytes\":1,\"parent_inflight_retained_bytes\":1}}", "nonnegative integer")]
    [InlineData("{\"status\":\"measured\",\"limits\":{\"parse_wall_ms\":-1,\"reconcile_wall_ms\":1,\"walk_wall_ms\":1,"
        + "\"child_peak_working_set_bytes\":1,\"parent_inflight_retained_bytes\":1}}", "nonnegative integer")]
    [InlineData("{\"status\":\"unknown\"}", "unset or measured")]
    [InlineData("{\"limits\":{" + FiveLimits + "}}", "status")]
    [InlineData("[]", "must be an object")]
    public void FromMember_MalformedBudget_IsRefused(string json, string expected)
    {
        var error = Should.Throw<InvalidOperationException>(() => Budget(json));

        error.Message.ShouldContain(expected);
    }

    [Fact]
    public void Read_RootWithoutBudgetMember_IsRefused()
    {
        using var document = JsonDocument.Parse("{\"full\":{\"tests\":1}}");

        var error = Should.Throw<InvalidOperationException>(() => ReportBudget.Read(document.RootElement));

        error.Message.ShouldContain("budget");
    }

    [Fact]
    public void Read_CommittedFixtureContract_IsAccepted()
    {
        var budget = ReportBudget.Read(Path.Combine(FixtureLoader.Fixtures("large/100k-tests"), "expected.json"));

        // The committed state is `unset` until the quiet-host run records numbers; a `measured` commit must still read.
        budget.Status.ShouldBe(BudgetStatus.Unset);

        budget.Compare(AtLimit).Message.ShouldContain(ReportBudget.RecordTheseNumbers);
    }

    private static ReportBudget Budget(string json)
    {
        using var document = JsonDocument.Parse(json);

        return ReportBudget.FromMember(document.RootElement);
    }
}
