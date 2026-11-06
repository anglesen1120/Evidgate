using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The event half of <see cref="ReprocessReportHandler"/>: the rows of one reprocessed report, their deterministic
/// identities and hashes, the single <c>unnest</c> insert, the closed synthetic slot those rows need, and the log lines.
/// The row shapes mirror the parse path's because the <c>event</c> table is one contract; the DTOs stay private here
/// until the envelope is extracted for both lanes together.
/// </summary>
internal sealed partial class ReprocessReportHandler
{
    /// <summary>The event rows and their attempt inputs, serialized once before the write scope opens.</summary>
    /// <param name="Events">One entry per event, in this producer's sequence order.</param>
    /// <param name="Json">The rows as one <c>jsonb</c> parameter (the recordset's column names).</param>
    /// <param name="MaxSequence">The highest sequence of the set (the slot's declared high-water mark).</param>
    private sealed record SerializedEventSet(SerializedEvent[] Events, string Json, long MaxSequence)
    {
        /// <summary>How many events this reprocess declares it sent (the slot's declared count).</summary>
        public int DeclaredCount => Events.Length;
    }

    /// <summary>One event row and the <c>case_attempt</c> it writes when it is newly inserted.</summary>
    /// <param name="Row">The row as the insert reads it.</param>
    /// <param name="Attempt">The attempt input of a resolved finish, or null when the report gave no status.</param>
    private sealed record SerializedEvent(EventRow Row, CaseAttemptInput? Attempt);

    /// <summary>One stored row read back to tell a replay from a reused identity.</summary>
    /// <param name="EventId">The stored event's producer id (the deterministic identity).</param>
    /// <param name="PayloadHash">The hash of the stored payload.</param>
    private sealed record StoredEventRow(Guid EventId, string PayloadHash);

    /// <summary>One execution key whose attempt this run already holds.</summary>
    /// <param name="ExecutionKey">The planned execution the existing attempt belongs to.</param>
    private sealed record AttemptedExecutionRow(string ExecutionKey);

    /// <summary>
    /// The executions this set names that already carry an attempt, at most one row per key. The keys are the report's
    /// own, so the read walks the unique <c>(org_id, run_id, execution_key, attempt_no)</c> index instead of every
    /// attempt of the run.
    /// </summary>
    private const string AttemptedExecutionsSql = """
        select distinct ca.execution_key as "ExecutionKey"
          from public.case_attempt ca
         where ca.org_id = @org_id
           and ca.run_id = @run_id
           and ca.execution_key in (
               select key from jsonb_array_elements_text(@execution_keys::jsonb) as key)
        """;

    /// <summary>The next free shard index of the run (1 when the run has no slot yet).</summary>
    private const string NextShardIndexSql = """
        select coalesce(max(shard_index), 0) + 1
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
        """;

    /// <summary>
    /// The synthetic producer's slot, written closed and declared in one statement, with no CI binding: no producer
    /// finalizes this shard, so the reconciler must see its declaration at once, and the shard must never wait for a
    /// token. A replay's existing row answers the conflict and stays as it is.
    /// </summary>
    private const string InsertSlotSql = """
        insert into public.producer_slot (
            id, org_id, run_id, shard_index, producer_id,
            bound_token_jti, last_sequence, closed_at, declared_last_sequence, declared_count)
        values (
            @id, @org_id, @run_id, @shard_index, @producer_id,
            @bound_token_jti, @last_sequence, now(), @declared_last_sequence, @declared_count)
        on conflict do nothing
        """;

