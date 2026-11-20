using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

internal static partial class XlsxReader
{
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
        var skippedRows = 0;

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
                // The count is still bounded, because a worksheet of empty rows — about twelve bytes each, up to the
                // part cap — would otherwise be walked to its end before any cap could fire (AC16).
                skippedRows++;

                if (skippedRows > ImportLimits.MaxDataRows)
                {
                    tooLarge = true;

                    break;
                }

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

            nextColumn = column + 1;

            if (column < 1 || column > ImportSchema.ColumnNames.Length)
            {
                // The cell is past the last column, so its value can never enter the table: consume the element without
                // collecting its text, so a 13-column file cannot make this reader materialise a cell of any size.
                ReadCellText(reader, type, sharedStrings, 0, out _, out _);
                problems.Add(new CellProblem(0, ValueInvalid));

                continue;
            }

            var text = ReadCellText(reader, type, sharedStrings, TextCharLimit, out var isText, out var reason);

            if (reason is not null)
            {
                problems.Add(new CellProblem(column, reason));

                continue;
            }

            if (text is null)
            {
                continue;
            }

            if (text.Length > ImportText.MaxCellChars)
            {
                // Refused before it is unescaped or composed: the cap is measured on the text the file carries, so a
                // shared string referenced by every cell cannot ask for a fresh copy past it.
                problems.Add(new CellProblem(column, ValueInvalid));

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
    /// <param name="maxChars">
    /// The most characters to collect from the cell's text, or <c>0</c> for a cell whose value is never used: the
    /// element is consumed either way, so the walk keeps its position without materialising the text.
    /// </param>
    /// <param name="isText">True when the value is text the <c>_xHHHH_</c> escaping applies to.</param>
    /// <param name="reason">The refusal when the cell's content cannot be a value, else null.</param>
    /// <returns>The cell's raw value, or null for an empty or refused cell.</returns>
    private static string? ReadCellText(
        XmlReader reader,
        string? type,
        List<string> sharedStrings,
        int maxChars,
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
                        value = ReadElementText(reader, maxChars);
                        break;
                    case "is":
                        inline = ReadStringItemText(reader, maxChars);
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
}
