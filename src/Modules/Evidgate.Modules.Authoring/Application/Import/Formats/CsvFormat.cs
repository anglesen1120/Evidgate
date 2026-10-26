using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// The RFC 4180 side of 13.1.1-T5 (route 25, AC13/AC14/AC16): a bounded CSV reader and writer over the flat row table, with
/// no package behind them (Q1 — the components are hand-written so the 5 MiB/5 000-row caps, the neutralisation and the
/// text rules are this module's own code rather than a library's defaults).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading is bounded before it is correct.</b> The body cap and the row cap are checked before the work they bound
/// (a 5 000-row file is refused with 413 the moment the 5 001st record starts, never after it is materialised), a single
/// cell over <see cref="ImportText.MaxCellBytes"/> is refused undecoded, and the refusal list stops at
/// <see cref="ImportSchema.MaxIssues"/> entries. The scanner walks the raw bytes, so record and field boundaries are known
/// before any text is decoded — a cell that is not valid UTF-8 refuses its own row and the rest of the file is still
/// read.
/// </para>
/// <para>
/// <b>The reader builds the contract document and validates it.</b> Every accepted record is written into an
/// <c>authoring-import.v1</c> document (empty cell = absent member, <c>position</c> as a number, a <c>steps</c> cell as its
/// parsed object), and that document is validated by <see cref="ImportSchema"/> — the same schema the JSON body is judged
/// by and the same row builder turns both into <see cref="ImportRow"/> values. A rule therefore cannot hold for JSON and
/// not for CSV: a missing title, an unknown position, a priority outside the four values, a node row carrying a
/// <c>code</c>, all answer through the schema with <c>value_invalid</c> at the column's own pointer.
/// </para>
/// <para>
/// <b>Writing is deterministic.</b> The header is the 13 contract columns in order, text is NFC, the file starts with a
/// UTF-8 BOM and every record ends with CRLF, a field is quoted only when it holds a comma, a quote or a line break, a
/// formula cell is prefixed with one apostrophe (AC16) and no timestamp, id or counter of the process appears anywhere —
/// so two exports of an unchanged tree are byte-identical.
/// </para>
/// </remarks>
internal static class CsvFormat
{
    /// <summary>The strict UTF-8 decoder: a byte sequence that is not UTF-8 is a refusal, never a replacement character.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The column index of <c>steps</c>, the one cell whose value is a nested document.</summary>
    private static readonly int StepsColumn = Array.IndexOf(ImportSchema.ColumnNames, "steps");

    private const byte Comma = (byte)',';
    private const byte Quote = (byte)'"';
    private const byte CarriageReturn = (byte)'\r';
    private const byte LineFeed = (byte)'\n';

