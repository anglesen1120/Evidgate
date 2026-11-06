using System.Globalization;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The summary's mapping rules, without a database: which metric is unavailable and why, what a metered metric with no
/// row answers, how a counter is labelled, when the retry hint is written, and when the answer is the 404 of a project
/// the org does not hold.
/// </summary>
/// <remarks>
/// The composition is pure (<see cref="UsageSummaryComposition"/>), so every rule the wire contract promises is pinned
/// here; the statement that produces the rows is proven by the Docker suites.
/// </remarks>
public sealed class UsageSummaryCompositionTests
{
    private const string Period = "2026-11";

    private static readonly DateTimeOffset Now = new(2026, 11, 30, 23, 59, 30, TimeSpan.Zero);

    /// <summary>AC8: a metric with no producer is null with a reason, never 0 — and a metered one is 0.</summary>
    [Fact]
    public void UsageSummary_UnmeteredMetric_IsNull()
    {
        var summary = Compose();
        var egress = Metric(summary, UsageLedgerMetricNames.EgressGb);
        var results = Metric(summary, UsageLedgerMetricNames.Results);

        egress.Used.ShouldBeNull();
        egress.Limit.ShouldBeNull();
        egress.UnavailableReason.ShouldNotBeNullOrEmpty();
        egress.RetryAfterSeconds.ShouldBeNull();
        egress.Projects.ShouldBeEmpty();

        results.Used.ShouldBe(0m);
        results.Limit.ShouldBeNull();
        results.UnavailableReason.ShouldBeNull();

        summary.Metrics.Count.ShouldBe(UsageLedgerMetricNames.All.Count);
        summary.Metrics.Select(metric => metric.Metric).ShouldBe(UsageLedgerMetricNames.All);
    }

    [Fact]
    public void UsageSummary_TenantAndProjectCounters_AreLabelledAndScoped()
    {
        var project = Guid.CreateVersion7();
        var warnedAt = Now.AddHours(-2);
        var summary = ComposeFor(
            project,
            Rows.Tenant(UsageLedgerMetricNames.Results, used: 4m, limit: 10m, warnedAt: warnedAt),
            Rows.Project(UsageLedgerMetricNames.Results, project, used: 2m, limit: 5m));

        var results = Metric(summary, UsageLedgerMetricNames.Results);

        results.Scope.ShouldBe(UsageSummaryScopes.Tenant);
        results.Used.ShouldBe(4m);
        results.Limit.ShouldBe(10m);
        results.WarnedAt.ShouldBe(warnedAt);

        var only = results.Projects.ShouldHaveSingleItem();
        only.ProjectId.ShouldBe(project);
        only.Scope.ShouldBe(UsageSummaryScopes.Project);
        only.Used.ShouldBe(2m);
        only.Limit.ShouldBe(5m);
        summary.Project.ShouldBe(project);
        summary.Period.ShouldBe(Period);
    }

    [Fact]
    public void UsageSummary_RequestedProjectWithoutRow_IsZeroWithNullLimit()
    {
        var project = Guid.CreateVersion7();
        var summary = ComposeFor(project);

        var only = Metric(summary, UsageLedgerMetricNames.Results).Projects.ShouldHaveSingleItem();
        only.ProjectId.ShouldBe(project);
        only.Scope.ShouldBe(UsageSummaryScopes.Project);
        only.Used.ShouldBe(0m);
        only.Limit.ShouldBeNull();
        only.WarnedAt.ShouldBeNull();
        only.RetryAfterSeconds.ShouldBeNull();
    }

    [Fact]
    public void UsageSummary_BlockedCounter_CarriesTheGateRetryHint()
    {
        var blockedAt = Now.AddMinutes(-5);
        var summary = Compose(Rows.Tenant(UsageLedgerMetricNames.Results, 10m, 10m, blockedAt: blockedAt));
        var results = Metric(summary, UsageLedgerMetricNames.Results);

        results.BlockedAt.ShouldBe(blockedAt);
        results.RetryAfterSeconds.ShouldBe(30L);
    }

    [Fact]
    public void UsageSummary_MissingProjectMarker_IsThe404Answer()
    {
        var marker = new UsageSummaryRow { ProjectMissing = true };

        UsageSummaryComposition.Compose(Period, Guid.CreateVersion7(), Now, [marker]).ShouldBeNull();
    }

