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
/// The write half of <see cref="ParseRawReportHandler"/>: the event rows, the deterministic identities, the one
/// <c>unnest</c> statement that inserts them and the slot rows. Kept beside the handler it serves so the pipeline's
/// shape stays readable in one file and the SQL stays in one place.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>
    /// The event rows and their attempt inputs, serialized once before the write scope opens: the JSON parameter of
    /// the one insert, the identity and hash of every row for the replay comparison, and the declaration the slot
    /// states.
    /// </summary>
    /// <param name="Events">One entry per event, in document order.</param>
    /// <param name="Json">The rows as one <c>jsonb</c> parameter (the recordset's column names).</param>
    /// <param name="MaxSequence">The highest sequence of the set (the slot's declared high-water mark).</param>
    private sealed record SerializedEventSet(SerializedEvent[] Events, string Json, long MaxSequence)
    {
        /// <summary>How many events the report declares it sent (the slot's declared count).</summary>
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

    /// <summary>
    /// Builds every row of the report, its substantive payload hash and its attempt input, so the write transaction
    /// only carries parameters.
    /// </summary>
    /// <param name="artifactId">The report artifact every event references.</param>
    /// <param name="producerId">The synthetic producer id.</param>
    /// <param name="prepared">The parser pair the events are stamped with.</param>
    /// <param name="cases">The mapped cases with a resolved key.</param>
    /// <returns>The serialized set and its declaration.</returns>
    private static SerializedEventSet SerializeEvents(
        Guid artifactId,
        string producerId,
        Prepared prepared,
        MappedCase[] cases)
    {
        var parser = prepared.ParserId + "/" + prepared.Version;
        var events = new SerializedEvent[cases.Length];
        var rows = new EventRow[cases.Length];

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
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
                EventIdFor(producerId, item.Sequence),
                producerId,
                item.Sequence,
                item.ExecutionKey,
                item.RawStatus,
                item.Normalized,
                item.DurationMs,
                CanonicalHash.Compute(hashDocument),
                payload,
                [artifactId]);

            // A passed/failed/error/skipped finish is exactly the attempt the plan records; a duration past the
            // attempt column's int range is dropped rather than truncated (the event row keeps the exact value).
            var duration = item.DurationMs is >= 0 and <= int.MaxValue ? (int)item.DurationMs.Value : (int?)null;
            events[index] = new SerializedEvent(
                rows[index],
                string.IsNullOrEmpty(item.RawStatus)
                    ? null
                    : new CaseAttemptInput(item.ExecutionKey, AttemptNo: 1, item.RawStatus, DurationMs: duration));
        }

        // A report that maps nothing still hashes and serializes, so its empty set is decided by the write half rather
        // than thrown out of the mapping; the high-water mark of nothing is 0, never an exception.
        var maxSequence = cases.Length == 0 ? 0L : cases.Max(item => (long)item.Sequence);

        return new SerializedEventSet(events, JsonSerializer.Serialize(rows), maxSequence);
    }

    /// <summary>The event ids as the <c>jsonb_to_recordset</c> argument the stored-hash read takes.</summary>
    /// <param name="eventIds">The ids of the rows this attempt could not insert.</param>
    /// <returns>A JSON array of <c>{"event_id": …}</c> objects.</returns>
    private static string SerializeEventIds(IEnumerable<Guid> eventIds) =>
        JsonSerializer.Serialize(eventIds.Select(eventId => new { event_id = eventId }));

    /// <summary>The deterministic event id of one (producer, sequence) pair, so a replay hashes to the same identity.</summary>
    /// <param name="producerId">The synthetic producer id.</param>
    /// <param name="sequence">The testcase's document position.</param>
    /// <returns>A GUID derived from the pair.</returns>
    private static Guid EventIdFor(string producerId, long sequence)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(
            Encoding.UTF8.GetBytes(producerId + ":" + sequence.ToString(CultureInfo.InvariantCulture)),
            digest);
        return new Guid(digest[..16]);
    }

    /// <summary>
    /// The run's state, its project's state and its late window, read under the run's own lock (the same admission facts
    /// the event batch route reads, plus the lock the synthetic slot's shard index needs).
    /// </summary>
    private const string RunGateSql = """
        select r.state as "RunState",
               p.status as "ProjectState",
               (r.state = 'incomplete'
                and now() <= r.collection_deadline_at + make_interval(mins => r.late_window_minutes)) as "LateWindowOpen"
          from public.run r
          join public.project p on p.org_id = r.org_id and p.id = r.project_id
         where r.org_id = @org_id
           and r.id = @run_id
         for no key update of r
        """;

    /// <summary>The next free shard index of the run (1 when the run has no slot yet).</summary>
    private const string NextShardIndexSql = """
        select coalesce(max(shard_index), 0) + 1
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
        """;

    /// <summary>
    /// The synthetic producer's slot, written closed and declared in one statement: no producer finalizes a report, so
    /// the reconciler must see this shard's declaration at once. A replay's existing row answers the conflict and stays
    /// as it is.
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

    /// <summary>The synthetic slot's high-water mark (the column grant adds for the ingest path).</summary>
    private const string UpdateSlotSql = """
        update public.producer_slot
           set last_sequence = greatest(last_sequence, @last_sequence)
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
        """;

    /// <summary>What the read scope resolved before the fetch: everything the parse needs and no live connection.</summary>
    /// <param name="ObjectKey">The artifact's stored key.</param>
    /// <param name="MaxBytes">The effective byte cap (the kind policy and the profile's ceiling, whichever is lower).</param>
    /// <param name="ParserId">The registry's <c>parser_id</c>.</param>
    /// <param name="Version">The registry's <c>version</c>.</param>
    /// <param name="Mapper">The loaded mapper for that pair.</param>
    /// <param name="Plan">The run's planned executions as an identity-hint lookup.</param>
    private sealed record Prepared(
        string ObjectKey,
        long MaxBytes,
        string ParserId,
        string Version,
        IJUnitMapper Mapper,
        PlannedExecutionIndex Plan);

    /// <summary>The admission facts of the run, as the one lock read returns them.</summary>
    /// <param name="RunState">The run's state (0031's closed vocabulary).</param>
    /// <param name="ProjectState">The project's state (<c>active</c>, <c>suspended</c>, <c>deleted</c>).</param>
    /// <param name="LateWindowOpen">True when <c>now()</c> is at or before the run's late-window end.</param>
    private sealed record RunGate(string RunState, string ProjectState, bool LateWindowOpen);

    /// <summary>One event as <c>jsonb_to_recordset</c> reads it (the wire names are the column names).</summary>
    /// <param name="Id">The row id.</param>
    /// <param name="EventId">The producer's id, now the deterministic dedupe identity.</param>
    /// <param name="ProducerId">The synthetic producer id.</param>
    /// <param name="Sequence">The testcase's document position.</param>
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
    /// report gave them (AC1: raw in <c>ext</c>, never re-cased or trimmed).
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
        EventId = 2460,
        Level = LogLevel.Debug,
        Message = "raw report parse skipped artifact {ArtifactId}: {Reason}")]
    private static partial void LogSkipped(ILogger logger, Guid artifactId, string reason);

    [LoggerMessage(
        EventId = 2461,
        Level = LogLevel.Warning,
        Message = "raw report parse found no parser for artifact {ArtifactId}: {Reason}, so no event was written")]
    private static partial void LogUnsupported(ILogger logger, Guid artifactId, string reason);

    [LoggerMessage(
        EventId = 2462,
        Level = LogLevel.Warning,
        Message = "raw report parse refused artifact {ArtifactId} as {Reason}: the document was declared unparseable, so no event was written")]
    private static partial void LogRefused(ILogger logger, Guid artifactId, string reason);

    [LoggerMessage(
        EventId = 2463,
        Level = LogLevel.Warning,
        Message = "raw report parse refused artifact {ArtifactId} as too_large: the stored object is larger than the kind's {MaxBytes}-byte cap, so it was not read")]
    private static partial void LogOversize(ILogger logger, Guid artifactId, long maxBytes);

    [LoggerMessage(
        EventId = 2464,
        Level = LogLevel.Information,
        Message = "raw report parse of artifact {ArtifactId} mapped {Mapped} case(s) and inserted {Inserted} event(s)")]
    private static partial void LogMapped(ILogger logger, Guid artifactId, int mapped, int inserted);

    [LoggerMessage(
        EventId = 2465,
        Level = LogLevel.Warning,
        Message = "raw report parse of artifact {ArtifactId} was refused by the case-attempt writer as {Status}, so no event of the attempt was committed")]
    private static partial void LogAttemptRefused(ILogger logger, Guid artifactId, string status);
}
