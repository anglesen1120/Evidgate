using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The answer of the gate's reservation statement: what the counter holds after the call and whether the
/// call was the one that reserved it.
/// </summary>
/// <param name="Used">The quantity the counter holds.</param>
/// <param name="QuotaLimit">The scope's limit.</param>
/// <param name="WarnRatio">The fraction of the limit at which the scope warns.</param>
/// <param name="Reserved">True when this call incremented the counter; false when the scope has no room.</param>
internal sealed record QuotaCounterRow(decimal Used, decimal QuotaLimit, decimal WarnRatio, bool Reserved)
{
    /// <summary>True once the reservation reached the scope's warning line.</summary>
    internal bool IsAtOrAboveWarnLine => Used >= QuotaLimit * WarnRatio;
}

/// <summary>
/// The answer of the gate's second statement: the counter the call left behind, and whether that call was
/// refused.
/// </summary>
/// <param name="Used">The quantity the counter holds.</param>
/// <param name="QuotaLimit">The scope's limit.</param>
/// <param name="WarnRatio">The fraction of the limit at which the scope warns.</param>
/// <param name="Blocked">
/// True when the call was refused and the counter still holds what it held; false when the second attempt
/// found room (a raise committed first) and reserved the call after all.
/// </param>
internal sealed record QuotaRefusalRow(decimal Used, decimal QuotaLimit, decimal WarnRatio, bool Blocked)
{
    /// <summary>True once the counter reached the scope's warning line.</summary>
    internal bool IsAtOrAboveWarnLine => Used >= QuotaLimit * WarnRatio;
}

/// <summary>The counter a raise created or moved: its id and the numbers it now holds.</summary>
/// <param name="Id">The counter row's id.</param>
/// <param name="Used">The usage the period already holds.</param>
/// <param name="QuotaLimit">The new limit.</param>
internal sealed record QuotaRaiseRow(Guid Id, decimal Used, decimal QuotaLimit);

/// <summary>
/// The <c>quota_counter</c> write path over the caller's tenant scope: the reservation, the refusal stamp
/// and the raise, so the counter and the work it guards commit or roll back together.
/// </summary>
internal sealed class QuotaCounterStore(ITenantDb db)
{
    /// <summary>
    /// Reserves <paramref name="quantity"/> on the scope's counter in one statement. Null means the org
    /// holds no counter for the scope (no configured limit); <see cref="QuotaCounterRow.Reserved"/> false
    /// means a counter exists but the call did not fit.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="projectId">The project, or null for a tenant-level counter.</param>
    /// <param name="quantity">The amount to add; the caller has already refused a non-positive one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<QuotaCounterRow?> ReserveAsync(
        Guid orgId,
        string period,
        string metric,
        Guid? projectId,
        decimal quantity,
        CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<QuotaCounterRow>(
            UsageQuotaGateSql.Reserve,
            new { org_id = orgId, project_id = projectId, period, metric, quantity },
            cancellationToken);

    /// <summary>
    /// Settles a call the reservation did not grant, in one statement: it reserves the call when the counter
    /// has room by then, stamps the first refusal when it has none, and answers the counter either way. Null
    /// means the org holds no counter row for the scope any more, so the call is not refused.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="projectId">The project, or null for a tenant-level counter.</param>
    /// <param name="quantity">The quantity the refused call asked for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<QuotaRefusalRow?> StampRefusalAsync(
        Guid orgId,
        string period,
        string metric,
        Guid? projectId,
        decimal quantity,
        CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<QuotaRefusalRow>(
            UsageQuotaGateSql.StampRefusal,
            new { org_id = orgId, project_id = projectId, period, metric, quantity },
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
