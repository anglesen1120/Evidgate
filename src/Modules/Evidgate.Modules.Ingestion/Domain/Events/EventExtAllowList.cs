using System.Buffers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Ingestion.Domain.Events;

/// <summary>
/// The ingest-side <c>ext</c> allow-list of 04 §6 ("fields are allow-listed per adapter; secret-looking
/// keys are dropped at the boundary"): the residue that reaches the <c>event.payload</c> jsonb column.
/// The declared keys are the ones the Playwright reporter may emit (04.3.1/04.3.2,
/// <c>packages/adapter-playwright/src/extFilter.ts</c>) plus the capability carrier (04.7.1-T2.3); every
/// other key is unknown and dropped, and a key whose <em>name</em> looks like a credential is dropped at
/// any depth, so a leak attempt can never be stored.
/// </summary>
/// <remarks>
/// Two keys are recognised but never stored: <c>capability</c> is consumed by the first-send seam from
/// the parsed element (04.7.1-T2.3, no re-parse), and <c>normalized</c> is the adapter's own copy of the
/// status the server recomputes (05.3.3), so storing it would create a second, drifting source.
/// </remarks>
internal static class EventExtAllowList
{
    /// <summary>The residue cap of 04 §8 S5; a larger residue rejects the event with <c>ext_too_large</c>.</summary>
    internal const int MaxResidueBytes = 8192;

    /// <summary>The residue of an envelope that carries no <c>ext</c> at all.</summary>
    internal static EventExtResidue Empty { get; } = new("{}", 2);

    /// <summary>
    /// Builds the kept residue of one <c>ext</c> object: the allow-listed keys of <paramref name="type"/>
    /// minus every secret-shaped key name, with the same secret rule applied to every nested member.
    /// </summary>
    /// <param name="type">The event type of the envelope (<see cref="EventBatchParser.TestFinished"/> …).</param>
    /// <param name="ext">The parsed <c>ext</c> object (already known to be an object with a string <c>schema</c>).</param>
    internal static EventExtResidue Build(string type, JsonElement ext)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in ext.EnumerateObject())
            {
                if (!IsAllowed(type, member.Name) || SecretShape.KeyLooksSecret(member.Name))
                {
                    continue;
                }

                writer.WritePropertyName(member.Name);
                WriteValue(writer, member.Value);
            }

            writer.WriteEndObject();
        }

        return new EventExtResidue(Encoding.UTF8.GetString(buffer.WrittenSpan), buffer.WrittenCount);
    }

    /// <summary>
    /// The static per-type allow-list. <c>schema</c> is kept on every type (the envelope schema requires
    /// it); the capability carrier and the adapter's normalised status are recognised but never stored;
    /// the remaining keys are the reporter's declared metadata for the type that carries them.
    /// </summary>
    private static bool IsAllowed(string type, string key) => key switch
    {
        // Every ext carries the schema (contracts/schemas/event-envelope.v1.json requires it).
        "schema" => true,

        // Consumed by the first-send seam from the parsed element, never stored in the payload.
        "capability" => false,

        // The adapter's normalised status is dropped: the server recomputes it (05.3.3).
        "normalized" => false,

        // A producer-asserted expected failure, meaningful only on the attempt's outcome envelope.
        "xfail" => IsType(type, EventBatchParser.TestFinished),

        // run.log markers and warning payloads (04.3.1/04.3.2).
        "marker" or "warn" => IsType(type, EventBatchParser.RunLog),

        // test.finished status detail (04.3.2-T2).
        "skip_reason" or "error_summary" => IsType(type, EventBatchParser.TestFinished),

        // The identity and test-metadata keys of the test envelopes (04.3.1/04.3.2).
        "title" or "file" or "location" or "project" or "engine" or "category" => IsTestType(type),
        "case_id" or "variant_hash" or "target_env" or "identity_conflict" => IsTestType(type),

        // Unknown keys are dropped (04 §6).
        _ => false,
    };

    private static bool IsType(string type, string expected) =>
        string.Equals(type, expected, StringComparison.Ordinal);

    private static bool IsTestType(string type) =>
        IsType(type, EventBatchParser.TestStarted)
        || IsType(type, EventBatchParser.TestStep)
        || IsType(type, EventBatchParser.TestFinished);

    /// <summary>
    /// Writes one value, dropping secret-shaped member names at every depth. The producer's member order
    /// is preserved so the residue is a stable, reviewable image of what was kept.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var member in value.EnumerateObject())
                {
                    if (SecretShape.KeyLooksSecret(member.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(member.Name);
                    WriteValue(writer, member.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                value.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
/// The kept <c>ext</c> residue of one envelope: the compact JSON for <c>event.payload</c> and its
/// UTF-8 length, which is what the 8 KiB cap is measured on.
/// </summary>
/// <param name="Json">The residue as compact JSON; <c>{}</c> when nothing is kept.</param>
/// <param name="ByteCount">The UTF-8 byte length of <paramref name="Json"/>.</param>
internal readonly record struct EventExtResidue(string Json, int ByteCount);
