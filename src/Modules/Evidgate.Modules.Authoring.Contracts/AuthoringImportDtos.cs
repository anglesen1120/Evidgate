namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// The answer of the import route (13.1.1 §5 route 25, AC13/AC15): one <c>batch_id</c> for every version row the file
/// wrote, the per-outcome totals and one row report per input row, in input order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nulls are meaningful.</b> <see cref="BatchId"/> is null on a dry run (nothing was written, so there is no batch) and
/// on an import where every row is <c>unchanged</c> (no version row exists to name) — AC15's "appends nothing" answer is
/// distinguishable from a write by that null alone, not by a status code. <see cref="ImportRowDto.Code"/> is null for a
/// node or item row and, on a dry run, for a case row that would receive a fresh code (no number is allocated before the
/// real run, so none can be reported).
/// </para>
/// <para>
/// <b>No cell text.</b> The report names a row by its file position, its file-local key, its kind and its outcome; the
/// title, the description, the steps and the code the file carried never travel back (S3), so a refusal or a report can
/// never echo a caller's content into a log or a client.
/// </para>
/// </remarks>
/// <param name="BatchId">The <c>batch_id</c> shared by every <c>authoring_version</c> row this file wrote; null when nothing was written.</param>
/// <param name="Counts">The per-outcome totals of the file.</param>
/// <param name="Rows">One entry per data row, in file order.</param>
public sealed record ImportResponse(Guid? BatchId, ImportCountsDto Counts, IReadOnlyList<ImportRowDto> Rows);

/// <summary>The per-outcome totals of one import (13.1.1 AC13/AC15).</summary>
/// <param name="Created">Rows that created an entity.</param>
/// <param name="Updated">Rows that updated a live entity whose content differed.</param>
/// <param name="Restored">Rows whose code matched a soft-deleted case and restored it (<c>change_kind = restore</c>).</param>
/// <param name="Unchanged">Rows whose content already matched the stored content; they appended no version.</param>
public sealed record ImportCountsDto(int Created, int Updated, int Restored, int Unchanged);

/// <summary>One row of the import report: the row's position, key, kind and outcome (never its text).</summary>
/// <param name="Index">The 1-based data-row position in the file (the header row is not counted).</param>
/// <param name="Key">The row's file-local key (<c>n1</c>, <c>i1</c>, <c>c1</c> on an export).</param>
/// <param name="Kind">The row kind: <c>node</c>, <c>item</c> or <c>case</c>.</param>
/// <param name="Outcome"><c>create</c>, <c>update</c>, <c>restore</c> or <c>unchanged</c>.</param>
/// <param name="Code">A case row's code after the import; null for a node/item row and for a dry-run case without one.</param>
public sealed record ImportRowDto(int Index, string Key, string Kind, string Outcome, string? Code);
