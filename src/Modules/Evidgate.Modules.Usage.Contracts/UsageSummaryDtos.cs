namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// The two scope labels a counter answer carries: the organization-wide counter (<c>quota_counter.project_id</c>
/// null) and one project's counter. They are wire strings, like every other status the API serves, so a client
/// compares the literal and never a numeric enum value.
/// </summary>
public static class UsageSummaryScopes
{
    /// <summary>The organization-wide counter, which caps every project of the tenant together.</summary>
    public const string Tenant = "tenant";

    /// <summary>The counter of one project.</summary>
    public const string Project = "project";
}

/// <summary>
/// The <c>200</c> answer of <c>GET /v1/orgs/{o}/usage/summary</c>: one entry per wire metric for the current
/// billing period, the organization-wide counter and one page of project counters, or the single project the caller
/// named.
/// </summary>
/// <param name="Period">The billing period the counters belong to, <c>YYYY-MM</c>, from the server's clock.</param>
/// <param name="Project">The project filter the request named, or null for the org-wide answer.</param>
/// <param name="Metrics">One entry per metric, in <see cref="UsageLedgerMetricNames"/> order.</param>
/// <param name="NextCursor">The next project page's opaque continuation, or null on the last or project-filtered page.</param>
public sealed record UsageSummaryDto(
    string Period,
    Guid? Project,
    IReadOnlyList<UsageSummaryMetricDto> Metrics,
    string? NextCursor);

/// <summary>
/// One metric's counters for the period. <see cref="Used"/>, <see cref="Limit"/> and the threshold stamps describe
/// the organization-wide counter; <see cref="Projects"/> holds the page's per-project counters, or the one project the
/// request named.
/// <para>
/// A metric with no producer in this build answers <see cref="Used"/> and <see cref="Limit"/> as null and names the
/// missing producer in <see cref="UnavailableReason"/>, never as <c>0</c>: a zero would read as a measurement.
/// A metered metric with no row yet answers <c>used</c> 0 and a null limit.
/// </para>
/// </summary>
/// <param name="Metric">The wire metric name the ledger stores.</param>
/// <param name="Scope">Always <see cref="UsageSummaryScopes.Tenant"/>: these are the organization-wide numbers.</param>
/// <param name="Used">
/// The tenant counter's <c>used</c>; when the month has no tenant counter, the sum of the period's roll-up totals
/// across the organization. Null only when the metric is unavailable.
/// </param>
/// <param name="Limit">The tenant counter's configured limit, or null when the month has no tenant counter.</param>
/// <param name="WarnedAt">When the tenant counter crossed its warning line, or null.</param>
/// <param name="BlockedAt">When the tenant counter refused, or null.</param>
/// <param name="RetryAfterSeconds">
/// On a blocked tenant counter only: whole seconds until the next period starts, so a client can retry then.
/// </param>
/// <param name="UnavailableReason">Why the metric cannot be reported, or null when it can.</param>
/// <param name="Projects">The page's per-project counters, ordered by project creation time then id.</param>
/// <param name="ProjectsStatus">
/// How many of the organization's projects this metric warns or blocks, over every project and never only the page.
/// </param>
public sealed record UsageSummaryMetricDto(
    string Metric,
    string Scope,
    decimal? Used,
    decimal? Limit,
    DateTimeOffset? WarnedAt,
    DateTimeOffset? BlockedAt,
    long? RetryAfterSeconds,
    string? UnavailableReason,
    IReadOnlyList<UsageSummaryProjectDto> Projects,
    UsageSummaryProjectsStatusDto ProjectsStatus);

/// <summary>
/// One metric's project stamps across the whole organization for the period: a count a client can render beside the
/// page without walking it. Both counts are always present, and a metric the month holds no project counter for
/// answers <c>0</c>/<c>0</c>.
/// </summary>
/// <param name="BlockedCount">Projects whose counter of this metric refused in the period.</param>
/// <param name="WarnedCount">
/// Projects whose counter of this metric warned and did not refuse: a blocked project is counted as blocked only, so
/// the two counts never describe one project.
/// </param>
public sealed record UsageSummaryProjectsStatusDto(
    long BlockedCount,
    long WarnedCount);

/// <summary>
/// One project's counter for one metric. A project the request named that has no counter or roll-up row yet answers
/// <c>used</c> 0 and a null limit, so a client can render "nothing measured" without inventing a number.
/// </summary>
/// <param name="ProjectId">The project the counter belongs to.</param>
/// <param name="Scope">Always <see cref="UsageSummaryScopes.Project"/>.</param>
/// <param name="Used">The project counter's <c>used</c>, or the project's roll-up total when no counter exists.</param>
/// <param name="Limit">The project counter's configured limit, or null when the month has no project counter.</param>
/// <param name="WarnedAt">When the project counter crossed its warning line, or null.</param>
/// <param name="BlockedAt">When the project counter refused, or null.</param>
/// <param name="RetryAfterSeconds">On a blocked counter only: whole seconds until the next period starts.</param>
public sealed record UsageSummaryProjectDto(
    Guid ProjectId,
    string Scope,
    decimal? Used,
    decimal? Limit,
    DateTimeOffset? WarnedAt,
    DateTimeOffset? BlockedAt,
    long? RetryAfterSeconds);
