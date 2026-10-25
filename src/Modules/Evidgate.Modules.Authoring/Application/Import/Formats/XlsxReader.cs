using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// Reads one XLSX body into the shared row table (13.1.1 AC13/AC14/AC16, T5.2): the archive walk and every XML part
/// go through the 06.3.1 parser profile, the first worksheet is resolved through the workbook and its relationships,
/// and its cells become the same <c>authoring-import.v1</c> document the JSON body carries — so the schema, the row
/// rules and the text hygiene of the other two formats are the ones this format gets
/// (<see cref="ImportSchema"/> and <see cref="ImportRows"/> are the one implementation of them).
/// </summary>
/// <remarks>
/// <para>
/// The order is deliberate: the archive is walked under the AC16 caps first, then a macro part is refused from the
/// entry metadata and the content types, and only then are the workbook, its relationships and the worksheet parsed
/// — so a hostile package is refused before any sheet is examined, and every reason names the guard
/// (<c>zip_size_exceeded</c>, <c>zip_entries_exceeded</c>, <c>zip_ratio_exceeded</c>, <c>dtd_prohibited</c>,
/// <c>macro_prohibited</c>, <c>unsupported_format</c>) rather than a cell (S3).
/// </para>
/// <para>
/// Cell types this reader accepts: <c>t="s"</c> (a shared-string index), <c>t="inlineStr"</c> (an <c>is</c> run),
/// <c>t="str"</c> and <c>t="n"</c>; styles are ignored, and a boolean, error or date cell is a <c>value_invalid</c>
/// refusal because the flat table has no column for it. Every cell is text except the numeric
/// <c>position</c> column, and a text cell is unescaped with <see cref="XlsxFormat.Unescape"/> before the shared row
/// rules see it, so a workbook this module wrote is read back exactly.
/// </para>
/// </remarks>
internal static class XlsxReader
{
    /// <summary>The AC13 reason for a file the XLSX carrier cannot represent (a broken part graph, a missing header).</summary>
    private const string UnsupportedFormat = "unsupported_format";

    /// <summary>The AC13 reason for one cell that breaks its column's rule.</summary>
    private const string ValueInvalid = "value_invalid";

    /// <summary>The AC16 reason for an archive over the expanded-size cap or a part over its own cap.</summary>
    private const string ZipSizeExceeded = "zip_size_exceeded";

    /// <summary>The AC16 reason for an archive over the entry cap.</summary>
    private const string ZipEntriesExceeded = "zip_entries_exceeded";

    /// <summary>The AC16 reason for an entry over the compression-ratio cap.</summary>
    private const string ZipRatioExceeded = "zip_ratio_exceeded";

    /// <summary>The AC16 reason for a part that declares or reaches a DTD or an external entity.</summary>
    private const string DtdProhibited = "dtd_prohibited";

    /// <summary>The AC16 reason for a package carrying a macro part or a macro-enabled content type.</summary>
    private const string MacroProhibited = "macro_prohibited";

    /// <summary>The pointer a cell beyond the thirteen columns carries; the header has no name for it (S3).</summary>
    private const string ExtraColumnPath = "/extra_column";

    /// <summary>The file suffix of the macro part the corpus and every Excel writer use.</summary>
    private const string MacroPartFileName = "vbaproject.bin";

    /// <summary>The column of the <c>steps</c> cell, which carries a nested JSON document.</summary>
    private static readonly int StepsColumn = Array.IndexOf(ImportSchema.ColumnNames, "steps");

