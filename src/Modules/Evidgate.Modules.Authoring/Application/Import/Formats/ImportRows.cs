using System.Buffers;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// Builds the shared <see cref="ImportRow"/> table out of an accepted import document: the one place the three formats
/// agree on what a row means (13.1.1 AC13/AC14).
/// </summary>
/// <remarks>
/// <para>
/// The JSON reader passes its own body, the CSV and XLSX readers pass the document they built from their cells, so a
/// column rule (an empty cell is an absent member, a text value is NFC, a <c>steps</c> cell is the steps document) is
/// implemented once. Text is composed here with <see cref="AuthoringText.Normalize"/> rather than in each reader, and
/// the unstorable characters are already refused by <see cref="ImportSchema.Scan"/> (JSON) or by
/// <see cref="ImportText.Normalise"/> (the cell formats) before a row is built.
/// </para>
/// <para>
/// An empty string and an absent member are the same thing — <c>null</c> — because a CSV cell cannot tell them apart and
/// AC14 requires the three formats to produce one projection for the same tree. The schema already refuses an empty
/// required value (<c>minLength: 1</c>), so this rule only ever applies to an optional member.
/// </para>
/// </remarks>
internal static class ImportRows
{
    /// <summary>
    /// Builds the rows of an accepted document, in file order with 1-based indexes, so the report of AC13 can name a row.
    /// </summary>
    /// <param name="document">The validated document.</param>
    /// <returns>The rows; a case's <c>steps</c> member is re-serialised to compact JSON text.</returns>
    /// <exception cref="InvalidOperationException">
    /// The document does not have the shape the schema accepted (a caller reached this method without validating it).
    /// </exception>
    internal static IReadOnlyList<ImportRow> FromDocument(JsonElement document)
    {
        if (!document.TryGetProperty("rows", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("the import document carries no rows array");
        }

        var rows = new List<ImportRow>(array.GetArrayLength());
        var index = 1;
        foreach (var element in array.EnumerateArray())
        {
            rows.Add(new ImportRow(
                index++,
                Kind(element),
                Text(element, "key") ?? throw new InvalidOperationException("an import row carries no key"),
                Text(element, "parent_key"),
                Text(element, "item_key"),
                Text(element, "node_kind"),
                Text(element, "title"),
                Text(element, "description"),
                Text(element, "precondition"),
                Steps(element),
                Text(element, "priority"),
                Text(element, "state"),
                Text(element, "code"),
                element.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Number
                    ? position.GetInt32()
                    : null));
        }

        return rows;
    }

    /// <summary>The file value of a row kind (<c>node</c>, <c>item</c>, <c>case</c>).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The column/document value.</returns>
    internal static string KindName(ImportRowKind kind) => kind switch
    {
        ImportRowKind.Node => "node",
        ImportRowKind.Item => "item",
        ImportRowKind.Case => "case",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown import row kind"),
    };

    /// <summary>The row kind of a file value.</summary>
    /// <param name="row">The validated row element.</param>
    /// <returns>The kind.</returns>
    private static ImportRowKind Kind(JsonElement row) => row.GetProperty("kind").GetString() switch
    {
        "node" => ImportRowKind.Node,
        "item" => ImportRowKind.Item,
        "case" => ImportRowKind.Case,
        _ => throw new InvalidOperationException("the import document carries a row kind the schema does not allow"),
    };

    /// <summary>Reads one optional text member, composed to NFC, with <c>null</c> for an absent or empty value.</summary>
    /// <param name="row">The row element.</param>
    /// <param name="member">The member name.</param>
    /// <returns>The NFC text, or <c>null</c>.</returns>
    private static string? Text(JsonElement row, string member)
    {
        if (!row.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : AuthoringText.Normalize(text);
    }

    /// <summary>
    /// Reads a case's <c>steps</c> member as compact JSON text: import stores the document exactly as the writer's
    /// <c>steps</c> snapshot does, so a re-export of an imported case is byte-identical to what was imported.
    /// </summary>
    /// <param name="row">The row element.</param>
    /// <returns>The compact document text, or <c>null</c> for a non-case row.</returns>
    private static string? Steps(JsonElement row)
    {
        if (!row.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            steps.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
