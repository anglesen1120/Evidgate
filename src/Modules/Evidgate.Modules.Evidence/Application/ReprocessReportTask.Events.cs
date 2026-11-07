using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The event half of <see cref="ReprocessReportHandler"/>: the rows of one reprocessed report, their deterministic
/// identities and hashes, and the log lines. The row shapes mirror the parse path's because the <c>event</c> table is one
/// contract; the statements both lanes share live in <c>RawReportWorkerSql</c>.
/// </summary>
internal sealed partial class ReprocessReportHandler
{
    /// <summary>The event rows, serialized once before the write scope opens.</summary>
    /// <param name="Events">One entry per event, in this producer's sequence order.</param>
    /// <param name="Json">The rows as one <c>jsonb</c> parameter (the recordset's column names).</param>
    /// <param name="MaxSequence">The highest sequence of the set (the slot's declared high-water mark).</param>
    private sealed record SerializedEventSet(EventRow[] Events, string Json, long MaxSequence)
    {
        /// <summary>How many events this reprocess declares it sent (the slot's declared count).</summary>
        public int DeclaredCount => Events.Length;
    }

    /// <summary>One stored row read back to tell a replay from a reused identity.</summary>
    /// <param name="EventId">The stored event's producer id (the deterministic identity).</param>
    /// <param name="PayloadHash">The hash of the stored payload.</param>
    private sealed record StoredEventRow(Guid EventId, string PayloadHash);

    /// <summary>
    /// Builds every row of the reprocessed report and its substantive payload hash, so the write transaction only carries
    /// parameters. Sequences are this producer's own 1..n, which is what keeps a case the original report already holds
    /// from colliding with it.
    /// </summary>
    /// <param name="prepared">The pinned parser release and the synthetic producer id the rows carry.</param>
    /// <param name="cases">The mapped cases with a resolved key.</param>
    /// <returns>The serialized set and its declaration.</returns>
    private static SerializedEventSet SerializeEvents(Prepared prepared, MappedCase[] cases)
    {
        var parser = prepared.ParserId + "/" + prepared.ParserVersion;
        var rows = new EventRow[cases.Length];

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var sequence = index + 1L;
            var ext = new EventExt(
                "1",
                parser,
                item.Normalized,
                item.Classname,
                item.Name);
            var payload = JsonSerializer.Serialize(new EventPayload(ext));

            // The hash covers the substantive fields of the event, not just its ext document: a replay that changes an
            // execution key, a status or a duration must not read as the identical evidence it is not.
            var hashDocument = JsonSerializer.Serialize(new EventHashDocument(
                item.ExecutionKey,
                item.RawStatus,
                item.Normalized,
                item.DurationMs,
                ext));

            rows[index] = new EventRow(
                Guid.CreateVersion7(),
                EventIdFor(prepared.ProducerId, sequence),
                prepared.ProducerId,
                sequence,
                item.ExecutionKey,
                item.RawStatus,
                item.Normalized,
                item.DurationMs,
                CanonicalHash.Compute(hashDocument),
                payload,
                [prepared.ArtifactId]);
        }

        // A report that maps nothing still serializes, so its empty set is decided by the write half rather than thrown
        // out of the mapping; the high-water mark of nothing is 0, never an exception.
        var maxSequence = cases.Length == 0 ? 0L : cases.Length;

