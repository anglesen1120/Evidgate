using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.Modules.Evidence.Application.Freeze;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The <c>raw_report</c> half of <see cref="ReprocessReportHandler"/>: the row one attempt writes, its counters and the
/// discrepancies between what the report declared and what the mapper observed. The row is keyed on the pinned release
/// and on the request's own id, so a replay writes nothing and two requests can never describe one pair twice.
/// </summary>
internal sealed partial class ReprocessReportHandler
{
    /// <summary>The discrepancies of a report that mapped without a difference, or of one that was refused.</summary>
    private const string EmptyDiscrepancies = "[]";

    /// <summary>The most distinct discrepancy codes the column's shape admits.</summary>
    private const int MaxDiscrepancyCodes = 16;

    /// <summary>The row of one reprocessed report, as the insert reads it.</summary>
    /// <param name="Outcome">One of <see cref="RawReportOutcomes"/>.</param>
    /// <param name="Reason">The refusal code word, or null for a report that mapped.</param>
    /// <param name="ParserId">The pinned dialect.</param>
    /// <param name="ParserVersion">The pinned release.</param>
    /// <param name="DeclaredTests">The declared <c>tests</c>, or null when absent or unusable.</param>
    /// <param name="DeclaredFailures">The declared <c>failures</c>, or null.</param>
    /// <param name="DeclaredErrors">The declared <c>errors</c>, or null.</param>
    /// <param name="DeclaredSkipped">The declared <c>skipped</c>, or null.</param>
    /// <param name="ObservedTests">How many cases the mapper stored.</param>
    /// <param name="ObservedFailures">How many stored cases failed.</param>
    /// <param name="ObservedErrors">How many stored cases errored.</param>
    /// <param name="ObservedSkipped">How many stored cases were skipped.</param>
    /// <param name="Discrepancies">The discrepancy codes as one <c>jsonb</c> array.</param>
    private sealed record RawReportDraft(
        string Outcome,
        string? Reason,
        string? ParserId,
        string? ParserVersion,
        int? DeclaredTests,
        int? DeclaredFailures,
        int? DeclaredErrors,
        int? DeclaredSkipped,
        int ObservedTests,
        int ObservedFailures,
        int ObservedErrors,
        int ObservedSkipped,
        string Discrepancies);

