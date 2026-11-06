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
/// billing period, the organization-wide counter and, when the caller names a project, that project's counter.
/// </summary>
/// <param name="Period">The billing period the counters belong to, <c>YYYY-MM</c>, from the server's clock.</param>
/// <param name="Project">The project filter the request named, or null for the org-wide answer.</param>
/// <param name="Metrics">One entry per metric, in <see cref="UsageLedgerMetricNames"/> order.</param>
public sealed record UsageSummaryDto(
    string Period,
    Guid? Project,
    IReadOnlyList<UsageSummaryMetricDto> Metrics);

/// <summary>
/// One metric's counters for the period. <see cref="Used"/>, <see cref="Limit"/> and the threshold stamps describe
/// the organization-wide counter; <see cref="Projects"/> holds the per-project counters the request asked for.
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
/// <param name="Projects">The per-project counters, ordered by project id; empty when none was asked for or seen.</param>
public sealed record UsageSummaryMetricDto(
    string Metric,
    string Scope,
    decimal? Used,
    decimal? Limit,
    DateTimeOffset? WarnedAt,
    DateTimeOffset? BlockedAt,
    long? RetryAfterSeconds,
    string? UnavailableReason,
    IReadOnlyList<UsageSummaryProjectDto> Projects);

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
