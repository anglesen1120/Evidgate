using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Queries;

/// <summary>
/// Turns the summary statement's rows into the wire answer: one metric entry per declared metric, the
/// organization-wide counter first and one page of per-project counters under it. Pure, so every
/// null/zero/labelling/paging rule is testable without a database.
/// </summary>
/// <remarks>
/// The rules this composition owns:
/// <list type="bullet">
/// <item>A metric with no producer (<see cref="UsageMetricAvailability"/>) answers null <c>used</c> and <c>limit</c>
/// with the reason, never <c>0</c>.</item>
/// <item>A metered metric with no counter and no roll-up row answers <c>used</c> 0 and a null limit: the month measured
/// nothing, which is not the same as "nobody can measure this".</item>
/// <item>The <c>retry_after_seconds</c> hint is written only on a blocked scope, and its value is the gate's own
/// wait until the next period (<see cref="UsagePeriod.RetryUntilNextPeriod"/>).</item>
/// <item>A limit is never carried from an earlier period: only the current period's counter can contribute one.</item>
/// <item>Paging is over projects, in <c>(created_at, id)</c> order: every metric describes the same page, and the
/// continuation is the last reported project's keyset. Tenant totals are never paged.</item>
/// </list>
/// </remarks>
internal static class UsageSummaryComposition
{
    /// <summary>
    /// Builds the answer for <paramref name="period"/>, or null when the statement reported the requested project as
    /// missing from the caller's tenant (the caller maps that to 404).
    /// </summary>
    /// <param name="period">The current period, <c>YYYY-MM</c>.</param>
    /// <param name="requestedProject">The project the request named, or null for the org-wide answer.</param>
    /// <param name="now">The instant the period was derived from; the retry hint is measured from it.</param>
    /// <param name="rows">The statement's rows, already scoped to the org.</param>
    /// <param name="limit">
    /// Projects this page reports. The statement is asked for one project more, so a full page says whether a
    /// continuation exists without a second read.
    /// </param>
    internal static UsageSummaryDto? Compose(
        string period,
        Guid? requestedProject,
        DateTimeOffset now,
        IReadOnlyList<UsageSummaryRow> rows,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // The marker row is the statement's whole answer for a project outside the tenant: no counter, no roll-up,
        // nothing to report - the same 404 the other Usage reads answer.
        if (rows.Any(row => row.ProjectMissing))
        {
            return null;
        }

        var tenant = new Dictionary<string, UsageSummaryRow>(StringComparer.Ordinal);
        var projects = new Dictionary<(string Metric, Guid ProjectId), UsageSummaryRow>();
        foreach (var row in rows)
        {
            Index(row, tenant, projects);
        }

        var page = Page(projects.Values, limit, out var more);
        var metrics = new List<UsageSummaryMetricDto>(UsageLedgerMetricNames.All.Count);
        foreach (var metric in Enum.GetValues<UsageMetric>())
        {
            metrics.Add(Metric(metric, requestedProject, tenant, projects, page, period, now));
        }

        // A filtered answer is one project by definition, so only the org-wide page can continue.
        return new UsageSummaryDto(
            period,
            requestedProject,
            metrics,
            requestedProject is null && more
                ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].ProjectId)
                : null);
    }

    /// <summary>
    /// The page's projects in keyset order, trimmed to <paramref name="limit"/>; <paramref name="more"/> says a
    /// further project was read. Membership comes from the rows: every project the statement names has a counter or a
    /// roll-up in the period, so it contributes at least one row of that page.
    /// </summary>
    private static List<(Guid ProjectId, DateTimeOffset CreatedAt)> Page(
        IEnumerable<UsageSummaryRow> rows,
        int limit,
        out bool more)
    {
        // The statement fills the project's creation time on every project row; the instant is what the keyset orders
        // by, so a page boundary never depends on the project ids' own order.
        var ordered = rows
            .Where(row => row.ProjectId.HasValue)
            .Select(row => (
                ProjectId: row.ProjectId.GetValueOrDefault(),
                CreatedAt: row.ProjectCreatedAt ?? DateTimeOffset.MinValue))
            .DistinctBy(entry => entry.ProjectId)
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.ProjectId)
            .ToList();

        more = ordered.Count > limit;
        if (more)
        {
            ordered.RemoveRange(limit, ordered.Count - limit);
        }

        return ordered;
    }

    /// <summary>Files one statement row under the scope it answers; the marker row carries no metric and is skipped.</summary>
    private static void Index(
        UsageSummaryRow row,
        Dictionary<string, UsageSummaryRow> tenant,
        Dictionary<(string Metric, Guid ProjectId), UsageSummaryRow> projects)
    {
        if (row.Metric is not { } metric)
        {
            return;
        }

        if (row.Scope == UsageSummaryScopes.Tenant)
        {
            tenant[metric] = row;
        }
        else if (row.ProjectId is { } projectId)
        {
            projects[(metric, projectId)] = row;
        }
    }

    /// <summary>One metric's answer: the unavailable null pair with its reason, or the tenant counter and its projects.</summary>
    private static UsageSummaryMetricDto Metric(
        UsageMetric metric,
        Guid? requestedProject,
        Dictionary<string, UsageSummaryRow> tenant,
        Dictionary<(string Metric, Guid ProjectId), UsageSummaryRow> projects,
        List<(Guid ProjectId, DateTimeOffset CreatedAt)> page,
        string period,
        DateTimeOffset now)
    {
        var wire = UsageMetricWire.ToWireValue(metric);
        if (UsageMetricAvailability.UnavailableReason(metric) is { } reason)
        {
            return new UsageSummaryMetricDto(
                wire,
                UsageSummaryScopes.Tenant,
                Used: null,
                Limit: null,
                WarnedAt: null,
                BlockedAt: null,
                RetryAfterSeconds: null,
                reason,
                Projects: []);
        }

        tenant.TryGetValue(wire, out var counter);
        return new UsageSummaryMetricDto(
            wire,
            UsageSummaryScopes.Tenant,
            counter?.Used ?? 0m,
            counter?.QuotaLimit,
            counter?.WarnedAt,
            counter?.BlockedAt,
            RetrySeconds(counter?.BlockedAt, period, now),
            UnavailableReason: null,
            ProjectRows(wire, requestedProject, projects, page, period, now));
    }

    /// <summary>
    /// One metric over the page's projects, in the page's order: a project the metric has no row for is its zero, so
    /// every metric of one page describes the same projects. The requested project is added when it has no row at all,
    /// which is the filtered answer's whole content.
    /// </summary>
    private static List<UsageSummaryProjectDto> ProjectRows(
        string wire,
        Guid? requestedProject,
        Dictionary<(string Metric, Guid ProjectId), UsageSummaryRow> counters,
        List<(Guid ProjectId, DateTimeOffset CreatedAt)> page,
        string period,
        DateTimeOffset now)
    {
        var found = new List<UsageSummaryProjectDto>(page.Count + 1);
        foreach (var entry in page)
        {
            found.Add(counters.TryGetValue((wire, entry.ProjectId), out var counter)
                ? Project(entry.ProjectId, counter, period, now)
                : Zero(entry.ProjectId));
        }

        if (requestedProject is { } projectId && found.TrueForAll(row => row.ProjectId != projectId))
        {
            found.Add(Zero(projectId));
        }

        return found;
    }

    /// <summary>One project's counter, or the roll-up total the statement answered for it.</summary>
    private static UsageSummaryProjectDto Project(
        Guid projectId,
        UsageSummaryRow row,
        string period,
        DateTimeOffset now) =>
        new(
            projectId,
            UsageSummaryScopes.Project,
            row.Used ?? 0m,
            row.QuotaLimit,
            row.WarnedAt,
            row.BlockedAt,
            RetrySeconds(row.BlockedAt, period, now));

    /// <summary>A project the month measured nothing for: zero used, no limit, no threshold and no hint.</summary>
    private static UsageSummaryProjectDto Zero(Guid projectId) =>
        new(
            projectId,
            UsageSummaryScopes.Project,
            Used: 0m,
            Limit: null,
            WarnedAt: null,
            BlockedAt: null,
            RetryAfterSeconds: null);

    /// <summary>Whole seconds until the next period on a blocked scope, or null when the scope is not blocked.</summary>
    private static long? RetrySeconds(DateTimeOffset? blockedAt, string period, DateTimeOffset now) =>
        blockedAt is null
            ? null
            : (long)Math.Ceiling(UsagePeriod.RetryUntilNextPeriod(period, now).TotalSeconds);
}
