using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Billing;

/// <summary>
/// The statement one issue left behind: the row that was written, or the row an earlier issue wrote when this
/// request replays it.
/// </summary>
/// <param name="Id">The statement's id.</param>
/// <param name="Period">The period it invoices, <c>YYYY-MM</c>.</param>
/// <param name="TotalAmount">The total it froze.</param>
/// <param name="Currency">The currency it froze.</param>
/// <param name="LineCount">How many usage rows its worksheet held.</param>
/// <param name="ContentHash">The worksheet's canonical hash as issued.</param>
/// <param name="IssuedAt">When it was issued.</param>
/// <param name="Replayed">True when this answer is the stored statement of an earlier issue, not a new one.</param>
internal sealed record IssuedStatement(
    Guid Id,
    string Period,
    decimal TotalAmount,
    string Currency,
    int LineCount,
    string ContentHash,
    DateTimeOffset IssuedAt,
    bool Replayed);

/// <summary>
/// <c>POST /internal/operator/billing/orgs/{o}/statements</c>: freezes one closed period's worksheet as the
/// invoice the org is billed on. The route names the org and the operator principal names the actor, so the
/// service opens the org's own scope, recomputes everything the statement claims and writes the row and its audit
/// entry as one transaction.
/// <para>
/// The request's total is never trusted: the period is re-read, the total is taken from the ledger's own
/// aggregate, and the submitted number must equal it. A statement is a document — its worksheet, total, currency
/// and hash are stored verbatim — so a later price or ledger change to the period cannot move what was issued.
/// </para>
/// </summary>
/// <remarks>
/// The refusals come in one fixed order: the period's shape, the org's existence, whether the period has ended,
/// then the worksheet's own rules. An unknown org is the same 404 every operator route answers, and no refusal
/// writes a row or an audit entry.
/// <para>
/// Re-issuing the same period is idempotent only while the recompute still hashes to what was issued: the caller
/// gets the stored statement with <see cref="IssuedStatement.Replayed"/> set and no second row. A late correction
/// changes the recompute, so the re-issue is a conflict — the stored document stays exactly as issued rather than
/// silently billing a different number under the same id.
/// </para>
/// </remarks>
internal sealed class StatementIssuer(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IAuditTrail audit,
    ITenantLimits limits,
    InvoiceWorksheetReader worksheets,
    BillingStatementStore statements,
    TimeProvider time)
{
    /// <summary>The audited action of one issued statement.</summary>
    internal const string Action = "usage.statement_issued";

    /// <summary>The object type the audit row names.</summary>
    internal const string ObjectType = "billing_statement";

    /// <summary>The conflict code of a re-issue whose recompute differs from the issued document.</summary>
    internal const string ExistsCode = "statement_exists";

    /// <summary>
    /// Validates the request, recomputes the period inside the org's scope, applies the issue rules and writes
    /// the statement with its audit entry in the same transaction.
    /// </summary>
    /// <param name="orgId">The org the route names.</param>
    /// <param name="operatorId">The authenticated operator, the audit row's actor.</param>
    /// <param name="request">The body.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result<IssuedStatement>> ExecuteAsync(
        Guid orgId,
        Guid operatorId,
        IssueStatementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Usage,
            nameof(StatementIssuer));

        var validated = StatementIssueRules.ValidatePeriod(request.Period);
        if (validated.Error is { } invalid)
        {
            return Result.Failure<IssuedStatement>(invalid);
        }

        using var unit = audit.BeginUnit();
        await using var scope = await scopes.BeginAsync(orgId, null, false, cancellationToken).ConfigureAwait(false);

        if (await limits.ForOrganizationAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<IssuedStatement>(Error.NotFound("no such organization"));
        }

        var now = time.GetUtcNow();
        if (!StatementIssueRules.IsClosed(validated.Value, now))
        {
            return Result.Failure<IssuedStatement>(StatementIssueRules.PeriodOpen());
        }

        var worksheet = await worksheets
            .BuildAsync(orgId, validated.Value, cancellationToken)
            .ConfigureAwait(false);
        var draft = StatementIssueRules.Evaluate(worksheet, request);
        if (draft.Error is { } refused)
        {
            return Result.Failure<IssuedStatement>(refused);
        }

        var write = await PersistAsync(orgId, operatorId, draft.Value, now, cancellationToken).ConfigureAwait(false);
        if (write.Error is { } conflict)
        {
            return Result.Failure<IssuedStatement>(conflict);
        }

        if (write.Value.Inserted)
        {
            DescribeAudit(operatorId, draft.Value, write.Value.Statement.Id);
            await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(write.Value.Statement);
    }

    /// <summary>
    /// Writes the statement, or answers with the one already stored. The insert is the decision point: its unique
    /// <c>(org_id, period, kind)</c> key means exactly one of two concurrent issues inserts, and the loser — which
    /// waited on the winner's transaction — reads the winner's committed row on its next statement, because the
    /// scope runs READ COMMITTED and takes a fresh snapshot per statement.
    /// </summary>
    private async Task<Result<StatementWrite>> PersistAsync(
        Guid orgId,
        Guid operatorId,
        StatementDraft draft,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = new BillingStatementRow(
            Guid.CreateVersion7(),
            draft.Period,
            BillingStatementStore.InvoiceKind,
            draft.TotalAmount,
            draft.Currency,
            draft.LineCount,
            draft.ContentHash,
            draft.WorksheetJson,
            operatorId,
            now);

        if (await statements.TryInsertAsync(orgId, row, cancellationToken).ConfigureAwait(false) is { } inserted)
        {
            return Result.Success(new StatementWrite(ToIssued(inserted, replayed: false), Inserted: true));
        }

        var stored = await statements
            .FindAsync(orgId, draft.Period, BillingStatementStore.InvoiceKind, cancellationToken)
            .ConfigureAwait(false);

        // A row this scope cannot read is still a row: refusing is the only answer that cannot bill twice.
        if (stored is null || !string.Equals(stored.ContentHash, draft.ContentHash, StringComparison.Ordinal))
        {
            return Result.Failure<StatementWrite>(Error.Conflict(ExistsCode,
                "the period was already issued with different content, and the stored statement was left untouched"));
        }

        return Result.Success(new StatementWrite(ToIssued(stored, replayed: true), Inserted: false));
    }

    private static IssuedStatement ToIssued(BillingStatementRow row, bool replayed) => new(
        row.Id,
        row.Period,
        row.TotalAmount,
        row.Currency,
        row.LineCount,
        row.ContentHash,
        row.IssuedAt,
        replayed);

    /// <summary>
    /// Records the period and how many rows the document froze, with the statement's own id as the object. No
    /// amount reaches the audit chain: the statement row holds what was billed, the audit row says it was issued.
    /// </summary>
    private void DescribeAudit(Guid operatorId, StatementDraft draft, Guid statementId)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["period"] = draft.Period,
            ["count"] = draft.LineCount.ToString(CultureInfo.InvariantCulture),
        };

        audit.Describe(new AuditObject(ObjectType, statementId.ToString("D"), Metadata: metadata));
        audit.Record(Action, new AuditActor(AuditActorTypes.PlatformOperator, operatorId.ToString("D")));
    }

    /// <summary>What <see cref="PersistAsync"/> settled: the statement to answer with, and whether this call wrote it.</summary>
    private sealed record StatementWrite(IssuedStatement Statement, bool Inserted);
}
