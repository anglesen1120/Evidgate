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
/// <c>unnest</c> statement that inserts them, the slot rows and the reconcile trigger those events raise. Kept beside
/// the handler it serves so the pipeline's shape stays readable in one file and the SQL stays in one place.
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

            // The attempt carries the normalized spelling: the generic writer folds an unknown raw word such as
            // pytest's "failure" to error, while the event keeps the raw word. A duration past the attempt
            // column's int range is dropped rather than truncated (the event row keeps the exact value).
            var duration = item.DurationMs is >= 0 and <= int.MaxValue ? (int)item.DurationMs.Value : (int?)null;
            events[index] = new SerializedEvent(
                rows[index],
                string.IsNullOrEmpty(item.Normalized)
                    ? null
                    : new CaseAttemptInput(item.ExecutionKey, AttemptNo: 1, item.Normalized, DurationMs: duration));
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
    /// The run's state, its project's state and its late window: the admission facts the event batch route reads, shared
    /// by the write transaction's lock read and the unlocked read the pre-check asks before the fetch.
    /// </summary>
    private const string RunFactsSql =
        """
        select r.state as "RunState",
               p.status as "ProjectState",
               (
        """
        + RunAdmissionPolicy.LateWindowOpenSql
        + """
               ) as "LateWindowOpen",
               r.project_id as "ProjectId"
          from public.run r
          join public.project p on p.org_id = r.org_id and p.id = r.project_id
         where r.org_id = @org_id
           and r.id = @run_id
        """;

    /// <summary>
    /// The same facts under the run's own lock (the same admission facts the event batch route reads, plus the lock the
    /// synthetic slot's shard index needs).
    /// </summary>
    private const string RunGateSql = RunFactsSql + "\nfor no key update of r";

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

    /// <summary>The admission facts of the run, as the locked write read and the unlocked pre-check read return them.</summary>
    /// <param name="RunState">The run's state (0031's closed vocabulary).</param>
    /// <param name="ProjectState">The project's state (<c>active</c>, <c>suspended</c>, <c>deleted</c>).</param>
    /// <param name="LateWindowOpen">True when <c>now()</c> is at or before the run's late-window end.</param>
    /// <param name="ProjectId">The project whose counter the inserted events reserve.</param>
    private sealed record RunGate(string RunState, string ProjectState, bool LateWindowOpen, Guid ProjectId);

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

    [LoggerMessage(
        EventId = 2466,
        Level = LogLevel.Warning,
        Message = "raw report refusal for artifact {ArtifactId} left no raw_report row: the report's producer kind cannot be stored, so the refusal is only the task's own outcome")]
    private static partial void LogUnstorableProducerKind(ILogger logger, Guid artifactId);

    /// <summary>
    /// Enqueues the one reconcile trigger this attempt's new events need: the late-evidence shape for a run that already
    /// finished as incomplete, the ordinary slot-closed shape for every other run, so the freeze path sees the events
    /// without waiting for a finalize that a synthetic producer can never trigger.
    /// </summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="producerId">The synthetic producer id of this report.</param>
    /// <param name="events">The serialized set, whose declared pair the trigger names.</param>
    /// <param name="late">True when this evidence arrived after the run finished incomplete.</param>
    /// <param name="batchId">The accepted batch's receipt id, which keys the trigger for both shapes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task EnqueueReconcileAsync(
        ParseRawReportPayload payload,
        string producerId,
        SerializedEventSet events,
        bool late,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var taskType = RunReconcileContract.OutboxTaskType;
        var aggregateType = RunReconcileContract.AggregateType;

        if (late)
        {
            await outbox.EnqueueAsync(
                    taskType,
                    RunReconcileContract.LatePayloadJson(payload.RunId, producerId),
                    aggregateType,
                    payload.RunId,
                    RunReconcileContract.LateBatchIdempotencyKey(payload.RunId, producerId, batchId),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await outbox.EnqueueAsync(
                taskType,
                RunReconcileContract.PayloadJson(payload.RunId, producerId, events.MaxSequence, events.DeclaredCount),
                aggregateType,
                payload.RunId,
                RunReconcileContract.BatchIdempotencyKey(payload.RunId, producerId, batchId),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