    /// <summary>Reads one XLSX body (13.1.1 AC13/AC16).</summary>
    /// <param name="body">The already-bounded request body.</param>
    /// <param name="parsing">The parser profile the archive walk and every part go through.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows, the refusals, or the 413 verdict for a file over the row cap.</returns>
    internal static ImportParse Read(byte[] body, IParsing parsing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(parsing);

        // An empty body is not an archive and the profile's caps are positive by contract, so the refusal is decided
        // here rather than by a cap of zero bytes.
        if (body.Length == 0)
        {
            return ImportParse.Invalid([FileIssue(UnsupportedFormat)]);
        }

        var archive = ReadArchive(body, parsing, cancellationToken);

        if (!archive.IsSuccess)
        {
            return ImportParse.Invalid([MapFailure(archive.Error!)]);
        }

        var contents = archive.Value;

        // The macro guard is metadata-only and runs before any part is parsed (AC16): a package that carries a VBA
        // project is refused whether or not its content types admit it.
        if (HasMacroPart(contents))
        {
            return ImportParse.Invalid([FileIssue(MacroProhibited)]);
        }

        if (!contents.TryGetEntry(XlsxFormat.ContentTypesPart, out var contentTypesPart))
        {
            return ImportParse.Invalid([FileIssue(UnsupportedFormat)]);
        }

        if (!TryParsePart(parsing, contentTypesPart, cancellationToken, out var contentTypes, out var contentTypesIssue))
        {
            return ImportParse.Invalid([contentTypesIssue]);
        }

        if (DeclaresMacro(contentTypes))
        {
            return ImportParse.Invalid([FileIssue(MacroProhibited)]);
        }

        if (!TryResolveWorksheet(contents, parsing, cancellationToken, out var worksheet, out var worksheetIssue))
        {
            return ImportParse.Invalid([worksheetIssue]);
        }

        var sharedStrings = ReadSharedStrings(contents, parsing, cancellationToken, out var sharedStringsIssue);

        if (sharedStringsIssue is not null)
        {
            return ImportParse.Invalid([sharedStringsIssue]);
        }

        return ReadSheet(parsing, worksheet, sharedStrings, cancellationToken);
    }

    /// <summary>Walks the archive under the AC16 caps, through the profile, and keeps the parts a workbook needs.</summary>
    /// <param name="body">The archive bytes.</param>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The accepted archive, or the profile failure this reader maps to an AC16 reason.</returns>
    private static Result<ArchiveContents> ReadArchive(byte[] body, IParsing parsing, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(body, writable: false);

        return parsing.ReadArchiveEntries(
            content,
            body.LongLength,
            new ArchiveCaps(
                ImportLimits.MaxXlsxEntries,
                ImportLimits.MaxXlsxExpandedBytes,
                ImportLimits.MaxXlsxCompressionRatio),
            XlsxFormat.IsSelectedPart,
            XlsxFormat.MaxPartBytes,
            cancellationToken);
    }

