using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Application.Commands;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>One validated raise: the scope, the new limit and the reason the audit row records.</summary>
/// <param name="ProjectId">The project whose counter moves, or null for the tenant-level counter.</param>
/// <param name="Period">The billing month, <c>YYYY-MM</c>.</param>
/// <param name="Metric">The wire metric name.</param>
/// <param name="NewLimit">The new limit; strictly above the stored one.</param>
/// <param name="Reason">Why the limit was raised.</param>
internal sealed record QuotaLimitChange(
    Guid? ProjectId,
    string Period,
    string Metric,
    decimal NewLimit,
    string Reason);

/// <summary>The counter a raise left behind: what the period used and the limit it now has.</summary>
/// <param name="CounterId">The counter row's id, the object the audit row names.</param>
/// <param name="ProjectId">The project, or null for the tenant-level counter.</param>
/// <param name="Period">The billing month.</param>
/// <param name="Metric">The wire metric name.</param>
/// <param name="Limit">The limit after the raise.</param>
/// <param name="Used">The usage the period holds; a raise above it clears the block.</param>
internal sealed record QuotaRaiseResult(
    Guid CounterId,
    Guid? ProjectId,
    string Period,
    string Metric,
    decimal Limit,
    decimal Used);

/// <summary>
/// The one write path of a quota raise: the project check, the single conditional upsert and the audit
/// description, shared by the operator route and the command handler so both surfaces record the same row.
/// </summary>
internal sealed class QuotaLimitWriter(QuotaCounterStore store, ITenantDb db)
{
    /// <summary>The audited action of a raise, also the command's <c>[AuditedAction]</c>.</summary>
    internal const string Action = "usage.quota_raise";

    /// <summary>The object type the audit row names.</summary>
    internal const string ObjectType = "quota_counter";

    /// <summary>
    /// Raises the scope's limit on the caller's tenant transaction. A limit at or below the stored one is
    /// refused: a raise is the only limit change this path makes, so it can never move a limit below the
    /// usage the period already holds.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value of the request).</param>
    /// <param name="change">The validated raise.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result<QuotaRaiseResult>> ApplyAsync(
        Guid orgId,
        QuotaLimitChange change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (change.ProjectId is { } projectId)
        {
            var visible = await UsageProjectVisibility.VisibleAsync(
                    db.Connection,
                    db.Transaction,
                    orgId,
                    [projectId],
                    cancellationToken)
                .ConfigureAwait(false);

            if (!visible.Contains(projectId))
            {
                // Unknown and another tenant's project answer one 404, so the route is never an oracle for which
                // ids exist somewhere else, and a raise can never be aimed at a counter this tenant cannot see.
                return Result.Failure<QuotaRaiseResult>(Error.NotFound("no such project"));
            }
        }

        var raised = await store
            .RaiseAsync(orgId, change.Period, change.Metric, change.ProjectId, change.NewLimit, cancellationToken)
            .ConfigureAwait(false);

        return raised is null
            ? Result.Failure<QuotaRaiseResult>(Error.Invalid(
                "the new limit must exceed the stored limit",
                [new ErrorDetail("new_limit", "limit_not_increased")]))
            : Result.Success(new QuotaRaiseResult(
                raised.Id,
                change.ProjectId,
                change.Period,
                change.Metric,
                raised.QuotaLimit,
                raised.Used));
    }

    /// <summary>
    /// Describes the raised counter for the next audited entry: the scope, the new limit and the operator's
    /// reason. No amount billed, no usage detail and never a secret reaches the chain from here.
    /// </summary>
    /// <param name="audit">The trail of the unit of work.</param>
    /// <param name="change">The applied raise.</param>
    /// <param name="result">The counter the statement answered.</param>
    internal static void Describe(IAuditTrail audit, QuotaLimitChange change, QuotaRaiseResult result)
    {
        ArgumentNullException.ThrowIfNull(audit);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["period"] = change.Period,
            ["metric"] = change.Metric,
            ["limit"] = result.Limit.ToString(CultureInfo.InvariantCulture),
            ["reason"] = change.Reason,
            ["outcome"] = "raised",
        };

        audit.Describe(new AuditObject(ObjectType, result.CounterId.ToString("D"), Metadata: metadata));
    }
}
