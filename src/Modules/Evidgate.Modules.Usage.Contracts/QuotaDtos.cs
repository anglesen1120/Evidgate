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
/// Which counter a verdict came from: a project's counter, or the organization-wide one
/// (<c>quota_counter.project_id</c> null) that caps every project of the tenant together.
/// </summary>
public enum QuotaScope
{
    /// <summary>The counter of one project.</summary>
    Project,

    /// <summary>The counter of the whole organization; a project_id-less call's only scope.</summary>
    Tenant,
}

/// <summary>
/// One quota question a hot path asks before it commits metered work (ruling T53): the metric, the period, the
/// project (null for a tenant-level limit) and the quantity the call is about to commit.
/// </summary>
/// <param name="Metric">The metric being metered.</param>
/// <param name="Period">
/// The billing period, exactly <c>YYYY-MM</c>, or null for the month of the caller's open database transaction, which
/// the gate reads inside its own first statement: the instant the batch's own rows and the ledger row metering them
/// are stamped with, so a batch is gated and metered in one month even when it straddles a month end. Callers pass
/// null; an answer that names a counter carries the month it used (<see cref="QuotaCheckResult.Period"/>).
/// </param>
/// <param name="ProjectId">The project the usage belongs to, or null for a tenant-level limit.</param>
/// <param name="Quantity">
/// The amount this call would add to <c>quota_counter.used</c>, strictly positive: a hot path asks about work it is
/// about to commit, and a correction is an adjustment of the ledger, not a negative reservation here. The gate refuses
/// a non-positive quantity loudly, before it writes.
/// </param>
/// <param name="IncludeTenantScope">
/// True charges the organization-wide counter as well as the project's, so an org-level cap applies to work a single
/// project commits: the call is admitted only when both scopes have room, and both are charged or neither is. False,
/// the default, asks about the project scope alone (and about the tenant scope alone when
/// <paramref name="ProjectId"/> is null), which is what every caller that names one scope wants.
/// </param>
public sealed record QuotaCheckRequest(
    UsageMetric Metric,
    string? Period,
    Guid? ProjectId,
    decimal Quantity,
    bool IncludeTenantScope = false);

/// <summary>
/// The answer of one <see cref="IQuotaGate.CheckAsync"/> call: the verdict and the numbers behind it,
/// so a caller can render <c>quota_exceeded</c> details without a second read of the counter.
/// </summary>
/// <param name="Decision">The verdict.</param>
/// <param name="Used">
/// The counter after the call, or null when there is no counter row or the gate is switched off. With more than one
/// scope charged, it is the scope with the least room left: the one whose numbers decided the verdict.
/// </param>
/// <param name="Limit">That scope's limit, or null when there is no counter row.</param>
/// <param name="RetryAfter">
/// On <see cref="QuotaDecision.Block"/> only: the time until the next period starts, at least one second. Measured from
/// the gate transaction's own clock, the clock that named the period, never from the host's. An early retry cannot
/// succeed — only a period rollover or an operator raise can.
/// </param>
/// <param name="RaisePath">
/// On <see cref="QuotaDecision.Block"/> only: the operator route that raises this scope's limit.
/// </param>
/// <param name="RefusedScope">
/// On <see cref="QuotaDecision.Block"/> only: the scope that refused, in lock order (the tenant counter, then the
/// project's) when both refuse. Null on every other verdict.
/// </param>
/// <param name="Period">
/// The billing month the verdict was decided in, <c>YYYY-MM</c>; null when no counter answered (no configured limit, or
/// the gate switched off). A caller that must write the refusal stamp after its own rollback builds the stamp's request
/// from it, so the stamp names the month that refused and not a later one.
/// </param>
public sealed record QuotaCheckResult(
    QuotaDecision Decision,
    decimal? Used = null,
    decimal? Limit = null,
    TimeSpan? RetryAfter = null,
    string? RaisePath = null,
    QuotaScope? RefusedScope = null,
    string? Period = null);

/// <summary>
/// The answer of one <see cref="IQuotaGate.PeekAsync"/> call. It is a different type from
/// <see cref="QuotaCheckResult"/> on purpose: an advisory read that refused nothing and admitted nobody can never be
/// passed where the authoritative verdict is expected, and there is no Ok or Warn to mistake for an admission.
/// </summary>
/// <param name="Blocked">
/// True when a counter of the period exists and has no room for the quantity right now. False means only "the
/// authoritative check still decides": there is no counter, or every charged scope has room.
/// </param>
/// <param name="Used">When blocked, the refusing counter's usage; otherwise null.</param>
/// <param name="Limit">When blocked, the refusing counter's limit; otherwise null.</param>
/// <param name="RetryAfter">When blocked, the time until the next period starts, at least one second; otherwise null.</param>
/// <param name="RaisePath">When blocked, the operator route that raises the refusing scope's limit; otherwise null.</param>
/// <param name="RefusedScope">When blocked, the refusing scope, in lock order; otherwise null.</param>
/// <param name="Period">When blocked, the month that refused, <c>YYYY-MM</c>; otherwise null.</param>
/// <param name="Unstamped">
/// When blocked: true when the refusing counter carries no <c>blocked_at</c> yet, so a caller that must write the stamp
/// after its own rollback knows the write would change a row. False for a counter that already carries the stamp — a
/// repeated refusal of the same month — and on a clear answer, where no counter refused at all.
/// </param>
public sealed record QuotaPeekResult(
    bool Blocked,
    decimal? Used = null,
    decimal? Limit = null,
    TimeSpan? RetryAfter = null,
    string? RaisePath = null,
    QuotaScope? RefusedScope = null,
    string? Period = null,
    bool Unstamped = false)
{
    /// <summary>The answer when nothing refuses: the authoritative check still decides.</summary>
    public static QuotaPeekResult Clear { get; } = new(false);
}
