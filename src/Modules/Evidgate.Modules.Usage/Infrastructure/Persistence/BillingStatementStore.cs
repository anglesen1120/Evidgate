using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>One issued billing statement as stored: the totals, the document address and the worksheet.</summary>
/// <param name="Id">The statement row's id.</param>
/// <param name="Period">The closed period the statement bills, <c>YYYY-MM</c>.</param>
/// <param name="Kind">The statement kind; an invoice is the only one that exists.</param>
/// <param name="TotalAmount">The server's recomputed total, frozen at issue time.</param>
/// <param name="Currency">The currency that total is expressed in.</param>
/// <param name="LineCount">How many lines the worksheet carried when it was issued.</param>
/// <param name="ContentHash">The canonical document hash of the worksheet as issued.</param>
/// <param name="WorksheetJson">The worksheet document itself, as it was read at issue time.</param>
/// <param name="IssuedBy">The platform operator who issued the statement.</param>
/// <param name="IssuedAt">The instant the statement was issued.</param>
internal sealed record BillingStatementRow(
    Guid Id,
    string Period,
    string Kind,
    decimal TotalAmount,
    string Currency,
    int LineCount,
    string ContentHash,
    string WorksheetJson,
    Guid IssuedBy,
    DateTimeOffset IssuedAt);

/// <summary>
/// The issued statements over the caller's tenant scope: the lookup that answers a re-issue, and the
/// insert whose nothing-on-conflict result is what makes one statement per period exist under a race.
/// </summary>
/// <remarks>
/// The document is stored once and never rewritten. <c>worksheet_json</c> is jsonb, so the database
/// normalises its key order and whitespace; the canonical document address is
/// <see cref="BillingStatementRow.ContentHash"/>, never the column's text.
/// </remarks>
internal sealed class BillingStatementStore(ITenantDb db)
{
    /// <summary>The one statement kind an issue writes.</summary>
    internal const string InvoiceKind = "invoice";

    /// <summary>
    /// The columns an answer carries. The worksheet is projected as text so the row type never asks
    /// Dapper to map jsonb itself, and the insert casts the parameter the other way.
    /// </summary>
    private const string SelectColumns =
        "id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json::text as worksheet_json, "
        + "issued_by, issued_at";

    private const string FindSql =
        $"""
        select {SelectColumns}
          from public.billing_statement
         where org_id = @org_id
           and period = @period
           and kind = @kind
        """;

    private const string InsertSql =
        $"""
        insert into public.billing_statement
            (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
             issued_by, issued_at)
        values
            (@id, @org_id, @period, @kind, @total_amount, @currency, @line_count, @content_hash,
             @worksheet_json::jsonb, @issued_by, @issued_at)
        on conflict (org_id, period, kind) do nothing
        returning {SelectColumns}
        """;

    /// <summary>Reads the statement one period already has, or null when it has none.</summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The billed period, <c>YYYY-MM</c>.</param>
    /// <param name="kind">The statement kind.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<BillingStatementRow?> FindAsync(
        Guid orgId,
        string period,
        string kind,
        CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<BillingStatementRow>(
            FindSql,
            new { org_id = orgId, period, kind },
            cancellationToken);

    /// <summary>
    /// Writes <paramref name="row"/> unless the period already has a statement of its kind. Null means
    /// another row won, which the caller answers by comparing the winner's document with its recompute;
    /// one row per period is the table's unique key, so two concurrent issues cannot both insert.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="row">The statement to write; its id and every frozen value come from the issuer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<BillingStatementRow?> TryInsertAsync(
        Guid orgId,
        BillingStatementRow row,
        CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<BillingStatementRow>(
            InsertSql,
            new
            {
                id = row.Id,
                org_id = orgId,
                period = row.Period,
                kind = row.Kind,
                total_amount = row.TotalAmount,
                currency = row.Currency,
                line_count = row.LineCount,
                content_hash = row.ContentHash,
                worksheet_json = row.WorksheetJson,
                issued_by = row.IssuedBy,
                issued_at = row.IssuedAt,
            },
            cancellationToken);
}
