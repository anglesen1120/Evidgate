using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The outcome vocabulary of <c>public.raw_report</c>: what the parse decided about one report, kept apart from the
/// events it may have produced.
/// </summary>
internal static class RawReportOutcomes
{
    /// <summary>The report parsed and its declared counters agree with the observed ones.</summary>
    public const string Mapped = "mapped";

    /// <summary>The report parsed and at least one discrepancy explains a difference.</summary>
    public const string Partial = "partial";

    /// <summary>The parser profile refused the document: no event and no counter.</summary>
    public const string Malformed = "malformed";

    /// <summary>No parser is registered for the producer kind, or none is loaded.</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// The <c>raw_report</c> half of <see cref="ParseRawReportHandler"/>: one append-only row per report and parser release.
/// The row is built before any scope opens, so a parsed report carries it in the same transaction as its events, and a
/// refusal carries it with the reconcile trigger the missing evidence needs.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>The most distinct discrepancy codes the row's shape CHECK admits.</summary>
    private const int MaxDiscrepancyCodes = 16;

    /// <summary>The empty discrepancy array, as the column's own default spells it.</summary>
    private const string EmptyDiscrepancies = "[]";

    /// <summary>
    /// One attempt's row as the insert reads it: the parser release that judged the report, what the document declared,
    /// what the mapper observed, and the discrepancy counts that explain a difference between the two.
    /// </summary>
    /// <param name="Outcome">One of <see cref="RawReportOutcomes"/>.</param>
    /// <param name="Reason">The code word behind a refusal; null for a parsed report.</param>
    /// <param name="ParserId">The registry's <c>parser_id</c>, or null when no parser was resolved.</param>
    /// <param name="ParserVersion">The registry's <c>version</c>, or null when no parser was resolved.</param>
    /// <param name="DeclaredTests">The declared <c>tests</c>, or null when the document gave none.</param>
    /// <param name="DeclaredFailures">The declared <c>failures</c>, or null when the document gave none.</param>
    /// <param name="DeclaredErrors">The declared <c>errors</c>, or null when the document gave none.</param>
    /// <param name="DeclaredSkipped">The declared <c>skipped</c>, or null when the document gave none.</param>
    /// <param name="ObservedTests">The mapped cases, i.e. the events this report could write.</param>
    /// <param name="ObservedFailures">The mapped cases the mapper stored as <c>failed</c>.</param>
    /// <param name="ObservedErrors">The mapped cases the mapper stored as <c>error</c>.</param>
    /// <param name="ObservedSkipped">The mapped cases the mapper stored as <c>skipped</c>.</param>
    /// <param name="Discrepancies">The <c>{code, count}</c> array as one <c>jsonb</c> parameter.</param>
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
    /// The one insert of a <c>raw_report</c> row. A parsed report's row runs inside the caller's write transaction, after
    /// the events, the attempts and the slot have succeeded, so a rollback or a skipped attempt leaves no row behind; a
    /// refusal's row runs in its own short transaction.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="report">The row this attempt built.</param>
    /// <param name="rawReportId">
    /// The row's own id, minted by the caller so a trigger written beside it can name that row and not the artifact.
    /// </param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>True when the row was inserted, false when the key already held one.</returns>
    private async Task<bool> InsertRawReportAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        RawReportDraft report,
        Guid rawReportId,
        CancellationToken cancellationToken)
    {
        var inserted = await db
            .ExecuteAsync(
                RawReportWorkerSql.InsertRawReport,
                RawReportParameters(orgId, payload, report, rawReportId),
                cancellationToken)
            .ConfigureAwait(false);

        return inserted > 0;
    }

    /// <summary>
    /// The outcome the row of this (artifact, parser release) key already stores. A conflicting insert waits for the
    /// transaction that holds the key, so the read sees that row's committed verdict and never a half-written one.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="artifactId">The report artifact the row hangs off.</param>
    /// <param name="report">The row this attempt built; its parser pair is the key it shares with the stored row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored outcome, or null when the pair is unresolved or no row holds that key.</returns>
    private async Task<string?> StoredOutcomeAsync(
        Guid orgId,
        Guid artifactId,
        RawReportDraft report,
        CancellationToken cancellationToken)
    {
        if (report.ParserId is null || report.ParserVersion is null)
        {
            return null;
        }

        return await db
            .ScalarAsync<string?>(
                RawReportWorkerSql.StoredOutcome,
                new
                {
                    org_id = orgId,
                    artifact_id = artifactId,
                    parser_id = report.ParserId,
                    parser_version = report.ParserVersion,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The insert's parameters, named after the columns they fill.</summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="report">The row this attempt built.</param>
    /// <param name="rawReportId">The row's own id, which an accompanying trigger may name.</param>
    /// <returns>The anonymous parameter bag.</returns>
    private static object RawReportParameters(
        Guid orgId,
        ParseRawReportPayload payload,
        RawReportDraft report,
        Guid rawReportId) => new
        {
            id = rawReportId,
            org_id = orgId,
            run_id = payload.RunId,
            artifact_id = payload.ArtifactId,
            producer_kind = payload.StoredProducerKind,
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
    /// The row of a report the mapper resolved: the counters it declared, the cases it produced (the event set the
    /// attempt could write), and every difference the check names between them. One code is enough to make it
    /// <c>partial</c>, because the two sides no longer agree.
    /// </summary>
    /// <param name="prepared">The parser release that judged the report.</param>
    /// <param name="mapped">What the mapper resolved.</param>
    /// <param name="cases">The mapped cases with a resolved execution key (the events the attempt writes).</param>
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
            prepared.Version,
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
    /// The mapper's discrepancy codes grouped into <c>{code, count}</c> objects, in the order the codes first appear and
    /// capped to the number of distinct codes the column admits. Codes only: a case name or a message body never
    /// reaches this row.
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
