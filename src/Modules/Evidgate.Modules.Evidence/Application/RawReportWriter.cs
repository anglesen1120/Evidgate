using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
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
/// refusal writes it alone.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>The longest code word the row's reason CHECK admits.</summary>
    private const int MaxReasonLength = 64;

    /// <summary>The longest producer kind the row's shape CHECK admits.</summary>
    private const int MaxProducerKindLength = 64;

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
    /// One row per (artifact, parser release), or per (artifact, producer kind) when no parser was resolved. A replay
    /// and a duplicate delivery conflict on that key and write nothing; the table refuses every update.
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
    /// The one insert of a <c>raw_report</c> row. A parsed report's row runs inside the caller's write transaction, after
    /// the events, the attempts and the slot have succeeded, so a rollback or a skipped attempt leaves no row behind; a
    /// refusal's row runs in its own short transaction.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="report">The row this attempt built.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    private async Task InsertRawReportAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        RawReportDraft report,
        CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(InsertRawReportSql, RawReportParameters(orgId, payload, report), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The one write of a refused report's row, in its own short transaction: a refusal produces no event, no slot and
    /// no trigger, so nothing else of the attempt is stored.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="outcome">One of <see cref="RawReportOutcomes"/>: <c>malformed</c> or <c>unsupported</c>.</param>
    /// <param name="reason">The code word the parse reported, kept as it is when the column admits it.</param>
    /// <param name="parserId">The registry's <c>parser_id</c>, or null when no parser was resolved.</param>
    /// <param name="parserVersion">The registry's <c>version</c>, or null when no parser was resolved.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task WriteRefusedAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        string outcome,
        string? reason,
        string? parserId,
        string? parserVersion,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        // The only fact the row needs beyond the payload: the artifact is still this run's ready raw report. A report
        // that left `ready` while the bytes were parsed is out of evidence, so it gets no row either.
        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == payload.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null
            || !string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal)
            || !string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal)
            || artifact.RunId != payload.RunId)
        {
            LogSkipped(logger, payload.ArtifactId, "left_ready");
            return;
        }

        if (!IsStorableProducerKind(payload.ProducerKind))
        {
            // The column admits only the registry's own kind vocabulary: a payload whose kind cannot be stored gets no
            // row rather than failing the refusal write.
            LogSkipped(logger, payload.ArtifactId, "unstorable_producer_kind");
            return;
        }

        await InsertRawReportAsync(
                orgId,
                payload,
                RefusedReport(outcome, reason, parserId, parserVersion),
                cancellationToken)
            .ConfigureAwait(false);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a refusal's one row and answers the named outcome the caller returns unchanged. The row's outcome and the
    /// task's outcome are the same word for a refusal, so they cannot drift.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="outcome">One of <see cref="RawReportOutcomes"/>: <c>malformed</c> or <c>unsupported</c>.</param>
    /// <param name="reason">The code word the parse reported.</param>
    /// <param name="parserId">The registry's <c>parser_id</c>, or null when no parser was resolved.</param>
    /// <param name="parserVersion">The registry's <c>version</c>, or null when no parser was resolved.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private async Task<ParseRawReportOutcome> RefuseAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        string outcome,
        string? reason,
        string? parserId,
        string? parserVersion,
        CancellationToken cancellationToken)
    {
        await WriteRefusedAsync(orgId, payload, outcome, reason, parserId, parserVersion, cancellationToken)
            .ConfigureAwait(false);

        return new ParseRawReportOutcome(outcome, reason);
    }

    /// <summary>
    /// The one malformed refusal of an attempt that already resolved a parser: the row names the release that judged the
    /// document, and the outcome keeps the reason word the parse reported.
    /// </summary>
    /// <param name="prepared">The parser release the refusal is recorded under.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="reason">The code word the parse reported.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private Task<ParseRawReportOutcome> RefuseMalformedAsync(
        Prepared prepared,
        Guid orgId,
        ParseRawReportPayload payload,
        string? reason,
        CancellationToken cancellationToken) =>
        RefuseAsync(
            orgId,
            payload,
            RawReportOutcomes.Malformed,
            reason,
            prepared.ParserId,
            prepared.Version,
            cancellationToken);

    /// <summary>
    /// The one unsupported refusal of an attempt the read scope ended: the row names the parser pair the registry
    /// resolved (or none, for a kind it does not know), and a skip writes nothing.
    /// </summary>
    /// <param name="preparation">What the read scope answered, including the pair a refusal row carries.</param>
    /// <param name="stopped">The outcome that ended the attempt.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private async Task<ParseRawReportOutcome> RefuseUnsupportedAsync(
        Preparation preparation,
        ParseRawReportOutcome stopped,
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        return stopped.IsRefusal
            ? await RefuseAsync(
                    orgId,
                    payload,
                    stopped.Status,
                    stopped.Reason,
                    preparation.ParserId,
                    preparation.ParserVersion,
                    cancellationToken)
                .ConfigureAwait(false)
            : stopped;
    }

    /// <summary>The insert's parameters, named after the columns they fill.</summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="report">The row this attempt built.</param>
    /// <returns>The anonymous parameter bag.</returns>
    private static object RawReportParameters(Guid orgId, ParseRawReportPayload payload, RawReportDraft report) => new
    {
        id = Guid.CreateVersion7(),
        org_id = orgId,
        run_id = payload.RunId,
        artifact_id = payload.ArtifactId,
        producer_kind = payload.ProducerKind,
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
    /// attempt could write), and the discrepancies between them. One discrepancy is enough to make it
    /// <c>partial</c>, because the two sides no longer agree.
    /// </summary>
    /// <param name="prepared">The parser release that judged the report.</param>
    /// <param name="mapped">What the mapper resolved.</param>
    /// <param name="cases">The mapped cases with a resolved execution key (the events the attempt writes).</param>
    /// <returns>The row the write transaction inserts.</returns>
    private static RawReportDraft ParsedReport(Prepared prepared, JUnitMapResult mapped, MappedCase[] cases)
    {
        var discrepancies = AggregateDiscrepancies(mapped.Discrepancies);

        return new RawReportDraft(
            mapped.Discrepancies.Count == 0 ? RawReportOutcomes.Mapped : RawReportOutcomes.Partial,
            null,
            prepared.ParserId,
            prepared.Version,
            mapped.Declared.Tests,
            mapped.Declared.Failures,
            mapped.Declared.Errors,
            mapped.Declared.Skipped,
            cases.Length,
            Observed(cases, PytestStatus.Failed),
            Observed(cases, PytestStatus.Error),
            Observed(cases, PytestStatus.Skipped),
            discrepancies);
    }

    /// <summary>
    /// The row of a report the parse refused: no event, no declared counter and no discrepancy, because nothing was
    /// mapped. The pair is carried only when the registry named one; an unregistered producer kind has none, and the
    /// row's key is then the kind it declared.
    /// </summary>
    /// <param name="outcome">One of <see cref="RawReportOutcomes"/>: <c>malformed</c> or <c>unsupported</c>.</param>
    /// <param name="reason">The code word the parse reported.</param>
    /// <param name="parserId">The registry's <c>parser_id</c>, when one was resolved.</param>
    /// <param name="parserVersion">The registry's <c>version</c>, when one was resolved.</param>
    /// <returns>The row the refusal write inserts.</returns>
    private static RawReportDraft RefusedReport(string outcome, string? reason, string? parserId, string? parserVersion)
    {
        var pairResolved = !string.IsNullOrEmpty(parserId) && !string.IsNullOrEmpty(parserVersion);

        return new RawReportDraft(
            outcome,
            RefusalReason(outcome, reason),
            pairResolved ? parserId : null,
            pairResolved ? parserVersion : null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            EmptyDiscrepancies);
    }

    /// <summary>
    /// The code word a refusal row stores: the parse's own word when it has the column's shape, and otherwise the
    /// outcome's own word. A child- or profile-supplied string therefore never fails the row's CHECK and hides the
    /// refusal instead.
    /// </summary>
    /// <param name="outcome">The refusal outcome, itself a valid code word.</param>
    /// <param name="reason">The parse's reason, possibly null or not a code word.</param>
    /// <returns>A word matching the column's <c>^[a-z][a-z0-9_]{0,63}$</c> shape.</returns>
    private static string RefusalReason(string outcome, string? reason) =>
        reason is not null && IsCodeWord(reason) ? reason : outcome;

    /// <summary>The producer kind column's shape: <c>^[a-z][a-z0-9-]*$</c>, one to 64 characters.</summary>
    /// <param name="kind">The kind the report declared.</param>
    /// <returns>True when the registry vocabulary admits the kind.</returns>
    private static bool IsStorableProducerKind(string kind)
    {
        if (kind.Length is 0 or > MaxProducerKindLength || !char.IsAsciiLetterLower(kind[0]))
        {
            return false;
        }

        foreach (var character in kind.AsSpan(1))
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The column's reason shape, one forward pass and no regex.</summary>
    /// <param name="value">The candidate word.</param>
    /// <returns>True when the value matches <c>^[a-z][a-z0-9_]{0,63}$</c>.</returns>
    private static bool IsCodeWord(string value)
    {
        if (value.Length is 0 or > MaxReasonLength || !char.IsAsciiLetterLower(value[0]))
        {
            return false;
        }

        foreach (var character in value.AsSpan(1))
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The mapper's discrepancy codes grouped into <c>{code, count}</c> objects, in the order the codes first appear and
    /// capped to the number of distinct codes the column admits. Codes only: a case name or a message body never
    /// reaches this row.
    /// </summary>
    /// <param name="codes">The codes the mapping reported, in document order.</param>
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