    /// <summary>True when the archive carries a VBA project part, in any directory and any casing.</summary>
    /// <param name="contents">The accepted archive.</param>
    /// <returns>True for a package with a macro part.</returns>
    private static bool HasMacroPart(ArchiveContents contents)
    {
        foreach (var entry in contents.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var name = entry.Name;
            var separator = name.LastIndexOf('/');
            var fileName = separator < 0 ? name : name[(separator + 1)..];

            if (string.Equals(fileName, MacroPartFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the content types declare a macro-enabled workbook or a VBA project.</summary>
    /// <param name="contentTypes">The parsed content-types part.</param>
    /// <returns>True for a macro-enabled package.</returns>
    private static bool DeclaresMacro(ParsedXmlReport contentTypes)
    {
        using var reader = contentTypes.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "Override", StringComparison.Ordinal))
            {
                continue;
            }

            var contentType = reader.GetAttribute("ContentType");

            if (contentType is not null
                && (contentType.Contains("macroenabled", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("vbaproject", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the first worksheet: the first <c>sheet</c> element's relationship id in the workbook, the matching
    /// relationship's target, and the target normalised against <c>xl/</c> — a target that escapes <c>xl/</c>, an
    /// absolute one or a non-worksheet relationship is refused.
    /// </summary>
    /// <param name="contents">The accepted archive.</param>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <param name="worksheet">The selected worksheet part's bytes.</param>
    /// <param name="issue">The refusal when the method returns false.</param>
    /// <returns>True when the worksheet part was resolved and is one the walk selected.</returns>
    private static bool TryResolveWorksheet(
        ArchiveContents contents,
        IParsing parsing,
        CancellationToken cancellationToken,
        out ReadOnlyMemory<byte> worksheet,
        [NotNullWhen(false)] out ImportIssue? issue)
    {
        worksheet = default;

        if (!contents.TryGetEntry(XlsxFormat.WorkbookPart, out var workbookPart)
            || !contents.TryGetEntry(XlsxFormat.WorkbookRelsPart, out var relationshipsPart))
        {
            issue = FileIssue(UnsupportedFormat);

            return false;
        }

        if (!TryParsePart(parsing, workbookPart, cancellationToken, out var workbook, out issue)
            || !TryParsePart(parsing, relationshipsPart, cancellationToken, out var relationships, out issue))
        {
            return false;
        }

        var relationshipId = FirstSheetRelationshipId(workbook);

        if (relationshipId is null)
        {
            issue = FileIssue(UnsupportedFormat);

            return false;
        }

        var target = WorksheetRelationshipTarget(relationships, relationshipId);

        if (target is null || !contents.TryGetEntry(target, out worksheet))
        {
            issue = FileIssue(UnsupportedFormat);

            return false;
        }

        issue = null;

        return true;
    }

    /// <summary>The relationship id of the first <c>sheet</c> element of the workbook part.</summary>
    /// <param name="workbook">The parsed workbook part.</param>
    /// <returns>The id, or null when the workbook declares no sheet.</returns>
    private static string? FirstSheetRelationshipId(ParsedXmlReport workbook)
    {
        using var reader = workbook.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "sheet", StringComparison.Ordinal))
            {
                continue;
            }

            var id = reader.GetAttribute("id", XlsxFormat.RelationshipsNamespace) ?? reader.GetAttribute("id");

            return string.IsNullOrEmpty(id) ? null : id;
        }

        return null;
    }

    /// <summary>The normalised target of the worksheet relationship <paramref name="relationshipId"/> names.</summary>
    /// <param name="relationships">The parsed workbook relationships part.</param>
    /// <param name="relationshipId">The id the workbook's first sheet element carries.</param>
    /// <returns>The part name inside the package, or null when the relationship is absent, not a worksheet or escapes <c>xl/</c>.</returns>
    private static string? WorksheetRelationshipTarget(ParsedXmlReport relationships, string relationshipId)
    {
        using var reader = relationships.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "Relationship", StringComparison.Ordinal)
                || !string.Equals(reader.GetAttribute("Id"), relationshipId, StringComparison.Ordinal))
            {
                continue;
            }

            var type = reader.GetAttribute("Type");

            if (type is not null && !type.EndsWith("/worksheet", StringComparison.Ordinal))
            {
                return null;
            }

            return ResolveTarget(reader.GetAttribute("Target"));
        }

        return null;
    }

    /// <summary>
    /// Normalises a relationship target against the workbook part's own directory (<c>xl/</c>): an absolute target, a
    /// scheme, a backslash, a fragment or a query is refused, as is one whose <c>..</c> segments climb out of
    /// <c>xl/</c>. Everything else resolves to a normalised part name.
    /// </summary>
    /// <param name="target">The <c>Target</c> attribute as the relationships part spells it.</param>
    /// <returns>The normalised part name, or null when the target escapes <c>xl/</c> or is not a part name.</returns>
    private static string? ResolveTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || target[0] == '/'
            || target.Contains(':', StringComparison.Ordinal)
            || target.Contains('\\', StringComparison.Ordinal)
            || target.Contains('?', StringComparison.Ordinal)
            || target.Contains('#', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = new List<string>(4) { "xl" };

        foreach (var segment in target.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (segments.Count <= 1)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count < 3 ? null : string.Join('/', segments);
    }

    /// <summary>Parses the shared-string table, when the package carries one.</summary>
    /// <param name="contents">The accepted archive.</param>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <param name="issue">The refusal when the table is over its cap or unreadable.</param>
    /// <returns>The table, or an empty one when the package inlines its strings.</returns>
    private static List<string> ReadSharedStrings(
        ArchiveContents contents,
        IParsing parsing,
        CancellationToken cancellationToken,
        out ImportIssue? issue)
    {
        issue = null;

        if (!contents.TryGetEntry(XlsxFormat.SharedStringsPart, out var part) || part.IsEmpty)
        {
            return [];
        }

        if (!TryParsePart(parsing, part, cancellationToken, out var report, out issue))
        {
            return [];
        }

        var strings = new List<string>();
        using var reader = report.OpenReader();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "si", StringComparison.Ordinal))
            {
                continue;
            }

            if (strings.Count >= XlsxFormat.MaxSharedStrings)
            {
                issue = FileIssue(UnsupportedFormat);

                return [];
            }

            strings.Add(ReadStringItemText(reader));
        }

