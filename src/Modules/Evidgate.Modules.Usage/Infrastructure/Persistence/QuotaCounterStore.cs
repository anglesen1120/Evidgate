using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// What one charged counter says about itself after the call: the scope it belongs to, what it holds and what it is
/// allowed to hold. Both statements answer counters, and the gate picks among them by the room they have left.
/// </summary>
internal interface IQuotaScopeNumbers
{
    /// <summary>The counter's scope: its project id, or <see cref="Guid.Empty"/> for the organization-wide counter.</summary>
    Guid ScopeKey { get; }

    /// <summary>The quantity the counter holds.</summary>
    decimal Used { get; }

    /// <summary>The scope's limit.</summary>
    decimal QuotaLimit { get; }

    /// <summary>What the scope has left below its limit; a refusal charges nothing, so it is never negative.</summary>
    decimal Headroom { get; }
}

/// <summary>
/// The answer of the gate's reservation statement for one scope: what the counter holds after the call, whether the
/// call was the one that charged it, and whether this call is the one that stamped the warning line.
/// </summary>
/// <param name="Id">The counter's id, the aggregate a warning task names.</param>
/// <param name="ScopeKey">
/// The counter's scope: its project id, or <see cref="Guid.Empty"/> for the organization-wide counter.
/// </param>
/// <param name="Used">The quantity the counter holds.</param>
/// <param name="QuotaLimit">The scope's limit.</param>
/// <param name="WarnRatio">The fraction of the limit at which the scope warns.</param>
/// <param name="Reserved">True when this call charged the counter; false when the scope has no room.</param>
/// <param name="NeedsCarry">
/// True when the month has no counter of its own but an earlier month did: the limit must be carried into
/// this period before the call can be answered, and no usage has been counted yet.
/// </param>
/// <param name="WarnedNow">True only for the statement that moved <c>warned_at</c> from null to a stamp.</param>
internal sealed record QuotaCounterRow(
    Guid Id,
    Guid ScopeKey,
    decimal Used,
    decimal QuotaLimit,
    decimal WarnRatio,
    bool Reserved,
    bool NeedsCarry,
    bool WarnedNow) : IQuotaScopeNumbers
{
    /// <summary>True when the scope's warning line is at or below what it now holds.</summary>
    internal bool IsAtOrAboveWarnLine => Used >= QuotaLimit * WarnRatio;

    /// <inheritdoc />
    public decimal Headroom => QuotaLimit - Used;
}

/// <summary>
/// The answer of the gate's second statement for one scope: the counter the call left behind, whether that call was
/// refused, and whether it is the one that stamped the warning line.
/// </summary>
/// <param name="Id">The counter's id, the aggregate a warning task names.</param>
/// <param name="ScopeKey">
/// The counter's scope: its project id, or <see cref="Guid.Empty"/> for the organization-wide counter.
/// </param>
/// <param name="Used">The quantity the counter holds.</param>
/// <param name="QuotaLimit">The scope's limit.</param>
/// <param name="WarnRatio">The fraction of the limit at which the scope warns.</param>
/// <param name="Blocked">
/// True when this scope refused the call and still holds what it held; false when the second attempt found room (a
/// raise committed first) and charged it after all, or when it had room all along.
/// </param>
/// <param name="WarnedNow">True only for the statement that moved <c>warned_at</c> from null to a stamp.</param>
/// <param name="DatabaseNow">The transaction's clock, for the retry calculation: the clock that named the period.</param>
internal sealed record QuotaRefusalRow(
    Guid Id,
    Guid ScopeKey,
    decimal Used,
    decimal QuotaLimit,
    decimal WarnRatio,
    bool Blocked,
    bool WarnedNow,
    DateTimeOffset DatabaseNow) : IQuotaScopeNumbers
{
    /// <summary>True when the scope's warning line is at or below what it now holds.</summary>
    internal bool IsAtOrAboveWarnLine => Used >= QuotaLimit * WarnRatio;

    /// <inheritdoc />
    public decimal Headroom => QuotaLimit - Used;
}

/// <summary>The counter a raise created or moved: its id and the numbers it now holds.</summary>
/// <param name="Id">The counter row's id.</param>
/// <param name="Used">The usage the period already holds.</param>
/// <param name="QuotaLimit">The new limit.</param>
internal sealed record QuotaRaiseRow(Guid Id, decimal Used, decimal QuotaLimit);

/// <summary>An existing refusing counter and the database instant at which it was read.</summary>
/// <param name="ScopeKey">The project id, or the tenant sentinel.</param>
/// <param name="Used">The stored usage.</param>
/// <param name="QuotaLimit">The stored limit.</param>
/// <param name="DatabaseNow">The transaction's clock for the retry calculation.</param>
internal sealed record QuotaPeekRow(Guid ScopeKey, decimal Used, decimal QuotaLimit, DateTimeOffset DatabaseNow);

