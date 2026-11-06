using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Mediator;

namespace Evidgate.Modules.Evidence.Application.Reports;

/// <summary>
/// One stored report read back against the events its parse wrote (<c>GET /v1/runs/{r}/reports/{report_id}/comparison</c>):
/// the report's declared and observed counters, the discrepancy codes that explain the difference, and one
/// sequence-ordered page of the report's cases.
/// </summary>
/// <param name="RunId">The run the route names.</param>
/// <param name="ReportId">The stored report the route names.</param>
/// <param name="Cursor">Opaque cursor of the next page, or null for the first page.</param>
/// <param name="Limit">Page size, 1–200 (00 §9.1).</param>
internal sealed record GetReportComparison(Guid RunId, Guid ReportId, string? Cursor, int Limit)
    : IQuery<Result<ReportComparison>>;

/// <summary>
/// Handler of <see cref="GetReportComparison"/>: the report row and the case page over the same ambient tenant scope
/// (00 §5.5 C1/C3/C5), two statements in total whatever the page holds.
/// <para>
/// The report is resolved by its id and the route's run, joined to its artifact for the project: RLS hides another
/// tenant's row, and the project check hides another project's row of the same org, so an unknown id, a foreign id, a
/// report of another run and a project-scoped caller's mismatch all take the one 404 of
/// <see cref="EvidenceErrors.ReportNotFound"/> (00 §5.4: no existence oracle). The only 403 is the permission
/// stage's: <c>run:read</c> is outside the service-role ceiling.
/// </para>
/// <para>
/// The discrepancy list comes from <see cref="JUnitDeclaredObservedCheck.EffectiveCodes"/> alone, so a refused row's
/// derived <c>malformed</c> code and the canonical order are the parse path's, never a second reading of the row. A
/// cursor this codec did not produce is <c>422 request_invalid</c> (<c>cursor</c>, <c>malformed</c>) rather than a
/// guessed page position; the cursor is not signed and carries no report, so the query's own predicates are what bind
/// it to this report.
/// </para>
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class GetReportComparisonHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetReportComparison, Result<ReportComparison>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ReportComparison>> Handle(
        GetReportComparison query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(GetReportComparison));

        var context = tenant.Require();

        var report = await db.QuerySingleOrDefaultAsync<ReportComparisonRow>(
                ReportComparisonSql.Report,
                new { run_id = query.RunId, report_id = query.ReportId },
                cancellationToken)
            .ConfigureAwait(false);

        if (report is null
            || EvidenceProjectScope.Project(report.ProjectId, principals.Current?.ProjectScope, context.ProjectId) is not null)
        {
            return Result.Failure<ReportComparison>(EvidenceErrors.ReportNotFound);
        }

        var firstPage = query.Cursor is null;
        var cursorSequence = 0L;
        if (!firstPage && !ReportComparisonCursor.TryDecode(query.Cursor, out cursorSequence))
        {
            return Result.Failure<ReportComparison>(CursorMalformed);
        }

        var rows = await CasesAsync(report, query.Limit, firstPage ? null : cursorSequence, cancellationToken)
            .ConfigureAwait(false);
        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<ReportComparisonCaseRow> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new ReportComparison(
            report.Id,
            report.RunId,
            report.ArtifactId,
            report.ProducerKind,
            report.ParserId,
            report.ParserVersion,
            report.Reason,
            [.. EffectiveCodes(report)],
            new ReportComparisonSummary(
                report.DeclaredTests,
                report.DeclaredFailures,
                report.DeclaredErrors,
                report.DeclaredSkipped,
                report.ObservedTests,
                report.ObservedFailures,
                report.ObservedErrors,
                report.ObservedSkipped,
                report.Outcome),
            [.. page.Select(row => new ReportComparisonCase(row.ExecutionKey, row.RawStatus, row.ObservedEvents, []))],
            hasMore ? ReportComparisonCursor.Encode(page[^1].Sequence) : null));
    }

    /// <summary>
    /// One page of the report's cases, or none for a refused report: a refusal writes no event, while an older parsed
    /// row of the same artifact may have, and those belong to that release rather than to this row.
    /// </summary>
    /// <param name="report">The stored report the route named.</param>
    /// <param name="limit">The page size; one more row is read to decide the next page.</param>
    /// <param name="cursorSequence">The last sequence an earlier page returned, or null for the first page.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    private async Task<IReadOnlyList<ReportComparisonCaseRow>> CasesAsync(
        ReportComparisonRow report,
        int limit,
        long? cursorSequence,
        CancellationToken cancellationToken)
    {
        if (report.Outcome is not (RawReportOutcomes.Mapped or RawReportOutcomes.Partial))
        {
            return [];
        }

        var producerId = RawReportProducer.IdFor(report.ArtifactId);

        return cursorSequence is { } sequence
            ? await db.QueryAsync<ReportComparisonCaseRow>(
                    ReportComparisonSql.NextCasePage,
                    ReportComparisonSql.NextPageParameters(report.RunId, producerId, sequence, limit + 1),
                    cancellationToken)
                .ConfigureAwait(false)
            : await db.QueryAsync<ReportComparisonCaseRow>(
                    ReportComparisonSql.FirstCasePage,
                    ReportComparisonSql.FirstPageParameters(report.RunId, producerId, limit + 1),
                    cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// The report's effective discrepancy codes, in the canonical order: the stored code words, each once as the query
    /// selects them, handed to the shared check, which orders them independently of how often a code occurred.
    /// </summary>
    /// <param name="report">The stored report row.</param>
    private static IReadOnlyList<string> EffectiveCodes(ReportComparisonRow report) =>
        JUnitDeclaredObservedCheck.EffectiveCodes(report.Outcome, report.Reason, report.DiscrepancyCodes);

    /// <summary>422: the cursor is not a value this endpoint issued, so no page position is guessed from it.</summary>
    private static Error CursorMalformed => Error.Invalid(
        "the cursor is not a value this endpoint issued",
        [new ErrorDetail("cursor", "malformed")]);

    /// <summary>One stored report as the route reads it, with the project its artifact belongs to.</summary>
    private sealed class ReportComparisonRow
    {
        public Guid Id { get; set; }

        public Guid RunId { get; set; }

        public Guid ArtifactId { get; set; }

        public Guid ProjectId { get; set; }

        public string ProducerKind { get; set; } = string.Empty;

        public string? ParserId { get; set; }

        public string? ParserVersion { get; set; }

        public string Outcome { get; set; } = string.Empty;

        public string? Reason { get; set; }

        public int? DeclaredTests { get; set; }

        public int? DeclaredFailures { get; set; }

        public int? DeclaredErrors { get; set; }

        public int? DeclaredSkipped { get; set; }

        public int ObservedTests { get; set; }

        public int ObservedFailures { get; set; }

        public int ObservedErrors { get; set; }

        public int ObservedSkipped { get; set; }

        public string[] DiscrepancyCodes { get; set; } = [];
    }

    /// <summary>One case row of the page: its execution key, the producer's status word and its event count.</summary>
    private sealed class ReportComparisonCaseRow
    {
        public string ExecutionKey { get; set; } = string.Empty;

        public string? RawStatus { get; set; }

        public long Sequence { get; set; }

        public int ObservedEvents { get; set; }
    }
}

/// <summary>
/// The opaque cursor of the comparison page (00 §9.1): the <c>sequence</c> of the page's last case, written in plain
/// decimal. Opaque to clients — nothing is promised beyond "hand it back verbatim" — and decoded to an integer so the
/// SQL never compares client text. A value that is well formed but not this codec's canonical spelling is refused
/// exactly like a malformed one, so a cursor can only ever be one this endpoint issued.
/// </summary>
internal static class ReportComparisonCursor
{
    /// <summary>Encodes one case's keyset position.</summary>
    /// <param name="sequence">The producer's sequence of the page's last case.</param>
    internal static string Encode(long sequence) => sequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>Decodes a cursor. False for anything this codec did not produce — the caller answers 422.</summary>
    /// <param name="cursor">The client's cursor text.</param>
    /// <param name="sequence">The decoded sequence, when the cursor is one this codec wrote.</param>
    internal static bool TryDecode(string? cursor, out long sequence)
    {
        sequence = 0;

        if (string.IsNullOrEmpty(cursor)
            || !long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0
            || !string.Equals(Encode(parsed), cursor, StringComparison.Ordinal))
        {
            return false;
        }

        sequence = parsed;

        return true;
    }
}
