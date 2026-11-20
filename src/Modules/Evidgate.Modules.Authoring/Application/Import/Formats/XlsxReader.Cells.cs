using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

internal static partial class XlsxReader
{
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
    /// <param name="maxChars">
    /// The most characters to collect: text past it is skipped rather than appended, and a single text node is appended
    /// only up to the budget, so one element of a 32 MiB part cannot become a 32 M-character string. The answer is at
    /// most one character past the budget, which is what the caller compares against the cell cap.
    /// </param>
    /// <returns>The element's text, whitespace included.</returns>
    internal static string ReadElementText(XmlReader reader, int maxChars)
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
                    // One text node can be the whole part, so the node is appended up to the budget rather than whole:
                    // the answer stays bounded by the budget it was given, whatever the file's node sizes are.
                    var room = (maxChars + 1) - (text?.Length ?? 0);

                    if (room > 0)
                    {
                        text ??= new StringBuilder(Math.Min(room, 256));

                        var node = reader.Value;

                        text.Append(node.AsSpan(0, Math.Min(node.Length, room)));
                    }

                    break;
                default:
                    break;
            }
        }

        return text?.ToString() ?? string.Empty;
    }

    /// <summary>Reads a string item — an <c>si</c> or an <c>is</c> — concatenating its text runs and ignoring phonetics.</summary>
    /// <param name="reader">The reader, positioned on the string item's start element.</param>
    /// <param name="maxChars">The most characters to collect, as <see cref="ReadElementText"/> applies it.</param>
    /// <returns>The item's text.</returns>
    private static string ReadStringItemText(XmlReader reader, int maxChars)
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
                var room = (maxChars + 1) - (text?.Length ?? 0);

                if (room > 0)
                {
                    text ??= new StringBuilder(Math.Min(room, 256));

                    text.Append(ReadElementText(reader, maxChars - text.Length));
                }
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
