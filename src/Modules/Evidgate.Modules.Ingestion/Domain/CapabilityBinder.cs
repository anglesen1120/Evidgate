using System.Text.Json;
using Evidgate.Modules.Ingestion.Contracts;

namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// Binds a capability document that already passed <c>capability.v1</c> to the
/// <see cref="CapabilityDocument"/> the negotiator and the store read (04.7.1-T2.2). The schema is the
/// gate, so this reads the members it guarantees; <see cref="CapabilityBinding.SupportsJson"/> keeps the
/// <c>supports</c> member's own JSON text, so a flag this server does not know is stored verbatim (AC3).
/// </summary>
internal static class CapabilityBinder
{
    /// <summary>Binds a <c>capability.v1</c>-validated document root.</summary>
    /// <param name="document">The validated document root.</param>
    /// <exception cref="InvalidOperationException">The document carries a member the schema should have refused.</exception>
    internal static CapabilityBinding Bind(JsonElement document)
    {
        var protocol = document.GetProperty("protocol");
        var engine = document.GetProperty("engine");

        var platforms = document.GetProperty("platforms")
            .EnumerateArray()
            .Select(platform => platform.GetString()!)
            .ToArray();

        var supports = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var flag in document.GetProperty("supports").EnumerateObject())
        {
            supports[flag.Name] = flag.Value.GetBoolean();
        }

        var mode = document.GetProperty("mode").GetString() switch
        {
            "full" => AdapterMode.Full,
            "ingest_only" => AdapterMode.IngestOnly,
            var other => throw new InvalidOperationException(
                $"capability.v1 accepted an unknown mode '{other}'"),
        };

        return new CapabilityBinding(
            new CapabilityDocument(
                new ProtocolVersion(
                    ReadVersion(protocol.GetProperty("major")),
                    ReadVersion(protocol.GetProperty("minor"))),
                engine.GetProperty("family").GetString()!,
                engine.GetProperty("version").GetString()!,
                platforms,
                supports,
                mode),
            document.GetProperty("supports").GetRawText());
    }

    /// <summary>
    /// Reads a <c>capability.v1</c> integer that may be spelled <c>1</c>, <c>1.0</c> or <c>1e0</c>:
    /// <see cref="JsonElement.GetInt32"/> only accepts the first, so an integral value in the schema's
    /// int32 range is read from its decimal (or double) form. Anything else is a schema bug, not caller
    /// input — the schema already bounds both members — so it is an exception, never a 422.
    /// </summary>
    /// <param name="element">The <c>protocol.major</c> or <c>protocol.minor</c> element.</param>
    /// <exception cref="InvalidOperationException">The value is not an integral number in the int32 range.</exception>
    private static int ReadVersion(JsonElement element)
    {
        if (element.TryGetInt32(out var value))
        {
            return value;
        }

        if (element.TryGetDecimal(out var number)
            && number == decimal.Truncate(number)
            && number is >= int.MinValue and <= int.MaxValue)
        {
            return (int)number;
        }

        if (element.TryGetDouble(out var real)
            && real == Math.Truncate(real)
            && real is >= int.MinValue and <= int.MaxValue)
        {
            return (int)real;
        }

        throw new InvalidOperationException(
            "capability.v1 accepted a protocol version the binder cannot read");
    }
}

/// <summary>
/// A bound capability document plus the <c>supports</c> member's verbatim JSON text, so the store writes
/// the flags the adapter declared without re-serialising them.
/// </summary>
/// <param name="Document">The bound document the negotiator reads.</param>
/// <param name="SupportsJson">The <c>supports</c> member's own JSON text.</param>
internal sealed record CapabilityBinding(CapabilityDocument Document, string SupportsJson);