        return new SerializedEventSet(rows, JsonSerializer.Serialize(rows), maxSequence);
    }

    /// <summary>The event ids as the <c>jsonb_to_recordset</c> argument the stored-hash read takes.</summary>
    /// <param name="eventIds">The ids of the rows this attempt could not insert.</param>
    /// <returns>A JSON array of <c>{"event_id": …}</c> objects.</returns>
    private static string SerializeEventIds(IEnumerable<Guid> eventIds) =>
        JsonSerializer.Serialize(eventIds.Select(eventId => new { event_id = eventId }));

    /// <summary>The deterministic event id of one (producer, sequence) pair, so a replay hashes to the same identity.</summary>
    /// <param name="producerId">The synthetic producer id.</param>
    /// <param name="sequence">This producer's 1-based sequence.</param>
    /// <returns>A GUID derived from the pair.</returns>
    private static Guid EventIdFor(string producerId, long sequence)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(
            Encoding.UTF8.GetBytes(producerId + ":" + sequence.ToString(CultureInfo.InvariantCulture)),
            digest);
        return new Guid(digest[..16]);
    }

    /// <summary>One event as <c>jsonb_to_recordset</c> reads it (the wire names are the column names).</summary>
    /// <param name="Id">The row id.</param>
    /// <param name="EventId">The producer's id, now the deterministic dedupe identity.</param>
    /// <param name="ProducerId">The synthetic producer id.</param>
    /// <param name="Sequence">This producer's 1-based sequence.</param>
    /// <param name="ExecutionKey">The planned execution the case resolves to.</param>
    /// <param name="RawStatus">The report's raw status, verbatim.</param>
    /// <param name="NormalizedStatus">The mapped status (<c>passed</c>/<c>failed</c>/<c>error</c>/<c>skipped</c>).</param>
    /// <param name="DurationMs">The testcase's duration, or null when the report gave none.</param>
    /// <param name="PayloadHash">The canonical hash of the document the row describes.</param>
    /// <param name="Payload">The event's <c>ext</c> document.</param>
    /// <param name="ArtifactIds">The report artifact this event came from.</param>
    private sealed record EventRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("event_id")] Guid EventId,
        [property: JsonPropertyName("producer_id")] string ProducerId,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("execution_key")] string ExecutionKey,
        [property: JsonPropertyName("raw_status")] string RawStatus,
        [property: JsonPropertyName("normalized_status")] string NormalizedStatus,
        [property: JsonPropertyName("duration_ms")] long? DurationMs,
        [property: JsonPropertyName("payload_hash")] string PayloadHash,
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("artifact_ids")] Guid[] ArtifactIds);

    /// <summary>
    /// The document one event's <c>payload_hash</c> covers: the execution key, both statuses, the duration and the
    /// <c>ext</c> object — the fields a replay must keep identical to count as the same evidence.
    /// </summary>
    /// <param name="ExecutionKey">The planned execution the case resolves to.</param>
    /// <param name="RawStatus">The report's raw status, verbatim.</param>
    /// <param name="NormalizedStatus">The mapped status.</param>
    /// <param name="DurationMs">The testcase's duration, or null.</param>
    /// <param name="Ext">The event's extension object.</param>
    private sealed record EventHashDocument(
        [property: JsonPropertyName("execution_key")] string ExecutionKey,
        [property: JsonPropertyName("raw_status")] string RawStatus,
        [property: JsonPropertyName("normalized_status")] string NormalizedStatus,
        [property: JsonPropertyName("duration_ms")] long? DurationMs,
        [property: JsonPropertyName("ext")] EventExt Ext);

    /// <summary>The stored <c>payload</c>: the event envelope's <c>ext</c> object and nothing else.</summary>
    /// <param name="Ext">The extension object.</param>
    private sealed record EventPayload([property: JsonPropertyName("ext")] EventExt Ext);

    /// <summary>
    /// <c>ext</c>: the parser that produced the status, the mapped status, and the case identity hints exactly as the
    /// report gave them (raw in <c>ext</c>, never re-cased or trimmed).
    /// </summary>
    /// <param name="Schema">The envelope schema marker.</param>
    /// <param name="Parser">The <c>&lt;parser_id&gt;/&lt;version&gt;</c> stamp.</param>
    /// <param name="Normalized">The mapped status.</param>
    /// <param name="Classname">The raw <c>classname</c>.</param>
    /// <param name="Name">The raw <c>name</c>.</param>
    private sealed record EventExt(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("parser")] string Parser,
        [property: JsonPropertyName("normalized")] string Normalized,
        [property: JsonPropertyName("classname")] string Classname,
        [property: JsonPropertyName("name")] string Name);

    [LoggerMessage(
        EventId = 2470,
        Level = LogLevel.Debug,
        Message = "raw report reprocess {ParsingId} skipped: {Reason}")]
    private static partial void LogSkipped(ILogger logger, Guid parsingId, string reason);

    [LoggerMessage(
        EventId = 2471,
        Level = LogLevel.Information,
        Message = "raw report reprocess {ParsingId} mapped {Mapped} case(s) and inserted {Inserted} event(s)")]
    private static partial void LogMapped(ILogger logger, Guid parsingId, int mapped, int inserted);

    [LoggerMessage(
        EventId = 2472,
        Level = LogLevel.Warning,
        Message = "raw report reprocess {ParsingId} refused the report as {Reason}: the document is unparseable, so no event was written")]
    private static partial void LogRefused(ILogger logger, Guid parsingId, string reason);

    [LoggerMessage(
        EventId = 2473,
        Level = LogLevel.Warning,
        Message = "raw report reprocess {ParsingId} refused the report as too_large: the stored object is larger than the kind's {MaxBytes}-byte cap, so it was not read")]
    private static partial void LogOversize(ILogger logger, Guid parsingId, long maxBytes);
}
