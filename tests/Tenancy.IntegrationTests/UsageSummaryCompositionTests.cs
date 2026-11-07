using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The summary's mapping rules, without a database: which metric is unavailable and why, what a metered metric with no
/// row answers, how a counter is labelled, when the retry hint is written, how a project page is trimmed and continued,
/// and when the answer is the 404 of a project the org does not hold.
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

        UsageSummaryComposition.Compose(Period, Guid.CreateVersion7(), Now, [marker], 1).ShouldBeNull();
    }

    [Fact]
    public void UsageSummary_ProjectRows_AreOrderedByCreationThenId()
    {
        // Ids are random so the id order cannot be what produces the answer: the older project's id sorts after the
        // newer one's here, and the page still reports the older project first.
        var newer = Guid.NewGuid();
        var older = Guid.NewGuid();
        var summary = Compose(
            Rows.Project(UsageLedgerMetricNames.Results, newer, 1m, null, Now.AddHours(-1)),
            Rows.Project(UsageLedgerMetricNames.Results, older, 2m, null, Now.AddHours(-2)));
        var projects = Metric(summary, UsageLedgerMetricNames.Results).Projects;

        projects.Select(project => project.ProjectId).ShouldBe([older, newer]);
    }

    [Fact]
    public void UsageSummary_ProjectsOfOneInstant_AreOrderedById()
    {
        var instant = Now.AddHours(-1);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var expected = new[] { first, second }.OrderBy(id => id).ToArray();
        var summary = Compose(
            Rows.Project(UsageLedgerMetricNames.Results, first, 1m, null, instant),
            Rows.Project(UsageLedgerMetricNames.Results, second, 2m, null, instant));

        Metric(summary, UsageLedgerMetricNames.Results).Projects
            .Select(project => project.ProjectId).ShouldBe(expected);
    }

    [Fact]
    public void UsageSummary_ProjectPage_TrimsTheLookaheadAndContinuesAfterTheLastKeptProject()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var firstAt = Now.AddHours(-3);
        var secondAt = Now.AddHours(-2);
        var summary = ComposePage(
            2,
            null,
            Rows.Project(UsageLedgerMetricNames.Results, first, 1m, null, firstAt),
            Rows.Project(UsageLedgerMetricNames.Results, second, 2m, null, secondAt),
            Rows.Project(UsageLedgerMetricNames.Results, third, 3m, null, Now.AddHours(-1)));

        Metric(summary, UsageLedgerMetricNames.Results).Projects
            .Select(project => project.ProjectId).ShouldBe([first, second]);

        summary.NextCursor.ShouldBe(KeysetCursor.Encode(secondAt, second));
        KeysetCursor.TryDecode(summary.NextCursor, out var createdAt, out var id).ShouldBeTrue();
        createdAt.ShouldBe(secondAt);
        id.ShouldBe(second);
    }

    [Fact]
    public void UsageSummary_ProjectPageOfExactlyTheLimit_HasNoContinuation()
    {
        var summary = ComposePage(
            2,
            null,
            Rows.Project(UsageLedgerMetricNames.Results, Guid.NewGuid(), 1m, null, Now.AddHours(-2)),
            Rows.Project(UsageLedgerMetricNames.Results, Guid.NewGuid(), 2m, null, Now.AddHours(-1)));

        summary.NextCursor.ShouldBeNull();
        Metric(summary, UsageLedgerMetricNames.Results).Projects.Count.ShouldBe(2);
    }

    [Fact]
    public void UsageSummary_PageWithoutProjects_HasNoContinuation()
    {
        var summary = Compose(Rows.Tenant(UsageLedgerMetricNames.Results, 4m, 10m));

        Metric(summary, UsageLedgerMetricNames.Results).Projects.ShouldBeEmpty();
        summary.NextCursor.ShouldBeNull();
    }

    [Fact]
    public void UsageSummary_ProjectFilteredAnswer_NeverCarriesAContinuation()
    {
        var project = Guid.CreateVersion7();
        var summary = ComposePage(
            1,
            project,
            Rows.Project(UsageLedgerMetricNames.Results, project, 1m, null, Now));

        summary.Project.ShouldBe(project);
        summary.NextCursor.ShouldBeNull();
        Metric(summary, UsageLedgerMetricNames.Results).Projects
            .ShouldHaveSingleItem().ProjectId.ShouldBe(project);
    }

    [Fact]
    public void UsageSummary_PageProjects_AppearInEveryMeteredMetric()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var summary = Compose(
            Rows.Project(UsageLedgerMetricNames.Results, first, 4m, 10m, Now.AddHours(-2)),
            Rows.Project(UsageLedgerMetricNames.ArtifactGbMonth, second, 2m, null, Now.AddHours(-1)));

        // The page is over projects, so the metered metric describes both of them: the project the month has no
        // results row for is its zero, which is what the filtered answer already does for its one project.
        var results = Metric(summary, UsageLedgerMetricNames.Results).Projects;
        results.Select(project => project.ProjectId).ShouldBe([first, second]);
        results[0].Used.ShouldBe(4m);
        results[1].Used.ShouldBe(0m);
        results[1].Limit.ShouldBeNull();

        // A metric with no producer still answers no projects: there is nothing to report for either of them.
        var unavailable = Metric(summary, UsageLedgerMetricNames.ArtifactGbMonth);
        unavailable.UnavailableReason.ShouldNotBeNullOrEmpty();
        unavailable.Projects.ShouldBeEmpty();
    }

    /// <summary>
    /// A statement with no alert row answers zeros for every metric: the counts are always present, and a metric nobody
    /// stamped is a genuine zero rather than an unknown.
    /// </summary>
    [Fact]
    public void UsageSummary_ProjectsStatus_IsZeroWithoutAnAlertRow()
    {
        var summary = Compose(Rows.Tenant(UsageLedgerMetricNames.Results, 4m, 10m));

        foreach (var metric in summary.Metrics)
        {
            metric.ProjectsStatus.BlockedCount.ShouldBe(0L);
            metric.ProjectsStatus.WarnedCount.ShouldBe(0L);
        }
    }

    [Fact]
    public void UsageSummary_ProjectsStatus_ComesFromTheAlertRowOfItsOwnMetric()
    {
        var project = Guid.CreateVersion7();
        var summary = ComposeFor(
            project,
            Rows.Tenant(UsageLedgerMetricNames.Results, used: 4m, limit: 10m),
            Rows.Project(UsageLedgerMetricNames.Results, project, used: 10m, limit: 10m, createdAt: Now.AddHours(-2)),
            Rows.Alert(UsageLedgerMetricNames.Results, blocked: 2, warned: 3));

        var results = Metric(summary, UsageLedgerMetricNames.Results);
        results.ProjectsStatus.BlockedCount.ShouldBe(2L);
        results.ProjectsStatus.WarnedCount.ShouldBe(3L);

        // The alert row describes the org, so it is neither a project row nor a total of the answer it rides in.
        results.Used.ShouldBe(4m);
        results.Projects.Select(row => row.ProjectId).ShouldBe([project]);

        // Another metric carries its own count, so a neighbour's stamps never apply.
        var egress = Metric(summary, UsageLedgerMetricNames.EgressGb);
        egress.ProjectsStatus.BlockedCount.ShouldBe(0L);
        egress.ProjectsStatus.WarnedCount.ShouldBe(0L);
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

        var before = UsageSummaryComposition.Compose(
            decemberPeriod, null, december, decemberRows, UsageSummaryPaging.DefaultLimit);
        var after = UsageSummaryComposition.Compose(
            januaryPeriod, null, january, [], UsageSummaryPaging.DefaultLimit);
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

    private static UsageSummaryDto ComposeFor(Guid? requestedProject, params UsageSummaryRow[] rows) =>
        ComposePage(UsageSummaryPaging.DefaultLimit, requestedProject, rows);

    private static UsageSummaryDto ComposePage(
        int limit,
        Guid? requestedProject,
        params UsageSummaryRow[] rows)
    {
        var summary = UsageSummaryComposition.Compose(Period, requestedProject, Now, rows, limit);
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

        internal static UsageSummaryRow Project(
            string metric,
            Guid projectId,
            decimal used,
            decimal? limit,
            DateTimeOffset? createdAt = null) =>
            new()
            {
                Scope = UsageSummaryScopes.Project,
                ProjectId = projectId,
                ProjectCreatedAt = createdAt,
                Metric = metric,
                Used = used,
                QuotaLimit = limit,
            };

        /// <summary>One metric's org-wide project counts, the scope label the statement gives them.</summary>
        internal static UsageSummaryRow Alert(string metric, long blocked, long warned) =>
            new()
            {
                Scope = "alerts",
                Metric = metric,
                BlockedProjects = blocked,
                WarnedProjects = warned,
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