    /// <summary>One statement for the whole event set, answering the ids it actually inserted (the replay filter).</summary>
    private const string InsertEventsSql = """
        insert into public.event (
            id, org_id, run_id, event_id, producer_id, sequence, job_id, epoch, execution_key,
            attempt_no, type, raw_status, normalized_status, duration_ms, occurred_at, payload_hash,
            payload, artifact_ids, after_cancel, late)
        select
            x.id, @org_id, @run_id, x.event_id, x.producer_id, x.sequence, null, null, x.execution_key,
            1, 'test.finished', x.raw_status, x.normalized_status, x.duration_ms, null, x.payload_hash,
            x.payload::jsonb, x.artifact_ids, @after_cancel, @late
        from jsonb_to_recordset(@events::jsonb) as x (
            id uuid, event_id uuid, producer_id text, sequence bigint, execution_key text, raw_status text,
            normalized_status text, duration_ms bigint, payload_hash text, payload text, artifact_ids uuid[])
        on conflict do nothing
        returning event_id
        """;

    /// <summary>The stored hash of every row this attempt could not insert, by its deterministic identity.</summary>
    private const string StoredEventHashesSql = """
        select e.event_id as "EventId",
               e.payload_hash as "PayloadHash"
          from jsonb_to_recordset(@events::jsonb) as x (event_id uuid)
          join public.event e
            on e.org_id = @org_id
           and e.run_id = @run_id
           and e.event_id = x.event_id
        """;

    /// <summary>
    /// Builds every row of the reprocessed report, its substantive payload hash and its attempt input, so the write
    /// transaction only carries parameters. Sequences are this producer's own 1..n, which is what keeps a case the
    /// original report already holds from colliding with it.
    /// </summary>
    /// <param name="prepared">The pinned parser release and the synthetic producer id the rows carry.</param>
    /// <param name="cases">The mapped cases with a resolved key.</param>
    /// <returns>The serialized set and its declaration.</returns>
    private static SerializedEventSet SerializeEvents(Prepared prepared, MappedCase[] cases)
    {
        var parser = prepared.ParserId + "/" + prepared.ParserVersion;
        var events = new SerializedEvent[cases.Length];
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

            // The attempt carries the normalized spelling: the generic writer folds an unknown raw word such as
            // pytest's "failure" to error, while the event keeps the raw word. A duration past the attempt column's int
            // range is dropped rather than truncated (the event row keeps the exact value).
            var duration = item.DurationMs is >= 0 and <= int.MaxValue ? (int)item.DurationMs.Value : (int?)null;
            events[index] = new SerializedEvent(
                rows[index],
                string.IsNullOrEmpty(item.Normalized)
                    ? null
                    : new CaseAttemptInput(item.ExecutionKey, AttemptNo: 1, item.Normalized, DurationMs: duration));
        }

        // A report that maps nothing still serializes, so its empty set is decided by the write half rather than thrown
        // out of the mapping; the high-water mark of nothing is 0, never an exception.
        var maxSequence = cases.Length == 0 ? 0L : cases.Length;

        return new SerializedEventSet(events, JsonSerializer.Serialize(rows), maxSequence);
    }

    /// <summary>The event ids as the <c>jsonb_to_recordset</c> argument the stored-hash read takes.</summary>
    /// <param name="eventIds">The ids of the rows this attempt could not insert.</param>
    /// <returns>A JSON array of <c>{"event_id": …}</c> objects.</returns>
    private static string SerializeEventIds(IEnumerable<Guid> eventIds) =>
        JsonSerializer.Serialize(eventIds.Select(eventId => new { event_id = eventId }));

    /// <summary>The execution keys as one <c>jsonb</c> array, which the tenant Dapper path accepts where it refuses a client array.</summary>
    /// <param name="inputs">The attempt inputs whose execution keys the read names.</param>
    /// <returns>A JSON array of strings, each key once.</returns>
    private static string SerializeExecutionKeys(IEnumerable<CaseAttemptInput> inputs) =>
        JsonSerializer.Serialize(inputs.Select(item => item.ExecutionKey).Distinct(StringComparer.Ordinal));

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

    [LoggerMessage(
        EventId = 2474,
        Level = LogLevel.Warning,
        Message = "raw report reprocess {ParsingId} did not commit: the case-attempt writer refused the plan as {Status}")]
    private static partial void LogAttemptRefused(ILogger logger, Guid parsingId, string status);
}
