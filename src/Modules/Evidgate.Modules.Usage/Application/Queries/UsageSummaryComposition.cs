using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Queries;

/// <summary>
/// Turns the summary statement's rows into the wire answer: one metric entry per declared metric, the
/// organization-wide counter first and the per-project counters under it. Pure, so every null/zero/labelling rule is
/// testable without a database.
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
    internal static UsageSummaryDto? Compose(
        string period,
        Guid? requestedProject,
        DateTimeOffset now,
        IReadOnlyList<UsageSummaryRow> rows)
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

        var metrics = new List<UsageSummaryMetricDto>(UsageLedgerMetricNames.All.Count);
        foreach (var metric in Enum.GetValues<UsageMetric>())
        {
            metrics.Add(Metric(metric, requestedProject, tenant, projects, period, now));
        }

        return new UsageSummaryDto(period, requestedProject, metrics);
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
            ProjectRows(wire, requestedProject, projects, period, now));
    }

    /// <summary>The project counters of one metric, ordered by project id, plus the requested project's zero when it
    /// has no row yet.</summary>
    private static List<UsageSummaryProjectDto> ProjectRows(
        string wire,
        Guid? requestedProject,
        Dictionary<(string Metric, Guid ProjectId), UsageSummaryRow> counters,
        string period,
        DateTimeOffset now)
    {
        var found = counters
            .Where(entry => entry.Key.Metric == wire)
            .OrderBy(entry => entry.Key.ProjectId)
            .Select(entry => new UsageSummaryProjectDto(
                entry.Key.ProjectId,
                UsageSummaryScopes.Project,
                entry.Value.Used ?? 0m,
                entry.Value.QuotaLimit,
                entry.Value.WarnedAt,
                entry.Value.BlockedAt,
                RetrySeconds(entry.Value.BlockedAt, period, now)))
            .ToList();

        if (requestedProject is { } projectId && found.TrueForAll(row => row.ProjectId != projectId))
        {
            found.Add(new UsageSummaryProjectDto(
                projectId,
                UsageSummaryScopes.Project,
                Used: 0m,
                Limit: null,
                WarnedAt: null,
                BlockedAt: null,
                RetryAfterSeconds: null));
        }

        return found;
    }

    /// <summary>Whole seconds until the next period on a blocked scope, or null when the scope is not blocked.</summary>
    private static long? RetrySeconds(DateTimeOffset? blockedAt, string period, DateTimeOffset now) =>
        blockedAt is null
            ? null
            : (long)Math.Ceiling(UsagePeriod.RetryUntilNextPeriod(period, now).TotalSeconds);
}
