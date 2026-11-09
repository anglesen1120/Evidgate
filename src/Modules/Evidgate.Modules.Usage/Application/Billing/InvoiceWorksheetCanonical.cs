using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Usage.Application.Billing;

/// <summary>
/// The one canonical text of an invoice worksheet, and the hash that names it.
/// <para>
/// The document is written with fixed properties and canonical decimals, then normalised to RFC 8785 by the
/// shared canonicalizer, so the text names the period, its currency, its ordered rows and the totals and nothing
/// else — no instant of generation anywhere. Two reads of an unchanged period therefore produce the same bytes,
/// which is what makes a re-issue comparable and the hash meaningful.
/// </para>
/// </summary>
/// <remarks>
/// Decimals are written as invariant strings without trailing zeros rather than as JSON numbers: an amount is not
/// a floating-point value, and <c>10.50</c> and <c>10.5</c> are the same amount at two scales. Anything added to
/// the document changes what an already issued statement means, so a hash from another version is never equal —
/// which is why a member only a re-dated row carries is written on that row alone and leaves every other row's
/// bytes as they were.
/// <para>
/// The digest is the bare lowercase hex the statement's <c>content_hash</c> column accepts; it is the same SHA-256
/// over the same canonical bytes as <see cref="CanonicalHash"/>, which prefixes its answer with the 00 §7
/// algorithm name.
/// </para>
/// </remarks>
internal static class InvoiceWorksheetCanonical
{
    /// <summary>The worksheet's canonical text: the hash input and the statement's stored document.</summary>
    /// <param name="worksheet">The worksheet to render.</param>
    internal static string ToJson(InvoiceWorksheet worksheet)
    {
        ArgumentNullException.ThrowIfNull(worksheet);

        return JsonCanonicalizer.Canonicalize(Document(worksheet));
    }

    /// <summary>The lowercase hex SHA-256 of <see cref="ToJson"/>, the worksheet's identity.</summary>
    /// <param name="worksheet">The worksheet to hash.</param>
    internal static string Hash(InvoiceWorksheet worksheet) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson(worksheet))));

    /// <summary>
    /// One decimal in canonical form: invariant culture, no exponent, and no trailing zeros, so <c>10.50</c> and
    /// <c>10.5</c> — the same amount at two scales — are the same text. Zero is always <c>0</c>, never <c>-0</c>.
    /// </summary>
    /// <param name="value">The value to write.</param>
    internal static string Text(decimal value)
    {
        if (value == 0m)
        {
            return "0";
        }

        var text = value.ToString(CultureInfo.InvariantCulture);

        return text.Contains('.', StringComparison.Ordinal) ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    private static string Document(InvoiceWorksheet worksheet)
    {
        var buffer = new ArrayBufferWriter<byte>(4 * 1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("period", worksheet.Period);
            WriteText(writer, "currency", worksheet.Currency);

            writer.WriteStartArray("lines");
            foreach (var line in worksheet.Lines)
            {
                WriteLine(writer, line);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("adjustments");
            foreach (var line in worksheet.Adjustments)
            {
                WriteLine(writer, line);
            }

            writer.WriteEndArray();

            WriteAmount(writer, "line_total", worksheet.LineTotal);
            WriteAmount(writer, "adjustment_total", worksheet.AdjustmentTotal);
            WriteAmount(writer, "total", worksheet.Total);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteLine(Utf8JsonWriter writer, WorksheetLine line)
    {
        writer.WriteStartObject();
        writer.WriteString("id", line.Id.ToString("D", CultureInfo.InvariantCulture));
        WriteText(writer, "project_id", line.ProjectId?.ToString("D", CultureInfo.InvariantCulture));
        writer.WriteString("metric", line.Metric);
        writer.WriteString("quantity", Text(line.Quantity));
        writer.WriteString("unit", line.Unit);
        writer.WriteString("billing_key", line.BillingKey);
        writer.WriteString("occurred_at", line.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        WriteText(writer, "price_ref", line.PriceRef?.ToString("D", CultureInfo.InvariantCulture));
        WriteText(writer, "amount", line.Amount is { } amount ? Text(amount) : null);
        WriteText(writer, "currency", line.Currency);
        WriteText(writer, "adjustment_of", line.AdjustmentOf?.ToString("D", CultureInfo.InvariantCulture));
        WriteText(writer, "reason", line.Reason);

        // Omitted rather than written as null: a row that was never re-dated keeps the document, and therefore
        // the hash, that the same period produced before this field existed.
        WritePresent(writer, "original_period", line.OriginalPeriod);
        writer.WriteEndObject();
    }

    private static void WriteAmount(Utf8JsonWriter writer, string name, decimal? value) =>
        WriteText(writer, name, value is { } amount ? Text(amount) : null);

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);

            return;
        }

        writer.WriteString(name, value);
    }

    /// <summary>
    /// Writes a property only when it has a value. The document's other members write an explicit null, but a
    /// re-dating marker is absent from every row that was never re-dated, so writing it either way would change
    /// what those rows hash to.
    /// </summary>
    private static void WritePresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
