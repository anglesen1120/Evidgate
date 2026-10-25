using System.Buffers;
using System.Text.Json;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// The JSON side of 13.1.1-T5 (route 25, AC13/AC14): reads and writes the <c>authoring-import.v1</c> document of
/// <c>contracts/schemas/authoring-import.v1.json</c>, the shape the CSV and XLSX readers also build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading is a scan, then a schema.</b> The body is scanned first (well-formedness, a 32-level depth cap, a repeated
/// member name, U+0000 and lone surrogates in a name or an escaped value) because a document carrying an unreadable
/// member name would otherwise throw from the engine or from a <c>JsonElement</c> access — the 500 that OI-13-116 closed
/// for <c>AuthoringContent</c> — and then validated against the contract schema through
/// <see cref="ImportSchema"/>. The row cap is checked before the schema runs, so a file of 5 001 rows is the 413 of
/// AC16 rather than 5 001 per-row refusals.
/// </para>
/// <para>
/// <b>No formula neutralisation in either direction.</b> A JSON consumer does not evaluate cell text, so AC16's
/// apostrophe prefix belongs to the CSV and XLSX formats only; neutralising here would store the apostrophe and make the
/// JSON projection differ from the same tree in CSV (AC14).
/// </para>
/// <para>
/// <b>Writing is deterministic and NFC.</b> Members appear in the contract's column order, a member whose value is
/// absent is omitted (so <c>null</c> and an empty CSV cell are one row), the document carries no indentation, no BOM and
/// no timestamp, and a <c>steps</c> member is written as the object it is — so two exports of an unchanged tree are
/// byte-identical.
/// </para>
/// </remarks>
internal static class JsonFormat
{
    /// <summary>
    /// Reads one JSON import body into the shared row table, or refuses it with the per-row reasons of AC13.
    /// </summary>
    /// <param name="body">The raw request body (a UTF-8 BOM is tolerated, though the exporter writes none).</param>
    /// <returns>The parse: rows, issues, or the over-cap marker the route answers with 413.</returns>
    internal static ImportParse Read(ReadOnlySpan<byte> body)
    {
        if (body.Length > ImportLimits.MaxBodyBytes)
        {
            return ImportParse.Large();
        }

        var content = ImportText.WithoutPreamble(body);

        var scanned = ImportSchema.Scan(content);
        if (scanned.Count > 0)
        {
            return ImportParse.Invalid(scanned);
        }

        using var document = Parse(content);
        if (document is null)
        {
            return ImportParse.Invalid([new ImportIssue(null, "/file", "unsupported_format")]);
        }

        var root = document.RootElement;

        // The row cap is the 413 of AC16, so it is answered before the schema's own maxItems (which would report a 422
        // for the same file) and before any row is materialised.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("rows", out var rows)
            && rows.ValueKind == JsonValueKind.Array
            && rows.GetArrayLength() > ImportLimits.MaxDataRows)
        {
            return ImportParse.Large();
        }

        var issues = ImportSchema.Validate(root);
        return issues.Count > 0 ? ImportParse.Invalid(issues) : ImportParse.Ok(ImportRows.FromDocument(root));
    }

    /// <summary>
    /// Writes one JSON import document from the shared row table: the envelope and one object per row, in the order given.
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

        // One buffer, written once: the route exports at most 5 000 rows (the same cap the import accepts), so the body
        // is bounded and a chunked writer would only add a second copy of it.
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "authoring-import");
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("rows");

            foreach (var row in rows)
            {
                WriteRow(writer, row);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        await output.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the body into a document, answering <c>null</c> for input the scan let through but the parser refuses.
    /// </summary>
    /// <remarks>
    /// The span's backing store is not recoverable, so the document parser — which needs contiguous memory — gets one
    /// copy bounded by <see cref="ImportLimits.MaxBodyBytes"/>; there is no span overload to avoid it.
    /// </remarks>
    /// <param name="content">The body without its preamble.</param>
    /// <returns>The document, or <c>null</c>.</returns>
    private static JsonDocument? Parse(ReadOnlySpan<byte> content)
    {
        try
        {
            return JsonDocument.Parse(content.ToArray(), ImportSchema.DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // Only a name or value the scan could not read reaches this: the same file-level text refusal.
            return null;
        }
    }

    /// <summary>Writes one row object: the members that carry a value, in the contract's column order.</summary>
    /// <param name="writer">The document writer, positioned inside the <c>rows</c> array.</param>
    /// <param name="row">The row.</param>
    private static void WriteRow(Utf8JsonWriter writer, ImportRow row)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", ImportRows.KindName(row.Kind));
        WriteText(writer, "key", row.Key);
        WriteText(writer, "parent_key", row.ParentKey);
        WriteText(writer, "item_key", row.ItemKey);
        WriteText(writer, "node_kind", row.NodeKind);
        WriteText(writer, "title", row.Title);
        WriteText(writer, "description", row.Description);
        WriteText(writer, "precondition", row.Precondition);

        if (row.StepsJson is { Length: > 0 } steps)
        {
            writer.WritePropertyName("steps");
            using var document = StepsDocument(steps);
            document.RootElement.WriteTo(writer);
        }

        WriteText(writer, "priority", row.Priority);
        WriteText(writer, "state", row.State);
        WriteText(writer, "code", row.Code);

        if (row.Position is { } position)
        {
            writer.WriteNumber("position", position);
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes one optional text member as NFC, omitting it when the row has no value.</summary>
    /// <param name="writer">The document writer.</param>
    /// <param name="member">The member name (a contract column).</param>
    /// <param name="value">The value.</param>
    private static void WriteText(Utf8JsonWriter writer, string member, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        writer.WriteString(member, AuthoringText.Normalize(value));
    }

    /// <summary>
    /// The parsed <c>steps</c> document of a row. A row whose <c>steps</c> is not a JSON object is a writer bug, not a
    /// caller error, so it fails loudly with a message that names no content (S3).
    /// </summary>
    /// <param name="steps">The row's steps document as JSON text.</param>
    /// <returns>The parsed document, owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">The row's steps text is not a JSON object.</exception>
    private static JsonDocument StepsDocument(string steps)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(steps);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("an import row carries a steps document that is not JSON");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidOperationException("an import row carries a steps value that is not a document");
        }

        return document;
    }
}
