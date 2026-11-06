using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// One <c>quota_counter</c> row as the gate answers it: what the counter holds and the warning line the
/// verdict is computed from.
/// </summary>
/// <param name="Used">The quantity the counter holds.</param>
/// <param name="QuotaLimit">The scope's limit.</param>
/// <param name="WarnRatio">The fraction of the limit at which the scope warns.</param>
internal sealed record QuotaCounterRow(decimal Used, decimal QuotaLimit, decimal WarnRatio)
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
/// The <c>quota_counter</c> write path over the caller's tenant scope: the conditional increment, the
/// refusal stamp and the raise, each one statement so the counter and the work it guards commit or roll
/// back together.
/// </summary>
internal sealed class QuotaCounterStore(ITenantDb db)
{
    /// <summary>
    /// Reserves <paramref name="quantity"/> on the scope's counter, or answers null when the scope holds no
    /// counter or the increment would pass the limit.
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
            UsageQuotaGateSql.Increment,
            new { org_id = orgId, project_id = projectId, period, metric, quantity },
            cancellationToken);

    /// <summary>
    /// Stamps the refusal of a call that found no room and answers the counter it stamped, or null when the
    /// org holds no counter for the scope (which is not a refusal: a missing counter is no configured
    /// limit). The numbers come from one indexed read of the same row, in the same transaction.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="projectId">The project, or null for a tenant-level counter.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<QuotaCounterRow?> MarkBlockedAsync(
        Guid orgId,
        string period,
        string metric,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        var scope = new { org_id = orgId, project_id = projectId, period, metric };
        var stamped = await db.ExecuteAsync(UsageQuotaGateSql.Block, scope, cancellationToken)
            .ConfigureAwait(false);

        if (stamped == 0)
        {
            return null;
        }

        return await db.QuerySingleOrDefaultAsync<QuotaCounterRow>(
                UsageQuotaGateSql.Read, scope, cancellationToken)
            .ConfigureAwait(false);
    }

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

    /// <summary>Reads the scope's counter, or null when the org holds none.</summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billing month, <c>YYYY-MM</c>.</param>
    /// <param name="metric">The wire metric name.</param>
    /// <param name="projectId">The project, or null for a tenant-level counter.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<QuotaCounterRow?> ReadAsync(
        Guid orgId,
        string period,
        string metric,
        Guid? projectId,
        CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<QuotaCounterRow>(
            UsageQuotaGateSql.Read,
            new { org_id = orgId, project_id = projectId, period, metric },
            cancellationToken);
}
