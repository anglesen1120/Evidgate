using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

internal static partial class CsvFormat
{
    /// <summary>
    /// Writes one CSV body from the shared row table: header, then one record per row in the order given.
    /// </summary>
    /// <param name="rows">The rows (an exporter orders them; this writer never reorders).</param>
    /// <param name="output">The destination stream; the writer leaves it open.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>A task that completes when the body is written and flushed.</returns>
    internal static async Task WriteAsync(
        IReadOnlyList<ImportRow> rows,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(output);

        // UTF8Encoding(true) is what puts the BOM at the head of the file: the reader of AC14 tolerates it and a
        // spreadsheet needs it to read a Vietnamese title as UTF-8.
        using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 16 * 1024,
            leaveOpen: true);

        var line = new StringBuilder(256);

        line.Append(string.Join(',', ImportSchema.ColumnNames)).Append("\r\n");
        await writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            line.Clear();
            AppendField(line, ImportRows.KindName(row.Kind));
            line.Append(',');
            AppendField(line, row.Key);
            line.Append(',');
            AppendField(line, row.ParentKey);
            line.Append(',');
            AppendField(line, row.ItemKey);
            line.Append(',');
            AppendField(line, row.NodeKind);
            line.Append(',');
            AppendField(line, row.Title);
            line.Append(',');
            AppendField(line, row.Description);
            line.Append(',');
            AppendField(line, row.Precondition);
            line.Append(',');
            AppendField(line, row.StepsJson);
            line.Append(',');
            AppendField(line, row.Priority);
            line.Append(',');
            AppendField(line, row.State);
            line.Append(',');
            AppendField(line, row.Code);
            line.Append(',');
            AppendField(line, row.Position?.ToString(CultureInfo.InvariantCulture));
            line.Append("\r\n");

            await writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends one cell: NFC text, neutralised when a spreadsheet would evaluate it, quoted only when it carries a
    /// delimiter, a quote or a line break.
    /// </summary>
    /// <param name="line">The record being built.</param>
    /// <param name="value">The value, or <c>null</c>/empty for an empty cell.</param>
    private static void AppendField(StringBuilder line, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        // Compose answers the value itself when the process cannot compose it (a noncharacter), so an export never
        // throws on the way out and the file re-imports to the same refusal the value would get.
        var cell = ImportText.NeutraliseForSpreadsheet(ImportText.Compose(value));
        var quote = cell.Contains(',')
            || cell.Contains('"')
            || cell.Contains('\r')
            || cell.Contains('\n');

        if (!quote)
        {
            line.Append(cell);
            return;
        }

        line.Append('"');
        foreach (var character in cell)
        {
            if (character == '"')
            {
                line.Append('"');
            }

            line.Append(character);
        }

        line.Append('"');
    }
}
