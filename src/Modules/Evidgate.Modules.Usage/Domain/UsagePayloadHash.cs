using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// The digest of what a ledger row bills, so a reused billing key with a different payload is a conflict instead of a
/// silent duplicate. It hashes only billable identity: <c>billing_key</c> is the key itself, and <c>occurred_at</c> is
/// not part of it because a source re-deriving the instant from another clock still bills the same fact.
/// </summary>
internal static class UsagePayloadHash
{
    /// <summary>
    /// Computes the canonical hash of one billable payload. <paramref name="period"/> is the period the request named,
    /// not the open month an issued period may re-date the row to: the move is the ledger's decision, not the payload.
    /// </summary>
    /// <param name="metric">The metric's column value (its wire name).</param>
    /// <param name="projectId">The project, or null for an org-level metric.</param>
    /// <param name="quantity">The measured amount; its scale is normalised before hashing.</param>
    /// <param name="unit">The unit of <paramref name="quantity"/>.</param>
    /// <param name="period">The <c>YYYY-MM</c> period the request asked for.</param>
    internal static string Compute(
        string metric,
        Guid? projectId,
        decimal quantity,
        string unit,
        string period)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(period);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("metric", metric);
            if (projectId is { } id)
            {
                writer.WriteString("project_id", id.ToString("D"));
            }
            else
            {
                writer.WriteNull("project_id");
            }

            writer.WriteString("quantity", FormatQuantity(quantity));
            writer.WriteString("unit", unit);
            writer.WriteString("period", period);
            writer.WriteEndObject();
        }

        return CanonicalHash.Compute(buffer.WrittenSpan);
    }

    /// <summary>
    /// The invariant-culture amount without exponent and without trailing fractional zeros, so the same amount at two
    /// scales (1.50 against 1.5) hashes alike and a stored <c>numeric</c> value compares with the request that wrote it.
    /// </summary>
    /// <param name="quantity">The amount to render.</param>
    private static string FormatQuantity(decimal quantity)
    {
        // One zero: a negative zero and a zero at any scale are the same amount, and no ledger row bills two kinds.
        if (quantity == 0m)
        {
            return "0";
        }

        var text = quantity.ToString(CultureInfo.InvariantCulture);

        // The decimal point protects the integer zeros, so only the fractional tail can be trimmed.
        return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
    }
}