    /// <summary>
    /// Reads one CSV body into the shared row table, or refuses it with the per-row reasons of AC13/AC16.
    /// </summary>
    /// <param name="body">The raw request body (a UTF-8 BOM is tolerated).</param>
    /// <returns>The parse: rows, issues, or the over-cap marker that the route answers with 413.</returns>
    internal static ImportParse Read(ReadOnlySpan<byte> body)
    {
        if (body.Length > ImportLimits.MaxBodyBytes)
        {
            return ImportParse.Large();
        }

        var content = ImportText.WithoutPreamble(body);
        var issues = new List<ImportIssue>();
        var fields = new List<Field>(ImportSchema.ColumnNames.Length);
        var values = new string?[ImportSchema.ColumnNames.Length];
        var position = 0;
        var rowIndex = 0;
        var headerRead = false;
        var tooLarge = false;

        var buffer = new ArrayBufferWriter<byte>(content.Length + 64);
        var document = new Utf8JsonWriter(buffer);
        document.WriteStartObject();
        document.WriteString("format", "authoring-import");
        document.WriteNumber("version", 1);
        document.WriteStartArray("rows");

        while (ReadRecord(content, ref position, fields))
        {
            // A record with a single empty field is a stray line (a trailing CRLF after a final blank line): it carries no
            // data, so it is skipped rather than refused with a column-count error every spreadsheet export would then
            // produce. Any other mismatch is a refusal below.
            if (fields.Count == 1 && fields[0].Length == 0 && !fields[0].Quoted && !fields[0].Malformed)
            {
                continue;
            }

            if (!headerRead)
            {
                if (!HeaderMatches(content, fields))
                {
                    ImportSchema.Add(issues, 0, "/header", "unsupported_format");
                }

                headerRead = true;
                if (issues.Count > 0)
                {
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

            if (fields.Exists(field => field.Malformed))
            {
                // An unterminated quote, text after a closing quote, or a quote inside an unquoted field: the record's
                // boundaries cannot be trusted, so it is refused whole rather than repaired.
                ImportSchema.Add(issues, rowIndex, "/row", "unsupported_format");
            }
            else if (fields.Count != ImportSchema.ColumnNames.Length)
            {
                ImportSchema.Add(issues, rowIndex, "/row", "value_invalid");
            }
            else if (DecodeRow(content, fields, rowIndex, issues, values, out var rowPosition))
            {
                WriteRow(document, values, rowPosition, rowIndex, issues);
            }

            if (issues.Count >= ImportSchema.MaxIssues)
            {
                break;
            }
        }

        if (!headerRead)
        {
            // No record at all: an empty body, or one that only carried a BOM.
            ImportSchema.Add(issues, 0, "/header", "unsupported_format");
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

        using var parsed = JsonDocument.Parse(buffer.WrittenMemory, ImportSchema.DocumentOptions);

        var validation = ImportSchema.Validate(parsed.RootElement);
        if (validation.Count > 0)
        {
            return ImportParse.Invalid(validation);
        }

        return ImportParse.Ok(ImportRows.FromDocument(parsed.RootElement));
    }

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

    /// <summary>
    /// The header of the file must be exactly the 13 contract columns, in order and case-sensitive; anything else (a
    /// missing, unknown, renamed or duplicated column) is one file-level <c>unsupported_format</c> at <c>/header</c>.
    /// </summary>
    /// <param name="content">The body.</param>
    /// <param name="fields">The first record's fields.</param>
    /// <returns>True when the header matches the contract.</returns>
    private static bool HeaderMatches(ReadOnlySpan<byte> content, List<Field> fields)
    {
        if (fields.Count != ImportSchema.ColumnNames.Length)
        {
            return false;
        }

        for (var index = 0; index < fields.Count; index++)
        {
            if (fields[index].Malformed || !TryDecode(content, fields[index], out var name))
            {
                return false;
            }

            if (!string.Equals(name, ImportSchema.ColumnNames[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Decodes one field, removing its quotes and unescaping a doubled quote.</summary>
    /// <param name="content">The body.</param>
    /// <param name="field">The field span.</param>
    /// <param name="text">The decoded text.</param>
    /// <returns>True when the field is valid UTF-8.</returns>
    private static bool TryDecode(ReadOnlySpan<byte> content, Field field, out string text)
    {
        var span = content.Slice(field.Start, field.Length);
        if (field.Quoted && span.Length >= 2)
        {
            span = span[1..^1];
        }

        try
        {
            text = StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }

        // Inside a quoted field a quote is only ever written doubled (RFC 4180), so collapsing every pair is exact.
        if (field.Quoted)
        {
            text = text.Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return true;
    }

    /// <summary>
    /// True when a field's bytes encode a surrogate (a WTF-8 <c>ED A0…BF</c> sequence), which is not valid UTF-8 but is
    /// what a lone surrogate looks like when it is written out: the refusal then names the text rather than the format.
    /// </summary>
    /// <param name="span">The field bytes.</param>
    /// <returns>True when an encoded surrogate was found.</returns>
    private static bool HasSurrogateEncoding(ReadOnlySpan<byte> span)
    {
        for (var index = 0; index + 1 < span.Length; index++)
        {
            if (span[index] == 0xED && span[index + 1] is >= 0xA0 and <= 0xBF)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Advances over one RFC 4180 record: comma-separated fields, quoted fields with doubled quotes and embedded line
    /// breaks, CRLF or LF record ends, and the field spans of the record.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="position">The read position; advanced past the record's terminator.</param>
    /// <param name="fields">Receives the record's field spans.</param>
    /// <returns>False at the end of the body.</returns>
    private static bool ReadRecord(ReadOnlySpan<byte> body, ref int position, List<Field> fields)
    {
        fields.Clear();
        if (position >= body.Length)
        {
            return false;
        }

        var index = position;
        var start = index;
        var quoted = false;
        var malformed = false;
        var insideQuotes = false;

        // A record has at most one field per column plus one: past that the record is over-long, which is a structural
        // refusal rather than a value one, and the collector must stop allocating for a body that is one 5 MiB line of
        // commas (AC16: the caps come before the work they bound).
        var overflowing = false;

        while (index < body.Length)
        {
            var current = body[index];

            if (insideQuotes)
            {
                if (current != Quote)
                {
                    index++;
                    continue;
                }

                if (index + 1 < body.Length && body[index + 1] == Quote)
                {
                    index += 2;
                    continue;
                }

                insideQuotes = false;
                index++;

                // After the closing quote only a delimiter, a record end or the end of the body may follow.
                if (index < body.Length && body[index] is not (Comma or CarriageReturn or LineFeed))
                {
                    malformed = true;
                }

                continue;
            }

            if (current == Quote)
            {
                if (index == start)
                {
                    insideQuotes = true;
                    quoted = true;
                }
                else
                {
                    // A quote inside an unquoted field is not RFC 4180.
                    malformed = true;
                }

                index++;
                continue;
            }

            if (current == Comma)
            {
                overflowing = Add(fields, new Field(start, index - start, quoted, malformed), overflowing);
                quoted = false;
                malformed = false;
                index++;
                start = index;
                continue;
            }

            if (current is CarriageReturn or LineFeed)
            {
                overflowing = Add(fields, new Field(start, index - start, quoted, malformed), overflowing);
                position = current == CarriageReturn && index + 1 < body.Length && body[index + 1] == LineFeed
                    ? index + 2
                    : index + 1;

                return true;
            }

            index++;
        }

        // The body ended without a record terminator: the last record is the field run up to here, and an unterminated
        // quote makes it malformed.
        Add(fields, new Field(start, index - start, quoted, malformed || insideQuotes), overflowing);
        position = index;
        return true;
    }

    /// <summary>
    /// Appends one field unless the record already holds more fields than the table has columns, and marks the record
    /// over-long once it does.
    /// </summary>
    /// <param name="fields">The record's field spans.</param>
    /// <param name="field">The field just scanned.</param>
    /// <param name="overflowing">True when the record was already over its field cap.</param>
    /// <returns>True when the record is over its field cap.</returns>
    private static bool Add(List<Field> fields, Field field, bool overflowing)
    {
        var limit = ImportSchema.ColumnNames.Length + 1;

        if (overflowing)
        {
            return true;
        }

        if (fields.Count >= limit)
        {
            // The record is over-long: keep the last collected field and flag the record malformed, so the refusal
            // names the record (`/row`) rather than a column, and no further field is materialised for this record.
            if (fields.Count > 0)
            {
                fields[^1] = fields[^1] with { Malformed = true };
            }

            return true;
        }

        fields.Add(field);
        return false;
    }

    /// <summary>One field of a record, as a byte span of the body.</summary>
    /// <param name="Start">The offset of the field's first byte (a quote included).</param>
    /// <param name="Length">The field's byte length.</param>
    /// <param name="Quoted">True when the field arrived in quotes.</param>
    /// <param name="Malformed">True when the field breaks RFC 4180 quoting.</param>
    private readonly record struct Field(int Start, int Length, bool Quoted, bool Malformed);
}
