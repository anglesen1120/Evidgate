namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// The quota verdict of one <see cref="IQuotaGate.CheckAsync"/> call (12.3.1 §5, ruling T53):
/// <see cref="Ok"/> below the warning line, <see cref="Warn"/> from 80 % of the limit and <see cref="Block"/> at
/// 100 %, which the caller maps to 429 <c>quota_exceeded</c> with <c>Retry-After</c>.
/// <para>
/// <see cref="Ok"/> is an answer the gate gave after running, including the case where the org has no counter row yet;
/// it is never the default for a gate that was not registered (see <see cref="IQuotaGate"/>).
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
/// The amount this call would add to <c>quota_counter.used</c>. It is never negative: a hot path asks about work it is
/// about to commit, and a correction is an adjustment of the ledger, not a negative reservation here.
/// </param>
public sealed record QuotaCheckRequest(
    UsageMetric Metric,
    string Period,
    Guid? ProjectId,
    decimal Quantity);
