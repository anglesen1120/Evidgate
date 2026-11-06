namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// The quota verdict: <see cref="Ok"/> below the warning line, <see cref="Warn"/> from
/// <c>warn_ratio</c> of the limit and <see cref="Block"/> at 100 %, which the caller maps to 429
/// <c>quota_exceeded</c> with <c>Retry-After</c>.
/// <para>
/// <see cref="Ok"/> is an answer the gate gave after running, including an org with no counter row or
/// a gate an operator switched off (see <see cref="IQuotaGate"/>); it is never a missing registration.
/// </para>
/// </summary>
public enum QuotaDecision
{
    /// <summary>The call is within quota; the caller proceeds.</summary>
    Ok,

    /// <summary>The call crosses the 80 % warning line; the caller proceeds and the first crossing notifies.</summary>
    Warn,

    /// <summary>The call would exceed the limit; the caller refuses it with 429 <c>quota_exceeded</c>.</summary>
    Block,
}

/// <summary>
/// One quota question a hot path asks before it commits metered work (ruling T53): the metric, the period, the
/// project (null for a tenant-level limit) and the quantity the call is about to commit.
/// </summary>
/// <param name="Metric">The metric being metered.</param>
/// <param name="Period">The billing period, exactly <c>YYYY-MM</c>.</param>
/// <param name="ProjectId">The project the usage belongs to, or null for a tenant-level limit.</param>
/// <param name="Quantity">
/// The amount this call would add to <c>quota_counter.used</c>, strictly positive: a hot path asks about work it is
/// about to commit, and a correction is an adjustment of the ledger, not a negative reservation here. The gate refuses
/// a non-positive quantity loudly, before it writes.
/// </param>
public sealed record QuotaCheckRequest(
    UsageMetric Metric,
    string Period,
    Guid? ProjectId,
    decimal Quantity);

/// <summary>
/// The answer of one <see cref="IQuotaGate.CheckAsync"/> call: the verdict and the numbers behind it,
/// so a caller can render <c>quota_exceeded</c> details without a second read of the counter.
/// </summary>
/// <param name="Decision">The verdict.</param>
/// <param name="Used">The counter after the call, or null when there is no counter row or the gate is switched off.</param>
/// <param name="Limit">The scope's limit, or null when there is no counter row.</param>
/// <param name="RetryAfter">
/// On <see cref="QuotaDecision.Block"/> only: the time until the next period starts, at least one second. An
/// early retry cannot succeed — only a period rollover or an operator raise can.
/// </param>
/// <param name="RaisePath">
/// On <see cref="QuotaDecision.Block"/> only: the operator route that raises this scope's limit.
/// </param>
public sealed record QuotaCheckResult(
    QuotaDecision Decision,
    decimal? Used = null,
    decimal? Limit = null,
    TimeSpan? RetryAfter = null,
    string? RaisePath = null);