    /// <summary>One discrepancy of the row: its closed code word and how many times the mapper reported it.</summary>
    /// <param name="Code">One <see cref="JUnitDiscrepancies"/> value.</param>
    /// <param name="Count">How many times the mapping reported that code.</param>
    private sealed record ReportedDiscrepancy(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("count")] int Count);

    /// <summary>
    /// The one insert of a reprocessed report's row. The id is the request's, so the row a trigger names and the row this
    /// attempt wrote are the same identity. A replay and a second request for the same key conflict and write nothing;
    /// the table refuses every update.
    /// </summary>
    private const string InsertRawReportSql = """
        insert into public.raw_report (
            id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version,
            outcome, reason, declared_tests, declared_failures, declared_errors, declared_skipped,
            observed_tests, observed_failures, observed_errors, observed_skipped, discrepancies)
        values (
            @id, @org_id, @run_id, @artifact_id, @producer_kind, @parser_id::text, @parser_version::text,
            @outcome, @reason::text, @declared_tests::integer, @declared_failures::integer,
            @declared_errors::integer, @declared_skipped::integer,
            @observed_tests, @observed_failures, @observed_errors, @observed_skipped, @discrepancies::jsonb)
        on conflict do nothing
        """;

    /// <summary>
    /// The outcome the row with this attempt's id already stores. A conflicting insert waits for the transaction that
    /// holds the key, so the read sees that row's committed verdict and never a half-written one.
    /// </summary>
    private const string StoredOutcomeByIdSql = """
        select outcome
        from public.raw_report
        where org_id = @org_id
          and id = @id
        """;

    /// <summary>
    /// Writes the report's own row inside the caller's transaction. A parsed attempt's row follows the events, so a
    /// rolled-back or conflicting attempt leaves no row; a refusal's row is the only thing its transaction writes.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The run, artifact and pinned release the row names.</param>
    /// <param name="report">The row this attempt built.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>True when the row was inserted, false when its key already held one.</returns>
    private async Task<bool> InsertRawReportAsync(
        Guid orgId,
        Prepared prepared,
        RawReportDraft report,
        CancellationToken cancellationToken)
    {
        var inserted = await db
            .ExecuteAsync(
                InsertRawReportSql,
                RawReportParameters(orgId, prepared, report),
                cancellationToken)
            .ConfigureAwait(false);

        return inserted > 0;
    }

    /// <summary>
    /// The outcome the row with this attempt's id already stores. It decides whether a mapped attempt may commit after
    /// its own insert wrote nothing: only a row of this same report can be there, and it must agree on having mapped.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="parsingId">The request's id, which is the row's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored outcome, or null when no row carries this id.</returns>
    private async Task<string?> StoredOutcomeByIdAsync(
        Guid orgId,
        Guid parsingId,
        CancellationToken cancellationToken) =>
        await db
            .ScalarAsync<string?>(
                StoredOutcomeByIdSql,
                new { org_id = orgId, id = parsingId },
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The report's own row, and the verdict it may not contradict: an insert that wrote nothing means the row with this
    /// id already exists, and only a parsed one (<c>mapped</c> or <c>partial</c>) lets this attempt continue. A stored
    /// refusal ends the attempt here, so the caller returns without committing and the events, the slot and the trigger
    /// written before this point roll back.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The request, its run and its pinned release.</param>
    /// <param name="report">The report's own row, built before this scope opened.</param>
    /// <param name="cancellationToken">Cancels the insert and the read.</param>
    /// <returns>
    /// The skipped outcome that ends the attempt (or null), and whether this attempt wrote the row: a replay wrote
    /// nothing, so it has no evidence to send to the reconciler.
    /// </returns>
    private async Task<(ParseRawReportOutcome? Stopped, bool Inserted)> StoreParsedRowAsync(
        Guid orgId,
        Prepared prepared,
        RawReportDraft report,
        CancellationToken cancellationToken)
    {
        if (await InsertRawReportAsync(orgId, prepared, report, cancellationToken).ConfigureAwait(false))
        {
            return (null, true);
        }

        var stored = await StoredOutcomeByIdAsync(orgId, prepared.ParsingId, cancellationToken).ConfigureAwait(false);
        if (stored is RawReportOutcomes.Mapped or RawReportOutcomes.Partial)
        {
            return (null, false);
        }

        // The stored row says the document was refused and this attempt mapped it: the refusal is immutable, and an event
        // set beside it would contradict what a reviewer reads. No content of the document is logged either.
        LogSkipped(logger, prepared.ParsingId, "report_outcome_conflict");
        return (new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "report_outcome_conflict"), false);
    }

    /// <summary>The insert's parameters, named after the columns they fill.</summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The run, artifact, producer kind and pinned release the row names.</param>
    /// <param name="report">The row this attempt built.</param>
    /// <returns>The anonymous parameter bag.</returns>
    private static object RawReportParameters(Guid orgId, Prepared prepared, RawReportDraft report) => new
    {
        id = prepared.ParsingId,
        org_id = orgId,
        run_id = prepared.RunId,
        artifact_id = prepared.ArtifactId,
        producer_kind = prepared.ProducerKind,
        parser_id = report.ParserId,
        parser_version = report.ParserVersion,
        outcome = report.Outcome,
        reason = report.Reason,
        declared_tests = report.DeclaredTests,
        declared_failures = report.DeclaredFailures,
        declared_errors = report.DeclaredErrors,
        declared_skipped = report.DeclaredSkipped,
        observed_tests = report.ObservedTests,
        observed_failures = report.ObservedFailures,
        observed_errors = report.ObservedErrors,
        observed_skipped = report.ObservedSkipped,
        discrepancies = report.Discrepancies,
    };

    /// <summary>
    /// The row of a report the mapper resolved: the counters it declared, the cases it produced (the event set this
    /// attempt writes), and every difference the check names between them. One code is enough to make the row
    /// <c>partial</c>, because the two sides no longer agree.
    /// </summary>
    /// <param name="prepared">The pinned release that judged the report.</param>
    /// <param name="mapped">What the mapper resolved.</param>
    /// <param name="cases">The mapped cases with a resolved execution key (the events this attempt writes).</param>
    /// <returns>The row the write transaction inserts.</returns>
    private static RawReportDraft ParsedReport(Prepared prepared, JUnitMapResult mapped, MappedCase[] cases)
    {
        var observed = new ObservedCounters(
            cases.Length,
            Observed(cases, PytestStatus.Failed),
            Observed(cases, PytestStatus.Error),
            Observed(cases, PytestStatus.Skipped));
        var codes = JUnitDeclaredObservedCheck.Compare(mapped.Declared, observed, mapped.Discrepancies);
        var discrepancies = AggregateDiscrepancies(codes);

        return new RawReportDraft(
            codes.Count == 0 ? RawReportOutcomes.Mapped : RawReportOutcomes.Partial,
            null,
            prepared.ParserId,
            prepared.ParserVersion,
            mapped.Declared.Tests,
            mapped.Declared.Failures,
            mapped.Declared.Errors,
            mapped.Declared.Skipped,
            observed.Tests,
            observed.Failures,
            observed.Errors,
            observed.Skipped,
            discrepancies);
    }

    /// <summary>
    /// The row of a document the parser profile or the mapper refused: no event, no declared counter and no discrepancy,
    /// because nothing was mapped. The pinned release is still named, because it is what judged the document.
    /// </summary>
    /// <param name="prepared">The pinned release the refusal is recorded under.</param>
    /// <param name="reason">The code word the refusal reported.</param>
    /// <returns>The row the refusal write inserts.</returns>
    private static RawReportDraft RefusedReport(Prepared prepared, string? reason) =>
        new(
            RawReportOutcomes.Malformed,
            RefusalReason(RawReportOutcomes.Malformed, reason),
            prepared.ParserId,
            prepared.ParserVersion,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            EmptyDiscrepancies);

    /// <summary>
    /// The code word a refusal row stores: the parse's own word when it has the column's shape, and otherwise the
    /// outcome's own word. A child- or profile-supplied string therefore never fails the row's CHECK and hides the
    /// refusal instead.
    /// </summary>
    /// <param name="outcome">The refusal outcome, itself a valid code word.</param>
    /// <param name="reason">The parse's reason, possibly null or not a code word.</param>
    /// <returns>A word matching the column's <c>^[a-z][a-z0-9_]{0,63}$</c> shape.</returns>
    private static string RefusalReason(string outcome, string? reason) =>
        reason is not null && EvidenceFreeze.IsReasonCode(reason) ? reason : outcome;

    /// <summary>
    /// The mapper's discrepancy codes grouped into <c>{code, count}</c> objects, in the order the codes first appear and
    /// capped to the number of distinct codes the column admits. Codes only: a case name or a message body never reaches
    /// this row.
    /// </summary>
    /// <param name="codes">The codes the check returned, already in canonical order.</param>
    /// <returns>The array as one <c>jsonb</c> parameter.</returns>
    private static string AggregateDiscrepancies(IReadOnlyList<string> codes)
    {
        if (codes.Count == 0)
        {
            return EmptyDiscrepancies;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstSeen = new List<string>(Math.Min(codes.Count, MaxDiscrepancyCodes));

        foreach (var code in codes)
        {
            if (counts.TryGetValue(code, out var count))
            {
                counts[code] = count + 1;
                continue;
            }

            if (firstSeen.Count < MaxDiscrepancyCodes)
            {
                counts[code] = 1;
                firstSeen.Add(code);
            }
        }

        return JsonSerializer.Serialize(firstSeen.Select(code => new ReportedDiscrepancy(code, counts[code])));
    }

    /// <summary>How many mapped cases the mapper stored with one normalised status.</summary>
    /// <param name="cases">The mapped cases with a resolved execution key.</param>
    /// <param name="status">The normalised status to count.</param>
    /// <returns>The number of cases carrying that status.</returns>
    private static int Observed(MappedCase[] cases, string status)
    {
        var count = 0;

        foreach (var item in cases)
        {
            if (string.Equals(item.Normalized, status, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