/// <summary>
/// The <c>quota_counter</c> path over the caller's tenant scope: the reservation, the carry-forward,
/// the refusal settle and stamp, and the raise, plus the read-only peek and the transaction month the caller gates with,
/// so the counter and the work it guards commit or roll back together.
/// </summary>
internal sealed class QuotaCounterStore(ITenantDb db)
{
    /// <summary>Reads the billing month of the caller's transaction even when quota enforcement is off.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The transaction's month, <c>YYYY-MM</c> in UTC.</returns>
    internal async Task<string> CurrentPeriodAsync(CancellationToken cancellationToken)
    {
        var period = await db
            .ScalarAsync<string>(UsageQuotaGateSql.CurrentPeriod, cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("the transaction did not return a billing month");

        return period;
    }

    /// <summary>
    /// Reads the first counter of the period that has no room for <paramref name="quantity"/>, in lock order, and the
    /// database instant beside it. Null means no charged scope refuses; nothing is written, locked or carried.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="scopeKeys">The scopes the call charges, the tenant's first.</param>
    /// <param name="quantity">The quantity the call would add.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The refusing counter, or null when every charged scope has room or holds no counter.</returns>
    internal Task<QuotaPeekRow?> PeekAsync(
        Guid orgId,
        string period,
        string metric,
        Guid[] scopeKeys,
        decimal quantity,
        CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<QuotaPeekRow>(
            UsageQuotaGateSql.Peek,
            new { org_id = orgId, scope_keys = scopeKeys, period, metric, quantity },
            cancellationToken);

    /// <summary>
    /// Reserves <paramref name="quantity"/> on every scope in <paramref name="scopeKeys"/> in one statement, all of
    /// them or none. A scope with no counter row and no earlier counter to carry a limit from reports no row (no
    /// configured limit); <see cref="QuotaCounterRow.Reserved"/> false means a counter exists but the call did not fit
    /// in it, and <see cref="QuotaCounterRow.NeedsCarry"/> means the period has no counter yet but an earlier one did.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="scopeKeys">The scopes to lock and charge, the tenant's first.</param>
    /// <param name="quantity">The amount to add; the caller has already refused a non-positive one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<IReadOnlyList<QuotaCounterRow>> ReserveAsync(
        Guid orgId,
        string period,
        string metric,
        Guid[] scopeKeys,
        decimal quantity,
        CancellationToken cancellationToken) =>
        await db.QueryAsync<QuotaCounterRow>(
                UsageQuotaGateSql.Reserve,
                new { org_id = orgId, scope_keys = scopeKeys, period, metric, quantity },
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Settles a call the reservation did not grant, in one statement: it charges every scope the call asks for when
    /// they all have room by then, stamps the first refusal on the scopes that have none, and answers each scope either
    /// way. An empty answer means the tenant holds no counter row for any of the scopes any more, so the call is not
    /// refused.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="scopeKeys">The scopes to lock and settle, the tenant's first.</param>
    /// <param name="quantity">The quantity the refused call asked for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<IReadOnlyList<QuotaRefusalRow>> SettleAsync(
        Guid orgId,
        string period,
        string metric,
        Guid[] scopeKeys,
        decimal quantity,
        CancellationToken cancellationToken) =>
        await db.QueryAsync<QuotaRefusalRow>(
                UsageQuotaGateSql.Settle,
                new { org_id = orgId, scope_keys = scopeKeys, period, metric, quantity },
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Stamps <c>blocked_at</c> on the scopes of a refusal whose caller rolled its transaction back, in the caller's
    /// own transaction (the stamp service opens one). Counters with room again, counters already stamped and scopes
    /// without a counter row are not written, so a repeated refusal costs the statement and no version bump.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="scopeKeys">The scopes the refused call charged, the tenant's first.</param>
    /// <param name="quantity">The quantity the refused call asked for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<int> StampBlockedAsync(
        Guid orgId,
        string period,
        string metric,
        Guid[] scopeKeys,
        decimal quantity,
        CancellationToken cancellationToken) =>
        db.ExecuteAsync(
            UsageQuotaGateSql.StampBlocked,
            new { org_id = orgId, scope_keys = scopeKeys, period, metric, quantity },
            cancellationToken);

    /// <summary>
    /// Carries every scope's latest earlier month's limit and warning line into <paramref name="period"/>, with no
    /// usage and no threshold stamp, in lock order and before any counter is locked. Nothing is written for a scope
    /// that already has a counter of the period or has no earlier one; the caller re-runs the reservation either way,
    /// so one carried row per scope is what every concurrent first caller of the month reads.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="scopeKeys">The scopes the call charges, the tenant's first.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<int> CarryForwardAsync(
        Guid orgId,
        string period,
        string metric,
        Guid[] scopeKeys,
        CancellationToken cancellationToken) =>
        db.ExecuteAsync(
            UsageQuotaGateSql.CarryForward,
            new { org_id = orgId, scope_keys = scopeKeys, period, metric },
            cancellationToken);

    /// <summary>
    /// Raises the scope's limit, creating the counter when the scope has none. Null means the stored limit
    /// is already at or above <paramref name="quotaLimit"/>, which the caller answers as a refusal.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="projectId">The project, or null for a tenant-level counter.</param>
    /// <param name="quotaLimit">The new limit, already validated as strictly positive.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<QuotaRaiseRow?> RaiseAsync(
        Guid orgId,
        string period,
        string metric,
        Guid? projectId,
        decimal quotaLimit,
        CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<QuotaRaiseRow>(
            UsageQuotaGateSql.Raise,
            new { org_id = orgId, project_id = projectId, period, metric, quota_limit = quotaLimit },
            cancellationToken);
}