    [Fact]
    public void UsageSummary_ProjectRows_AreOrderedByProject()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var summary = Compose(
            Rows.Project(UsageLedgerMetricNames.Results, second, 1m, null),
            Rows.Project(UsageLedgerMetricNames.Results, first, 2m, null));
        var projects = Metric(summary, UsageLedgerMetricNames.Results).Projects;

        projects.Select(project => project.ProjectId).ShouldBe(
            new[] { first, second }.OrderBy(id => id));
    }

    /// <summary>
    /// A counter blocked 30 seconds before January is answered with a 30 second hint on the clock's month, and the same
    /// metric read at January's first instant answers the new month with nothing measured and no hint.
    /// </summary>
    [Fact]
    public void UsageSummary_BlockedJustBeforeJanuary_HintsWholeSecondsAndJanuaryStartsEmpty()
    {
        var december = new DateTimeOffset(2026, 12, 31, 23, 59, 30, TimeSpan.Zero);
        var january = december.AddSeconds(31);
        var decemberPeriod = december.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var januaryPeriod = january.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var decemberRows = new[] { Rows.Tenant(UsageLedgerMetricNames.Results, 10m, 10m, blockedAt: december.AddMinutes(-1)) };

        var before = UsageSummaryComposition.Compose(decemberPeriod, null, december, decemberRows);
        var after = UsageSummaryComposition.Compose(januaryPeriod, null, january, []);
        before.ShouldNotBeNull();
        after.ShouldNotBeNull();

        // The two instants really straddle the year, so the hint is measured against a month that is about to end.
        decemberPeriod.ShouldBe("2026-12");
        januaryPeriod.ShouldBe("2027-01");
        Metric(before!, UsageLedgerMetricNames.Results).RetryAfterSeconds.ShouldBe(30L);

        var fresh = Metric(after!, UsageLedgerMetricNames.Results);
        before!.Period.ShouldBe(decemberPeriod);
        after!.Period.ShouldBe(januaryPeriod);
        fresh.Used.ShouldBe(0m);
        fresh.Limit.ShouldBeNull();
        fresh.BlockedAt.ShouldBeNull();
        fresh.RetryAfterSeconds.ShouldBeNull();
    }

    private static UsageSummaryDto Compose(params UsageSummaryRow[] rows) => ComposeFor(null, rows);

    private static UsageSummaryDto ComposeFor(Guid? requestedProject, params UsageSummaryRow[] rows)
    {
        var summary = UsageSummaryComposition.Compose(Period, requestedProject, Now, rows);
        summary.ShouldNotBeNull();
        return summary!;
    }

    private static UsageSummaryMetricDto Metric(UsageSummaryDto summary, string wire) =>
        summary.Metrics.Single(metric => metric.Metric == wire);

    private static class Rows
    {
        internal static UsageSummaryRow Tenant(
            string metric,
            decimal used,
            decimal? limit,
            DateTimeOffset? warnedAt = null,
            DateTimeOffset? blockedAt = null) =>
            new()
            {
                Scope = UsageSummaryScopes.Tenant,
                Metric = metric,
                Used = used,
                QuotaLimit = limit,
                WarnedAt = warnedAt,
                BlockedAt = blockedAt,
            };

        internal static UsageSummaryRow Project(string metric, Guid projectId, decimal used, decimal? limit) =>
            new()
            {
                Scope = UsageSummaryScopes.Project,
                ProjectId = projectId,
                Metric = metric,
                Used = used,
                QuotaLimit = limit,
            };
    }

    /// <summary>Every refusal branch of the project-scope rule, and the one answer that lets the read through.</summary>
    [Fact]
    public void ProjectScope_RefusesEveryOutOfScopeCombination()
    {
        var own = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        UsageProjectScope.Project(own, null, null).ShouldBeNull();
        UsageProjectScope.Project(own, own, own).ShouldBeNull();
        UsageProjectScope.Project(own, other, null)!.Code.ShouldBe("project_scope_mismatch");
        UsageProjectScope.Project(own, null, other)!.Code.ShouldBe("project_scope_mismatch");
        UsageProjectScope.Project(own, own, other)!.Code.ShouldBe("project_scope_mismatch");
        UsageProjectScope.Project(own, other, own)!.Code.ShouldBe("project_scope_mismatch");
    }
}