        return strings;
    }

    /// <summary>
    /// Reads the worksheet cells into the contract document and hands it to <see cref="ImportSchema"/> and
    /// <see cref="ImportRows"/>, so an XLSX file is judged by exactly the rules a CSV file is.
    /// </summary>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="worksheet">The worksheet part's bytes.</param>
    /// <param name="sharedStrings">The shared-string table the cells index into.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The rows, the refusals, or the 413 verdict for a file over the row cap.</returns>
    private static ImportParse ReadSheet(
        IParsing parsing,
        ReadOnlyMemory<byte> worksheet,
        List<string> sharedStrings,
        CancellationToken cancellationToken)
    {
        if (!TryParsePart(parsing, worksheet, cancellationToken, out var report, out var issue))
        {
            return ImportParse.Invalid([issue]);
        }

        using var reader = report.OpenReader();

        var issues = new List<ImportIssue>();
        var values = new string?[XlsxFormat.ColumnCount];
        var problems = new List<CellProblem>(2);
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        var document = new Utf8JsonWriter(buffer);
        document.WriteStartObject();
        document.WriteString("format", "authoring-import");
        document.WriteNumber("version", 1);
        document.WriteStartArray("rows");

        var headerRead = false;
        var rowIndex = 0;
        var tooLarge = false;
        var lastRowNumber = 0;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "row", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryReadRowNumber(reader.GetAttribute("r"), lastRowNumber, out var rowNumber))
            {
                // A row without a usable number breaks the file's structure, which is a carrier problem, not a row one.
                ImportSchema.Add(
                    issues,
                    headerRead ? rowIndex + 1 : 0,
                    headerRead ? "/row" : "/header",
                    UnsupportedFormat);

                break;
            }

            lastRowNumber = rowNumber;

            Array.Clear(values);
            problems.Clear();

            var hasValues = ReadRowCells(reader, values, sharedStrings, problems);

            if (!headerRead)
            {
                if (!HeaderMatches(rowNumber, values))
                {
                    ImportSchema.Add(issues, 0, "/header", UnsupportedFormat);

                    break;
                }

                headerRead = true;

                foreach (var problem in problems)
                {
                    ImportSchema.Add(issues, 0, ProblemPath(problem), problem.Reason);
                }

                if (issues.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (!hasValues && problems.Count == 0)
            {
                // A styled but empty row carries no data: it is skipped rather than reported as a row of blank cells.
                continue;
            }

            rowIndex++;

            if (rowIndex > ImportLimits.MaxDataRows)
            {
                tooLarge = true;

                break;
            }

            foreach (var problem in problems)
            {
                ImportSchema.Add(issues, rowIndex, ProblemPath(problem), problem.Reason);
            }

            if (problems.Count == 0 && DecodeRow(values, rowIndex, issues, out var position))
            {
                WriteRow(document, values, position, rowIndex, issues);
            }

            if (buffer.WrittenCount > ImportLimits.MaxBodyBytes)
            {
                // A few-kilobyte workbook can point every cell at one 64 KiB shared string, so the document this reader
                // builds is capped at the body budget it came from: the caps stay one budget, never thirteen times it.
                tooLarge = true;

                break;
            }

            if (issues.Count >= ImportSchema.MaxIssues)
            {
                break;
            }
        }

        if (!headerRead && issues.Count == 0)
        {
            // No row at all: an empty worksheet carries no header.
            ImportSchema.Add(issues, 0, "/header", UnsupportedFormat);
        }

        // The document is closed whatever happened, so the writer is never disposed mid-object.
        document.WriteEndArray();
        document.WriteEndObject();
        document.Flush();
        document.Dispose();

        if (tooLarge)
        {
            return ImportParse.Large();
        }

        if (issues.Count > 0)
        {
            return ImportParse.Invalid(issues);
        }

        var validation = ImportSchema.Validate(buffer.WrittenMemory);

        if (validation.Count > 0)
        {
            return ImportParse.Invalid(validation);
        }

        using var parsed = JsonDocument.Parse(buffer.WrittenMemory, ImportSchema.DocumentOptions);

        return ImportParse.Ok(ImportRows.FromDocument(parsed.RootElement));
    }

    /// <summary>True when the row is row 1 and carries exactly the thirteen column names, in contract order.</summary>
    /// <param name="rowNumber">The sheet row number the row states.</param>
    /// <param name="cells">The row's cells, indexed by column.</param>
    /// <returns>True for a valid header row.</returns>
    private static bool HeaderMatches(int rowNumber, string?[] cells)
    {
        if (rowNumber != 1)
        {
            return false;
        }

        for (var index = 0; index < ImportSchema.ColumnNames.Length; index++)
        {
            if (!string.Equals(cells[index], ImportSchema.ColumnNames[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads one <c>row</c>'s cells into <paramref name="cells"/>, collecting per-cell problems.</summary>
    /// <param name="reader">The reader, positioned on the <c>row</c> start element.</param>
    /// <param name="cells">The cell values by column; the caller cleared it.</param>
    /// <param name="sharedStrings">The shared-string table the cells index into.</param>
    /// <param name="problems">The per-cell problems this call appends to.</param>
    /// <returns>True when at least one cell carried a value.</returns>
    private static bool ReadRowCells(
        XmlReader reader,
        string?[] cells,
        List<string> sharedStrings,
        List<CellProblem> problems)
    {
        var hasValues = false;

        if (reader.IsEmptyElement)
        {
            return false;
        }

        var rowDepth = reader.Depth;
        var nextColumn = 1;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == rowDepth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element
                || reader.Depth != rowDepth + 1
                || !string.Equals(reader.LocalName, "c", StringComparison.Ordinal))
            {
                continue;
            }

            var reference = reader.GetAttribute("r");
            var type = reader.GetAttribute("t");
            var column = reference is null ? nextColumn : ColumnOf(reference);
            var text = ReadCellText(reader, type, sharedStrings, out var isText, out var reason);

            nextColumn = column + 1;

            if (column < 1 || column > ImportSchema.ColumnNames.Length)
            {
                problems.Add(new CellProblem(0, ValueInvalid));

                continue;
            }

            if (reason is not null)
            {
                problems.Add(new CellProblem(column, reason));

                continue;
            }

            if (text is null)
            {
                continue;
            }

            cells[column - 1] = isText ? XlsxFormat.Unescape(text) : text;
            hasValues = true;
        }

        return hasValues;
    }

    /// <summary>Reads one <c>c</c> element's value, resolving the cell type the element declares.</summary>
    /// <param name="reader">The reader, positioned on the <c>c</c> start element.</param>
    /// <param name="type">The cell's <c>t</c> attribute, or null for the numeric default.</param>
    /// <param name="sharedStrings">The shared-string table a <c>t="s"</c> cell indexes into.</param>
    /// <param name="isText">True when the value is text the <c>_xHHHH_</c> escaping applies to.</param>
    /// <param name="reason">The refusal when the cell's content cannot be a value, else null.</param>
    /// <returns>The cell's raw value, or null for an empty or refused cell.</returns>
    private static string? ReadCellText(
        XmlReader reader,
        string? type,
        List<string> sharedStrings,
        out bool isText,
        out string? reason)
    {
        reason = null;
        isText = false;

        string? value = null;
        string? inline = null;
        var cellDepth = reader.Depth;

        if (!reader.IsEmptyElement)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellDepth)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element || reader.Depth != cellDepth + 1)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "v":
                        value = ReadElementText(reader);
                        break;
                    case "is":
                        inline = ReadStringItemText(reader);
                        break;
                    default:
                        // A formula, an extension list or anything else carries no value the table can store.
                        break;
                }
            }
        }

        switch (type)
        {
            case null:
                return value;
            case "s":
                isText = true;

                if (value is null)
                {
                    return null;
                }

                if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0
                    || index >= sharedStrings.Count)
                {
                    isText = false;
                    reason = ValueInvalid;

                    return null;
                }

                return sharedStrings[index];
            case "inlineStr":
                isText = true;

                return inline;
            case "str":
                isText = true;

                return value;
            case "b":
            case "e":
            case "d":
                reason = ValueInvalid;

                return null;
            default:
                return value;
        }
    }

    /// <summary>
    /// Applies the shared cell rules to one row's values — the CSV reader's <c>DecodeRow</c>, over the same columns:
    /// an empty cell is an absent member, a cell over the cap is refused undecoded, text is unneutralised and composed
    /// to NFC, <c>position</c> is an invariant integer, and a <c>steps</c> cell is a nested document that gets the
    /// structural scan of <see cref="ImportSchema.Scan"/>.
    /// </summary>
    /// <param name="cells">The row's cells; replaced by the decoded values.</param>
    /// <param name="rowIndex">The row's 1-based data position.</param>
    /// <param name="issues">The issue list of the file.</param>
    /// <param name="position">The decoded position, or null when the cell is absent.</param>
    /// <returns>True when the row was accepted.</returns>
    private static bool DecodeRow(string?[] cells, int rowIndex, List<ImportIssue> issues, out int? position)
    {
        position = null;
        var accepted = true;

        for (var index = 0; index < ImportSchema.ColumnNames.Length; index++)
        {
            var column = ImportSchema.ColumnNames[index];
            var cell = cells[index];

            if (string.IsNullOrEmpty(cell))
            {
                cells[index] = null;

                continue;
            }

            if (cell.Length > ImportText.MaxCellChars)
            {
                // Refused before it is composed: one cell must not be able to ask for a megabyte-scale string.
                ImportSchema.Add(issues, rowIndex, "/" + column, ValueInvalid);
                accepted = false;

                continue;
            }

            var value = ImportText.Normalise(ImportText.UnneutraliseFromSpreadsheet(cell), out var invalid);

            if (invalid)
            {
                ImportSchema.Add(issues, rowIndex, "/" + column, "invalid_text");
                accepted = false;

                continue;
            }

            if (string.Equals(column, "position", StringComparison.Ordinal))
            {
                if (value is null
                    || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    ImportSchema.Add(issues, rowIndex, "/position", ValueInvalid);
                    accepted = false;

                    continue;
                }

                position = number;
                cells[index] = null;

                continue;
            }

            cells[index] = value;
        }

        if (accepted && cells[StepsColumn] is { } steps)
        {
            // A `steps` cell is a nested JSON document, so it gets the same structural, duplicate-member and
            // storable-text scan the JSON body gets — a 422 rather than a 500 from the schema engine.
            var cellIssues = ImportSchema.Scan(Encoding.UTF8.GetBytes(steps));

            if (cellIssues.Count > 0)
            {
                var reason = string.Equals(cellIssues[0].Reason, "invalid_text", StringComparison.Ordinal)
                    ? "invalid_text"
                    : "steps_invalid";

                ImportSchema.Add(issues, rowIndex, "/steps", reason);
                accepted = false;
            }
        }

        return accepted;
    }

    /// <summary>Writes one accepted row as an object of the contract document.</summary>
    /// <param name="document">The document writer, positioned inside the <c>rows</c> array.</param>
    /// <param name="values">The decoded cells.</param>
    /// <param name="position">The decoded <c>position</c>.</param>
    /// <param name="rowIndex">The 1-based row index, used only if the steps cell turns out to be unparseable.</param>
    /// <param name="issues">The issue list.</param>
    private static void WriteRow(
        Utf8JsonWriter document,
        string?[] values,
        int? position,
        int rowIndex,
        List<ImportIssue> issues)
    {
        JsonDocument? steps = null;

        if (values[StepsColumn] is { } stepsText)
        {
            try
            {
                steps = JsonDocument.Parse(stepsText, ImportSchema.DocumentOptions);
            }
            catch (JsonException)
            {
                ImportSchema.Add(issues, rowIndex, "/steps", "steps_invalid");

                return;
            }
            catch (InvalidOperationException)
            {
                ImportSchema.Add(issues, rowIndex, "/steps", "invalid_text");

                return;
            }
        }

        using (steps)
        {
            document.WriteStartObject();

            for (var index = 0; index < ImportSchema.ColumnNames.Length; index++)
            {
                var column = ImportSchema.ColumnNames[index];

                if (string.Equals(column, "position", StringComparison.Ordinal))
                {
                    if (position is { } number)
                    {
                        document.WriteNumber("position", number);
                    }

                    continue;
                }

                if (values[index] is not { } value)
                {
                    continue;
                }

                if (index == StepsColumn && steps is not null)
                {
                    document.WritePropertyName("steps");
                    steps.RootElement.WriteTo(document);

                    continue;
                }

                document.WriteString(column, value);
            }

            document.WriteEndObject();
        }
    }

    /// <summary>Reads the text of an element that carries text only (a <c>v</c> or a <c>t</c>).</summary>
    /// <param name="reader">The reader, positioned on the element.</param>
    /// <returns>The element's text, whitespace included.</returns>
    private static string ReadElementText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var depth = reader.Depth;
        StringBuilder? text = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == depth)
                {
                    break;
                }

                continue;
            }

            if (reader.Depth != depth + 1)
            {
                continue;
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    text ??= new StringBuilder();
                    text.Append(reader.Value);
                    break;
                default:
                    break;
            }
        }

        return text?.ToString() ?? string.Empty;
    }

    /// <summary>Reads a string item — an <c>si</c> or an <c>is</c> — concatenating its text runs and ignoring phonetics.</summary>
    /// <param name="reader">The reader, positioned on the string item's start element.</param>
    /// <returns>The item's text.</returns>
    private static string ReadStringItemText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var itemDepth = reader.Depth;
        var phoneticDepth = -1;
        StringBuilder? text = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == itemDepth)
                {
                    break;
                }

                if (reader.Depth == phoneticDepth)
                {
                    phoneticDepth = -1;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (string.Equals(reader.LocalName, "rPh", StringComparison.Ordinal))
            {
                if (!reader.IsEmptyElement)
                {
                    phoneticDepth = reader.Depth;
                }

                continue;
            }

            if (phoneticDepth >= 0)
            {
                continue;
            }

            if (string.Equals(reader.LocalName, "t", StringComparison.Ordinal))
            {
                text ??= new StringBuilder();
                text.Append(ReadElementText(reader));
            }
        }

        return text?.ToString() ?? string.Empty;
    }

    /// <summary>Parses one part through the profile, refusing an empty part as an unsupported file.</summary>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="part">The part's bytes.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <param name="report">The validated part.</param>
    /// <param name="issue">The refusal when the method returns false.</param>
    /// <returns>True when the part was validated.</returns>
    private static bool TryParsePart(
        IParsing parsing,
        ReadOnlyMemory<byte> part,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out ParsedXmlReport? report,
        [NotNullWhen(false)] out ImportIssue? issue)
    {
        report = null;

        if (part.IsEmpty)
        {
            issue = FileIssue(UnsupportedFormat);

            return false;
        }

        using var stream = StreamOver(part);
        var parsed = parsing.ParseXmlReport(stream, part.Length, cancellationToken);

        if (!parsed.IsSuccess)
        {
            issue = MapFailure(parsed.Error!);

            return false;
        }

        report = parsed.Value;
        issue = null;

        return true;
    }

    /// <summary>A read-only stream over the profile's buffer, without copying it.</summary>
    /// <param name="part">The part's bytes.</param>
    /// <returns>A stream the caller disposes.</returns>
    private static MemoryStream StreamOver(ReadOnlyMemory<byte> part) =>
        MemoryMarshal.TryGetArray(part, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(part.ToArray(), writable: false);

    /// <summary>Reads one row's stated number, or the next number when the row omits it.</summary>
    /// <param name="stated">The <c>r</c> attribute, or null.</param>
    /// <param name="previous">The number of the previous row.</param>
    /// <param name="rowNumber">The row's number.</param>
    /// <returns>False when the attribute is not a positive integer.</returns>
    private static bool TryReadRowNumber(string? stated, int previous, out int rowNumber)
    {
        rowNumber = previous + 1;

        if (stated is null)
        {
            return true;
        }

        if (!int.TryParse(stated, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            return false;
        }

        rowNumber = parsed;

        return true;
    }

    /// <summary>The 1-based column of an A1-style cell reference; 0 when it carries no letters, over the cap above 13.</summary>
    /// <param name="reference">The <c>r</c> attribute of a cell.</param>
    /// <returns>The column index, or 0 for a reference without letters.</returns>
    private static int ColumnOf(string reference)
    {
        var column = 0;
        var index = 0;

        while (index < reference.Length && char.IsAsciiLetter(reference[index]))
        {
            column = (column * 26) + (char.ToUpperInvariant(reference[index]) - 'A' + 1);
            index++;

            if (column > 16_384)
            {
                return ImportSchema.ColumnNames.Length + 1;
            }
        }

        return index == 0 ? 0 : column;
    }

    /// <summary>The fixed pointer of one cell problem: a column's name, or the marker for a cell past the table.</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The pointer.</returns>
    private static string ProblemPath(CellProblem problem)
    {
        if (problem.Column < 1 || problem.Column > ImportSchema.ColumnNames.Length)
        {
            return ExtraColumnPath;
        }

        return "/" + ImportSchema.ColumnNames[problem.Column - 1];
    }

    /// <summary>A file-level refusal with the fixed <c>/file</c> pointer (S3: no name, no path, no value).</summary>
    /// <param name="reason">The AC13 or AC16 reason.</param>
    /// <returns>The issue.</returns>
    private static ImportIssue FileIssue(string reason) => new(null, XlsxFormat.FilePath, reason);

    /// <summary>Maps a parser-profile failure onto the AC16 reason its guard stands for.</summary>
    /// <param name="error">The profile's failure.</param>
    /// <returns>The file-level issue.</returns>
    private static ImportIssue MapFailure(Error error)
    {
        var reason = error.Details is { Count: > 0 } details ? details[0].Reason : null;

        return FileIssue(reason switch
        {
            ParsingFailureReason.SizeExceeded => ZipSizeExceeded,
            ParsingFailureReason.EntryCountExceeded => ZipEntriesExceeded,
            ParsingFailureReason.CompressionRatioExceeded => ZipRatioExceeded,
            ParsingFailureReason.DtdProhibited => DtdProhibited,
            _ => UnsupportedFormat,
        });
    }

    /// <summary>One cell this reader could not turn into a value: its column (0 for one past the table) and the reason.</summary>
    /// <param name="Column">The 1-based column of the table, or 0 for a cell past its last column.</param>
    /// <param name="Reason">The AC13 reason.</param>
    private readonly record struct CellProblem(int Column, string Reason);
}
