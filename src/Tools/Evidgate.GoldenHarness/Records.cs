namespace Evidgate.GoldenHarness;

/// <summary>
/// One <c>fixtures/&lt;producer&gt;/&lt;case&gt;/</c> case. <see cref="Directory"/> is the
/// absolute case directory; <see cref="ReportFiles"/> holds the file names inside it,
/// ordinal, so a shard-merged case can carry more than one original report.
/// </summary>
public sealed record FixtureCase(
    string Producer,
    string Name,
    string Directory,
    CaseMeta Meta,
    ExpectedDoc Expected,
    IReadOnlyList<string> ReportFiles);

/// <summary>
/// <c>case.json</c>: the producer and parser the expectation was captured against, and
/// why the case exists. <see cref="ParserStatus"/> stays <c>pending</c> while no parser
/// implements <see cref="ParserId"/> yet, which is what lets a case land before its adapter.
/// </summary>
public sealed record CaseMeta(
    string ProducerId,
    string ParserId,
    string ParserVersion,
    string Rationale,
    string? ParserStatus);

/// <summary><c>expected.json</c>: what the parser must produce for this case.</summary>
public sealed record ExpectedDoc(
    string RawReportStatus,
    int DeclaredCount,
    IReadOnlyList<NormalisedEvent> Events);

/// <summary>
/// One normalised event, named the way <c>expected.json</c> names it (snake_case), so a
/// diff field name is also the JSON member a reader has to change.
/// </summary>
public sealed record NormalisedEvent(
    string ProducerId,
    long Sequence,
    string CaseKey,
    string RawStatus,
    string NormalisedStatus);

/// <summary>
/// A case directory does not satisfy the layout contract. The message starts with
/// <c>&lt;producer&gt;/&lt;case&gt;</c> so a failing run names the fixture, not just the rule.
/// </summary>
public sealed class FixtureLayoutException(string fixture, string message)
    : Exception($"{fixture}: {message}")
{
    /// <summary>The <c>&lt;producer&gt;/&lt;case&gt;</c> identity (or the root path for a missing root).</summary>
    public string Fixture { get; } = fixture;
}

/// <summary>
/// One difference. <see cref="Field"/> is a JSON member name, or <c>declared_count</c>/
/// <c>raw_report_status</c> for a document-level difference and <c>event</c> for a whole
/// missing or extra event. <see cref="EventIndex"/> is the sorted expected position; an extra
/// observed event carries its own position instead.
/// </summary>
public sealed record DiffEntry(
    int? EventIndex,
    string Field,
    string Reason,
    string? Expected,
    string? Observed);

/// <summary>
/// The reason vocabulary of a diff: the parser's own discrepancy codes plus the event-level
/// codes only a comparison adds. Both the tool and the runner read these, so one reason is
/// never spelled two ways.
/// </summary>
public static class DiffReasons
{
    /// <summary>Declared count differs from the declared count the parser reported.</summary>
    public const string CountMismatch = "count_mismatch";

    /// <summary>The same producer event resolved to a different case identity.</summary>
    public const string UnmappedCase = "unmapped_case";

    /// <summary>A raw or normalised status (or the report's own status) differs.</summary>
    public const string StatusMismatch = "status_mismatch";

    /// <summary>The parser produced an event the expectation does not have.</summary>
    public const string ExtraEvent = "extra_event";

    /// <summary>The expectation has an event the parser did not produce.</summary>
    public const string MissingEvent = "missing_event";

    /// <summary>Any other member of an otherwise matching event differs.</summary>
    public const string FieldMismatch = "field_mismatch";

    /// <summary>The parser refused the report, which only a declared <c>malformed</c> expectation explains.</summary>
    public const string Malformed = "malformed";
}

/// <summary>One case's outcome in the harness table.</summary>
public enum Verdict
{
    /// <summary>Produced events match the expectation.</summary>
    Pass,

    /// <summary>An unexplained difference, or a parser refusal the expectation does not declare.</summary>
    Fail,

    /// <summary>No parser is registered yet and the case says it is <c>pending</c>.</summary>
    Pending,

    /// <summary>The case could not be run: a layout refusal or an unregistered parser that claims no pending state.</summary>
    Error,
}

/// <summary>
/// One printed row: <see cref="Fixture"/> is <c>&lt;producer&gt;/&lt;case&gt;</c>, and
/// <see cref="Diffs"/> holds what failed so the table can name the field.
/// </summary>
public sealed record CaseVerdict(
    string Fixture,
    Verdict Verdict,
    IReadOnlyList<DiffEntry> Diffs,
    string? Message);

/// <summary>
/// An in-process parser entry point, called with the case that names it. It throws to signal
/// a report it cannot parse; the runner decides whether that refusal was declared.
/// </summary>
public delegate ExpectedDoc ParseFixture(FixtureCase fixture);
