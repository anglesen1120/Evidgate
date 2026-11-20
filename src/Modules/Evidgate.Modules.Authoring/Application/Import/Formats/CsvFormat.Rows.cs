using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

internal static partial class CsvFormat
{
    /// <summary>
    /// Decodes and normalises one record's cells, adding one issue per offending cell.
    /// </summary>
    /// <param name="content">The body.</param>
    /// <param name="fields">The record's field spans, exactly one per column.</param>
    /// <param name="rowIndex">The 1-based data-row index.</param>
    /// <param name="issues">The issue list.</param>
    /// <param name="values">The reusable cell buffer: the value of each column, <c>null</c> for an empty cell.</param>
    /// <param name="position">The decoded <c>position</c> cell, or <c>null</c>.</param>
    /// <returns>True when every cell was accepted.</returns>
    private static bool DecodeRow(
        ReadOnlySpan<byte> content,
        List<Field> fields,
        int rowIndex,
        List<ImportIssue> issues,
        string?[] values,
        out int? position)
    {
        position = null;
        var accepted = true;

        for (var index = 0; index < fields.Count; index++)
        {
            values[index] = null;
            var field = fields[index];
            var column = ImportSchema.ColumnNames[index];

            if (field.Length == 0)
            {
                continue;
            }

            if (field.Length > ImportText.MaxCellBytes)
            {
                // Refused before it is decoded or allocated: a 5 MiB body must not be able to ask for one 5 MiB string.
                ImportSchema.Add(issues, rowIndex, "/" + column, "value_invalid");
                accepted = false;
                continue;
            }

            if (!TryDecode(content, field, out var text))
            {
                ImportSchema.Add(
                    issues,
                    rowIndex,
                    "/" + column,
                    HasSurrogateEncoding(content.Slice(field.Start, field.Length)) ? "invalid_text" : "unsupported_format");
                accepted = false;
                continue;
            }

            var value = ImportText.Normalise(ImportText.UnneutraliseFromSpreadsheet(text), out var invalid);
            if (invalid)
            {
                ImportSchema.Add(issues, rowIndex, "/" + column, "invalid_text");
                accepted = false;
                continue;
            }

            if (string.Equals(column, "position", StringComparison.Ordinal))
            {
                // A quoted empty cell is the absent position (append) exactly as an unquoted one is: this writer emits
                // the absence as an empty cell, and a tool that quotes an empty cell means the same thing.
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    ImportSchema.Add(issues, rowIndex, "/position", "value_invalid");
                    accepted = false;
                    continue;
                }

                position = number;
                continue;
            }

            // An empty cell is the absent member, as an absent JSON member is: the three formats must agree on the
            // projection of one tree (AC14).
            values[index] = string.IsNullOrEmpty(value) ? null : value;
        }

        if (accepted && values[StepsColumn] is { Length: > 0 } steps)
        {
            // A `steps` cell is a nested JSON document, so it gets the same structural, duplicate-member and
            // storable-text scan the JSON body gets — a 422 `steps_invalid` rather than a 500 from the schema engine on
            // an unreadable member name, and no NUL ever reaches the jsonb column.
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

    /// <summary>Writes one accepted record as a row object of the contract document.</summary>
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
}
