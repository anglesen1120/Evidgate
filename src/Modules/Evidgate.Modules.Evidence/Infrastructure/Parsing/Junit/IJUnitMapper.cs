using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// One producer's versioned JUnit XML mapping (<c>report_parser</c> row): the producer dialect is named by
/// <see cref="ParserId"/> and pinned by <see cref="Version"/>, so a reprocess under a new dialect version is a
/// different parser rather than a silent change of the same one.
/// <para>
/// The mapper is a pure function of the profile-validated bytes and the caller's resolution callback: it
/// persists nothing and reads no clock, so two runs over the same bytes and version produce identical cases
/// (document order, never wall-clock or receive order).
/// </para>
/// </summary>
internal interface IJUnitMapper
{
    /// <summary>The <c>report_parser.parser_id</c> this mapper implements (<c>junit-pytest</c>).</summary>
    string ParserId { get; }

    /// <summary>The dialect version; part of every event's <c>ext.parser</c> stamp and the row's key.</summary>
    string Version { get; }

    /// <summary>
    /// Maps one report. The caller owns the stream and disposes it.
    /// </summary>
    /// <param name="xml">The untrusted report bytes.</param>
    /// <param name="context">The synthetic producer identity and the caller's execution-key resolution.</param>
    /// <returns>One mapped case per resolvable <c>testcase</c>, the declared counters and the discrepancies.</returns>
    /// <exception cref="JUnitParseRefusedException">
    /// The parser profile refused the document (malformed, DTD, over a cap). Only a profile refusal becomes this
    /// exception: a caller bug or a resolver failure propagates as itself, so a fault can never read as a
    /// declared malformed report.
    /// </exception>
    JUnitMapResult Map(Stream xml, JUnitMapContext context);
}

/// <summary>
/// What the mapper needs from its caller for one report.
/// <para>
/// <see cref="ResolveExecutionKey"/> is the caller's own identity resolution (planned execution lookup): the
/// mapper never invents a key, so a report cannot name an execution the run never planned.
/// </para>
/// </summary>
/// <param name="RawReportId">The stored raw report's identity (the artifact id); the synthetic producer is derived from it.</param>
/// <param name="ResolveExecutionKey">
/// Maps a case identity hint (<c>classname</c>, <c>name</c>, raw as written, empty when absent) to the run's
/// planned <c>execution_key</c>, or <see langword="null"/> when the run planned no such execution.
/// </param>
internal sealed record JUnitMapContext(
    Guid RawReportId,
    Func<string, string, string?> ResolveExecutionKey);

/// <summary>One <c>testcase</c> that resolved to a planned execution.</summary>
/// <param name="ExecutionKey">The resolved <c>execution_key</c>; never guessed.</param>
/// <param name="RawStatus">The producer's own status word, verbatim (<c>failure</c>, <c>error</c>, <c>skipped</c>, <c>passed</c>).</param>
/// <param name="Normalized">The stored status: <c>passed</c>, <c>failed</c>, <c>error</c> or <c>skipped</c>.</param>
/// <param name="DurationMs">The testcase's wall time in whole milliseconds, or <see langword="null"/> when absent or unusable.</param>
/// <param name="Classname">The raw <c>classname</c> attribute (empty when absent).</param>
/// <param name="Name">The raw <c>name</c> attribute (empty when absent).</param>
/// <param name="Sequence">1-based document position among the report's <c>testcase</c> elements.</param>
internal sealed record MappedCase(
    string ExecutionKey,
    string RawStatus,
    string Normalized,
    long? DurationMs,
    string Classname,
    string Name,
    int Sequence);

/// <summary>
/// The counters a <c>testsuite</c>/<c>testsuites</c> element declares, kept apart from the counts the mapper
/// observed: the declared numbers are the producer's claim, and a disagreement is a discrepancy, never an
/// overwrite of what the events actually say.
/// </summary>
/// <param name="Tests">The declared <c>tests</c>, or <see langword="null"/> when absent or unusable.</param>
/// <param name="Failures">The declared <c>failures</c>, or <see langword="null"/> when absent or unusable.</param>
/// <param name="Errors">The declared <c>errors</c>, or <see langword="null"/> when absent or unusable.</param>
/// <param name="Skipped">The declared <c>skipped</c>, or <see langword="null"/> when absent or unusable.</param>
internal sealed record JUnitDeclaredCounters(int? Tests, int? Failures, int? Errors, int? Skipped);

/// <summary>
/// What one report mapped to. <see cref="Discrepancies"/> uses the closed code-word vocabulary in
/// <see cref="JUnitDiscrepancies"/> so the comparison read can explain every difference by name.
/// </summary>
/// <param name="Cases">The mapped cases in document order.</param>
/// <param name="Declared">The counters the document declared.</param>
/// <param name="Discrepancies">Why the declared document and the mapped cases disagree, in document order.</param>
internal sealed record JUnitMapResult(
    IReadOnlyList<MappedCase> Cases,
    JUnitDeclaredCounters Declared,
    IReadOnlyList<string> Discrepancies);

/// <summary>The discrepancy vocabulary the mapping result and the comparison read share.</summary>
internal static class JUnitDiscrepancies
{
    /// <summary>The report named a testcase the run planned no execution for; no event was emitted.</summary>
    public const string UnmappedCase = "unmapped_case";

    /// <summary>Two testcases resolved to one execution key; the later one was skipped.</summary>
    public const string DuplicateCase = "duplicate_case";

    /// <summary>The declared <c>tests</c> counter differs from the testcases the document actually carries.</summary>
    public const string CountMismatch = "count_mismatch";
}

/// <summary>
/// A document the parser profile refused (<c>artifact_invalid</c>, or <c>payload_too_large</c> for the byte
/// cap). <see cref="Reason"/> is one profile code word, so the caller can store the report as
/// <c>malformed</c> without parsing a message; the message itself is the profile's secret-free text.
/// </summary>
internal sealed class JUnitParseRefusedException : Exception
{
    /// <summary>Creates the refusal for one profile failure.</summary>
    /// <param name="reason">One <c>ParsingFailureReason</c> code word (or the failure's own code).</param>
    /// <param name="message">The profile's generic, secret-free message.</param>
    public JUnitParseRefusedException(string reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The profile reason code: <c>malformed</c>, <c>dtd_prohibited</c>, <c>size_exceeded</c>, …</summary>
    public string Reason { get; }

    /// <summary>Builds the refusal from a profile failure, preferring the per-content reason over the code.</summary>
    /// <param name="failure">The profile's <see cref="Error"/>.</param>
    /// <returns>The refusal the caller catches by this exact type.</returns>
    public static JUnitParseRefusedException From(Error failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var reason = failure.Details
            ?.FirstOrDefault(detail => string.Equals(detail.Path, "content", StringComparison.Ordinal))
            ?.Reason
            ?? (failure.Details is { Count: > 0 } details ? details[0].Reason : null)
            ?? failure.Code;

        return new JUnitParseRefusedException(reason, failure.Message);
    }
}
