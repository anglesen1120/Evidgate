using System.Text.Json;

namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// The body of <c>POST …/authoring/reports</c>: a documented shape of the request. The route reads the raw body, so
/// one bad member is answered with its own 422 path instead of an opaque refusal.
/// </summary>
/// <param name="ScopeKind"><c>feature_nodes</c>, <c>requirements</c>, <c>manual_run</c> or <c>release</c>.</param>
/// <param name="SelectedIds">1..1000 distinct ids of the scope kind, all of this project.</param>
/// <param name="Sections">1..4 distinct section ids, in the order the report shows them.</param>
/// <param name="Language"><c>vi</c> or <c>en</c>; every label of the snapshot is in this language.</param>
/// <param name="Strategy">Optional free text of at most 2000 code points, kept as written.</param>
/// <param name="Environment">Optional free text of at most 2000 code points, kept as written.</param>
public sealed record CreateTestReportRequest(
    string ScopeKind,
    IReadOnlyList<Guid> SelectedIds,
    IReadOnlyList<string> Sections,
    string Language,
    string? Strategy = null,
    string? Environment = null);

/// <summary>The 202 answer of a create: the job whose outcome the job route reports.</summary>
/// <param name="JobId">The opaque job reference; the finished report carries the same <c>job_id</c>.</param>
public sealed record TestReportJobAcceptedDto(Guid JobId);

/// <summary>One row of the report history: identity and hash, never the payload.</summary>
/// <param name="ReportId">The <c>test_report.id</c>.</param>
/// <param name="JobId">The job that produced it.</param>
/// <param name="ScopeKind">The scope kind the report was requested with.</param>
/// <param name="Language"><c>vi</c> or <c>en</c>.</param>
/// <param name="ContentHash">The <c>sha256:…</c> digest of the canonical payload.</param>
/// <param name="CreatedAt">When the snapshot was published.</param>
public sealed record TestReportSummaryDto(
    Guid ReportId,
    Guid JobId,
    string ScopeKind,
    string Language,
    string ContentHash,
    DateTimeOffset CreatedAt);

/// <summary>A keyset page of the report history, newest first.</summary>
/// <param name="Items">The page.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null on the last one.</param>
public sealed record TestReportListResponse(IReadOnlyList<TestReportSummaryDto> Items, string? NextCursor);

/// <summary>One report with its whole snapshot.</summary>
/// <param name="ReportId">The <c>test_report.id</c>.</param>
/// <param name="JobId">The job that produced it.</param>
/// <param name="ScopeKind">The scope kind.</param>
/// <param name="Language"><c>vi</c> or <c>en</c>.</param>
/// <param name="ContentHash">The <c>sha256:…</c> digest of the canonical payload.</param>
/// <param name="CreatedAt">When the snapshot was published.</param>
/// <param name="SchemaVersion">The snapshot schema the payload is written in.</param>
/// <param name="SelectedIds">The ids the report was requested for.</param>
/// <param name="Sections">The section ids in the order they are shown.</param>
/// <param name="Payload">The canonical payload; the only input of every renderer.</param>
public sealed record TestReportDetailDto(
    Guid ReportId,
    Guid JobId,
    string ScopeKind,
    string Language,
    string ContentHash,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    IReadOnlyList<Guid> SelectedIds,
    IReadOnlyList<string> Sections,
    JsonElement Payload);

/// <summary>Where a report job stands.</summary>
/// <param name="JobId">The job reference the create answered with.</param>
/// <param name="State"><c>pending</c>, <c>running</c>, <c>retry</c>, <c>failed</c> or <c>complete</c>.</param>
/// <param name="ReportId">The published report once the state is <c>complete</c>, else null.</param>
/// <param name="FailureReason"><c>report_too_large</c> or <c>build_failed</c> once the state is <c>failed</c>, else null.</param>
public sealed record TestReportJobDto(Guid JobId, string State, Guid? ReportId, string? FailureReason);
