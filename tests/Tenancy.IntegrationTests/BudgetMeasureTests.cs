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

    [Theory]
    [InlineData("parse_wall_ms")]
    [InlineData("reconcile_wall_ms")]
    [InlineData("walk_wall_ms")]
    [InlineData("child_peak_working_set_bytes")]
    [InlineData("parent_inflight_retained_bytes")]
    public void Compare_OverOneLimit_FailsWithMeasuredAndLimit(string metric)
    {
        var limits = string.Join(
            ",",
            BudgetMetrics.Names.Select(name => $"\"{name}\":{(name == metric ? 999 : 1000)}"));
        var budget = Budget("{\"status\":\"measured\",\"limits\":{" + limits + "}}");

        var verdict = budget.Compare(AtLimit);

        verdict.WithinBudget.ShouldBeFalse();
        verdict.Status.ShouldBe(BudgetStatus.Measured);
        verdict.Message.ShouldStartWith("budget exceeded by 1 of 5 metrics");
        verdict.Message.ShouldContain($"{metric} measured=1000 limit=999");
    }

    [Fact]
    public void Compare_OverEveryLimit_NamesEveryExceededMetric()
    {
        var budget = Budget("{\"status\":\"measured\",\"limits\":{" + FiveLimits + "}}");

        var verdict = budget.Compare(new BudgetMetrics(1001, 1001, 1001, 1001, 1001));

        verdict.WithinBudget.ShouldBeFalse();
        verdict.Message.ShouldStartWith("budget exceeded by 5 of 5 metrics");
        foreach (var metric in BudgetMetrics.Names)
        {
            verdict.Message.ShouldContain($"{metric} measured=1001 limit=1000");
        }
    }

    [Theory]
    [InlineData("child_peak_working_set_bytes", "{\"status\":\"unset\"}")]
    [InlineData("parent_inflight_retained_bytes", "{\"status\":\"unset\"}")]
    [InlineData("child_peak_working_set_bytes", "{\"status\":\"measured\",\"limits\":{" + FiveLimits + "}}")]
    [InlineData("parent_inflight_retained_bytes", "{\"status\":\"measured\",\"limits\":{" + FiveLimits + "}}")]
    public void Compare_ZeroByteMeasurement_IsRefused(string metric, string budgetJson)
    {
        var budget = Budget(budgetJson);
        var measured = metric == "child_peak_working_set_bytes"
            ? AtLimit with { ChildPeakWorkingSetBytes = 0 }
            : AtLimit with { ParentInflightRetainedBytes = 0 };

        var error = Should.Throw<InvalidOperationException>(() => budget.Compare(measured));

        error.Message.ShouldContain(ReportBudget.MeasurementMissing);
        error.Message.ShouldContain(metric);
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
    public void Read_CommittedFixtureContract_AcceptsUnsetOrCompleteMeasurement()
    {
        var budget = ReportBudget.Read(Path.Combine(FixtureLoader.Fixtures("large/100k-tests"), "expected.json"));

        // Both committed states are valid: `unset` before a quiet-host run records numbers, `measured` after it.
        if (budget.Status == BudgetStatus.Unset)
        {
            budget.Limits.ShouldBeEmpty();
            return;
        }

        budget.Status.ShouldBe(BudgetStatus.Measured);
        budget.Limits.Count.ShouldBe(BudgetMetrics.Names.Length);
        foreach (var name in BudgetMetrics.Names)
        {
            budget.Limits[name].ShouldBeGreaterThanOrEqualTo(0L);
        }

        budget.Compare(AtLimit).Status.ShouldBe(BudgetStatus.Measured);
    }

    private static ReportBudget Budget(string json)
    {
        using var document = JsonDocument.Parse(json);

        return ReportBudget.FromMember(document.RootElement);
    }
}
