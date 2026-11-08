using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Usage.Application.Billing;

/// <summary>
/// What one issue records about itself: the statement it froze, the stored copy it answered a re-issue with, or
/// the refusal it returned. One attempt leaves exactly one row, and a refusal's row outlives the statement that
/// must not.
/// </summary>
/// <remarks>
/// A refusal names the org as its object because no statement exists to name, and records only the reason code and
/// the period that passed the shape rule — never the worksheet, the submitted total or an amount. A row that cannot
/// be recorded propagates instead of being swallowed: an unaudited attempt must fail rather than answer.
/// </remarks>
internal sealed partial class StatementIssuer
{
    /// <summary>The audited action of one refused issue.</summary>
    internal const string RefusedAction = "usage.statement_refused";

    /// <summary>The audited action of one replayed issue.</summary>
    internal const string ReplayedAction = "usage.statement_replayed";

    private const string RefusedOutcome = "refused";

    private const string ReplayedOutcome = "replayed";

    /// <summary>
    /// Records the refusal and commits the scope holding only its audit row: nothing else was written under it, so
    /// a refusal leaves no statement, worksheet or ledger row behind.
    /// </summary>
    private async Task<Result<IssuedStatement>> RefuseAsync(
        Guid orgId,
        Guid operatorId,
        string? period,
        Error refusal,
        ITenantScope scope,
        CancellationToken cancellationToken)
    {
        DescribeRefusal(operatorId, orgId, period, Reason(refusal));
        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Failure<IssuedStatement>(refusal);
    }

    /// <summary>
    /// Rolls the refused statement back and then records the refusal in a scope of its own, so the audit row
    /// commits where the document must not. The roll back happens first: the audit row cannot share a transaction
    /// that is about to be discarded.
    /// </summary>
    private async Task<Result<IssuedStatement>> RefuseAfterRollbackAsync(
        Guid orgId,
        Guid operatorId,
        string period,
        Error refusal,
        ITenantScope rolledBack,
        CancellationToken cancellationToken)
    {
        // Disposal rolls an uncommitted scope back and releases its transaction-scoped locks, so the audit scope
        // below starts from a connection that holds nothing of this attempt.
        await rolledBack.DisposeAsync().ConfigureAwait(false);

        await using var auditScope = await scopes
            .BeginAsync(orgId, null, false, cancellationToken)
            .ConfigureAwait(false);

        return await RefuseAsync(orgId, operatorId, period, refusal, auditScope, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records what the issue left behind: an issued statement with its period and frozen row count, or the stored
    /// copy a replay answered with. No amount reaches the audit chain in either case.
    /// </summary>
    private void DescribeIssue(Guid operatorId, StatementDraft draft, StatementWrite write)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["period"] = draft.Period };

        if (write.Inserted)
        {
            metadata["count"] = draft.LineCount.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            metadata["outcome"] = ReplayedOutcome;
        }

        audit.Describe(new AuditObject(ObjectType, write.Statement.Id.ToString("D"), Metadata: metadata));
        audit.Record(
            write.Inserted ? Action : ReplayedAction,
            new AuditActor(AuditActorTypes.PlatformOperator, operatorId.ToString("D")));
    }

    private void DescribeRefusal(Guid operatorId, Guid orgId, string? period, string reason)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["outcome"] = RefusedOutcome,
        };

        if (period is not null)
        {
            metadata["period"] = period;
        }

        // The org is the object: the period was refused, so no statement row exists to name, and the audit row
        // still has to say which tenant's attempt it was.
        audit.Describe(new AuditObject(ObjectType, orgId.ToString("D"), Metadata: metadata));
        audit.Record(RefusedAction, new AuditActor(AuditActorTypes.PlatformOperator, operatorId.ToString("D")));
    }

    /// <summary>
    /// The code a caller branches on: the worksheet's own reason (which member was wrong) when the error names one,
    /// otherwise the error's code, as the conflicts carry.
    /// </summary>
    private static string Reason(Error error) =>
        error.Details is { Count: > 0 } details ? details[0].Reason : error.Code;
}
