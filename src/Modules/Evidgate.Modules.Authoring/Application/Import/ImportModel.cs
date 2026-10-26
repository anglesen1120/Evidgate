namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>The kind of one import/export row (13.1.1 AC13/AC14): the flat table carries the whole tree.</summary>
internal enum ImportRowKind
{
    /// <summary>A <c>feature_node</c> row; <see cref="ImportRow.ParentKey"/> is the parent node's key (null for a module).</summary>
    Node,

    /// <summary>A <c>checklist_item</c> row; <see cref="ImportRow.ParentKey"/> is the owning node's key.</summary>
    Item,

    /// <summary>A <c>manual_test_case</c> row; <see cref="ImportRow.ItemKey"/> is the owning item's key.</summary>
    Case,
}

/// <summary>
/// One row of the import/export table, the single shape JSON, CSV and XLSX all parse into and write from (13.1.1 §3
/// <c>authoring-import.v1.json</c>). Every text member is NFC and free of U+0000 and lone surrogates once a reader has
/// produced it; a reader refuses (never repairs) anything else with reason <c>invalid_text</c>/<c>value_invalid</c>.
/// </summary>
/// <param name="Index">The 1-based data-row position in the file (the header row is not counted); 0 is never a row.</param>
/// <param name="Kind">node, item or case.</param>
/// <param name="Key">The row's file-local key (unique per file); export writes <c>n1…</c>, <c>i1…</c>, <c>c1…</c> in tree order.</param>
/// <param name="ParentKey">Node row: parent node key or null. Item row: owning node key. Case row: null.</param>
/// <param name="ItemKey">Case row: owning item key. Otherwise null.</param>
/// <param name="NodeKind">Node row only: <c>module</c>, <c>feature</c>, <c>category</c> or <c>sub_category</c> as stored.</param>
/// <param name="Title">Title of the node, item or case.</param>
/// <param name="Description">Item or case description.</param>
/// <param name="Precondition">Case precondition.</param>
/// <param name="StepsJson">Case <c>steps</c> document as JSON text (<c>{"steps":[…]}</c>, authoring-steps.v1); null for node/item.</param>
/// <param name="Priority">Item or case priority as stored.</param>
/// <param name="State">Item or case state as stored (draft/active/retired, draft/approved/retired); null means the creation default.</param>
/// <param name="Code">Case code (<c>PREFIX-NNN</c>); null/empty on a new case means "allocate one".</param>
/// <param name="Position">Sibling position; null means append.</param>
internal sealed record ImportRow(
    int Index,
    ImportRowKind Kind,
    string Key,
    string? ParentKey,
    string? ItemKey,
    string? NodeKind,
    string? Title,
    string? Description,
    string? Precondition,
    string? StepsJson,
    string? Priority,
    string? State,
    string? Code,
    int? Position);

/// <summary>One refusal found while reading or validating a file: becomes one <c>details[]</c> entry (00 §9.2, AC13/AC16).</summary>
/// <param name="Index">The 1-based data-row position, or null for a file-level reason (zip caps, unsupported format).</param>
/// <param name="Path">A fixed member pointer such as <c>/title</c> or <c>/key</c>; never a cell value (S3).</param>
/// <param name="Reason">AC13 reasons (<c>unknown_parent</c>, <c>orphan_row</c>, <c>duplicate_key</c>, <c>code_conflict</c>, <c>unsupported_format</c>, <c>value_invalid</c>, <c>steps_invalid</c>) or AC16 reasons (<c>zip_size_exceeded</c>, <c>zip_entries_exceeded</c>, <c>zip_ratio_exceeded</c>, <c>dtd_prohibited</c>, <c>macro_prohibited</c>) or <c>invalid_text</c>.</param>
internal sealed record ImportIssue(int? Index, string Path, string Reason);

/// <summary>The outcome of reading one file: rows, or issues, or "too large" (413).</summary>
internal sealed class ImportParse
{
    private ImportParse(IReadOnlyList<ImportRow> rows, IReadOnlyList<ImportIssue> issues, bool tooLarge)
    {
        Rows = rows;
        Issues = issues;
        TooLarge = tooLarge;
    }

    /// <summary>The rows when <see cref="IsSuccess"/>.</summary>
    public IReadOnlyList<ImportRow> Rows { get; }

    /// <summary>The refusals when not successful and not <see cref="TooLarge"/>.</summary>
    public IReadOnlyList<ImportIssue> Issues { get; }

    /// <summary>True when the file holds more than <see cref="ImportLimits.MaxDataRows"/> data rows (413 payload_too_large).</summary>
    public bool TooLarge { get; }

    /// <summary>True when rows were read and nothing was refused.</summary>
    public bool IsSuccess => !TooLarge && Issues.Count == 0;

    /// <summary>A successful read.</summary>
    public static ImportParse Ok(IReadOnlyList<ImportRow> rows) => new(rows, [], false);

    /// <summary>A refused read; the issues are reported, nothing is applied.</summary>
    public static ImportParse Invalid(IReadOnlyList<ImportIssue> issues) => new([], issues, false);

    /// <summary>A file over the row cap (413).</summary>
    public static ImportParse Large() => new([], [], true);
}

/// <summary>The import caps of AC16, constants in code (13.1.1 §11 kill-switch note).</summary>
internal static class ImportLimits
{
    /// <summary>The raw body cap, 5 MiB; over it is 413 before parsing.</summary>
    public const int MaxBodyBytes = 5 * 1024 * 1024;

    /// <summary>The most data rows one file may hold; over it is 413.</summary>
    public const int MaxDataRows = 5_000;

    /// <summary>The XLSX archive's expanded-size cap, 64 MiB.</summary>
    public const long MaxXlsxExpandedBytes = 64L * 1024 * 1024;

    /// <summary>The XLSX archive's entry cap.</summary>
    public const int MaxXlsxEntries = 1_000;

    /// <summary>The XLSX archive's per-entry compression ratio cap, 20:1.</summary>
    public const int MaxXlsxCompressionRatio = 20;
}

/// <summary>The per-outcome totals of one import (AC13).</summary>
/// <param name="Created">Rows that created an entity.</param>
/// <param name="Updated">Rows that updated a live entity.</param>
/// <param name="Restored">Case rows whose code matched a soft-deleted case (change_kind restore).</param>
/// <param name="Unchanged">Rows whose content equals the stored content (AC15).</param>
internal sealed record ImportCounts(int Created, int Updated, int Restored, int Unchanged);

/// <summary>One row of the import report (<c>rows[]</c>); never carries a cell value (S3).</summary>
/// <param name="Index">The 1-based data-row position.</param>
/// <param name="Key">The row's file key.</param>
/// <param name="Kind">node, item or case.</param>
/// <param name="Outcome"><c>create</c>, <c>update</c>, <c>restore</c> or <c>unchanged</c>.</param>
/// <param name="Code">The case code the row has after the import (null for node/item rows; null on a dry run for a case that would get a fresh code).</param>
internal sealed record ImportRowReport(int Index, string Key, ImportRowKind Kind, string Outcome, string? Code);

/// <summary>The result of <c>AuthoringImporter.ImportAsync</c> (AC13/AC15).</summary>
/// <param name="BatchId">The single <c>batch_id</c> of every version row written; null on a dry run and when every row is unchanged.</param>
/// <param name="Counts">The totals.</param>
/// <param name="Rows">One entry per input row, in input order.</param>
internal sealed record ImportReport(Guid? BatchId, ImportCounts Counts, IReadOnlyList<ImportRowReport> Rows);
