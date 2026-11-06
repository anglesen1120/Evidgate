using System.Text.Json.Serialization;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>A stored report's declared and observed evidence, with one page of mapped cases.</summary>
/// <param name="ReportId">The stored report id.</param>
/// <param name="RunId">The report's run.</param>
/// <param name="RawReportArtifactId">The artifact that supplied the report.</param>
/// <param name="ProducerKind">The producer's declared kind.</param>
/// <param name="ParserId">The resolved parser, when one exists.</param>
/// <param name="ParserVersion">The parser release.</param>
/// <param name="Reason">The stored refusal reason.</param>
/// <param name="Discrepancies">Distinct effective report discrepancy codes, in canonical order.</param>
/// <param name="Summary">The stored counters and outcome.</param>
/// <param name="Cases">One sequence-ordered page of mapped cases.</param>
/// <param name="NextCursor">The last returned sequence when another page exists.</param>
public sealed record ReportComparison(
    [property: JsonPropertyName("report_id")] Guid ReportId,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("raw_report_artifact_id")] Guid RawReportArtifactId,
    [property: JsonPropertyName("producer_kind")] string ProducerKind,
    [property: JsonPropertyName("parser_id")] string? ParserId,
    [property: JsonPropertyName("parser_version")] string? ParserVersion,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("discrepancies")] string[] Discrepancies,
    [property: JsonPropertyName("summary")] ReportComparisonSummary Summary,
    [property: JsonPropertyName("cases")] ReportComparisonCase[] Cases,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>The document's counters beside the parser's observed counters and stored outcome.</summary>
/// <param name="DeclaredTests">Declared tests, or null when absent.</param>
/// <param name="DeclaredFailures">Declared failures, or null when absent.</param>
/// <param name="DeclaredErrors">Declared errors, or null when absent.</param>
/// <param name="DeclaredSkipped">Declared skips, or null when absent.</param>
/// <param name="ObservedTests">Mapped cases.</param>
/// <param name="ObservedFailures">Mapped failures.</param>
/// <param name="ObservedErrors">Mapped errors.</param>
/// <param name="ObservedSkipped">Mapped skips.</param>
/// <param name="Status">The stored report outcome.</param>
public sealed record ReportComparisonSummary(
    [property: JsonPropertyName("declared_tests")] int? DeclaredTests,
    [property: JsonPropertyName("declared_failures")] int? DeclaredFailures,
    [property: JsonPropertyName("declared_errors")] int? DeclaredErrors,
    [property: JsonPropertyName("declared_skipped")] int? DeclaredSkipped,
    [property: JsonPropertyName("observed_tests")] int ObservedTests,
    [property: JsonPropertyName("observed_failures")] int ObservedFailures,
    [property: JsonPropertyName("observed_errors")] int ObservedErrors,
    [property: JsonPropertyName("observed_skipped")] int ObservedSkipped,
    [property: JsonPropertyName("status")] string Status);

/// <summary>One execution's producer status and observed event count.</summary>
/// <param name="CaseRef">The execution key.</param>
/// <param name="DeclaredStatus">The producer's own status word.</param>
/// <param name="ObservedEvents">The number of matching terminal events.</param>
/// <param name="Discrepancies">Empty because stored report discrepancies have no case attribution.</param>
public sealed record ReportComparisonCase(
    [property: JsonPropertyName("case_ref")] string CaseRef,
    [property: JsonPropertyName("declared_status")] string? DeclaredStatus,
    [property: JsonPropertyName("observed_events")] int ObservedEvents,
    [property: JsonPropertyName("discrepancies")] string[] Discrepancies);
