using System.Buffers;
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
internal static partial class CsvFormat
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
