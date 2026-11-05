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
/// The write half of <see cref="ParseRawReportHandler"/>: the event rows, the deterministic identities, the one
/// <c>unnest</c> statement that inserts them and the slot high-water update. Kept beside the handler it serves so the
/// pipeline's shape stays readable in one file and the SQL stays in one place.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>
    /// The event rows as one <c>jsonb</c> parameter (the recordset's column names) — one statement for the whole set.
    /// </summary>
    /// <param name="artifactId">The report artifact every event references.</param>
    /// <param name="producerId">The synthetic producer id.</param>
    /// <param name="prepared">The parser pair the events are stamped with.</param>
    /// <param name="cases">The mapped cases with a resolved key.</param>
    /// <returns>The rows as JSON text.</returns>
    private static string SerializeEvents(
        Guid artifactId,
        string producerId,
        Prepared prepared,
        MappedCase[] cases)
    {
        var parser = prepared.ParserId + "/" + prepared.Version;
        var rows = new EventRow[cases.Length];

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var payload = JsonSerializer.Serialize(new EventPayload(new EventExt(
                "1",
                parser,
                item.Normalized,
                item.Classname,
                item.Name)));

            rows[index] = new EventRow(
                Guid.CreateVersion7(),
                EventIdFor(producerId, item.Sequence),
                producerId,
                item.Sequence,
                item.ExecutionKey,
                item.RawStatus,
                item.Normalized,
                item.DurationMs,
                CanonicalHash.Compute(payload),
                payload,
                [artifactId]);
        }

        return JsonSerializer.Serialize(rows);
    }

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

    /// <summary>The run's state as the late-window and after-cancel rules see it, read under the run's own lock.</summary>
    private const string RunGateSql = """
        select state = 'canceled' as "AfterCancel",
               (state = 'incomplete'
                and now() <= collection_deadline_at + make_interval(mins => late_window_minutes)) as "Late"
          from public.run
         where org_id = @org_id
           and id = @run_id
         for no key update
        """;

    /// <summary>The next free shard index of the run (1 when the run has no slot yet).</summary>
    private const string NextShardIndexSql = """
        select coalesce(max(shard_index), 0) + 1
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
        """;

    /// <summary>The synthetic producer's slot; a replay's existing row answers the conflict and stays as it is.</summary>
    private const string InsertSlotSql = """
        insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id)
        values (@id, @org_id, @run_id, @shard_index, @producer_id)
        on conflict do nothing
        """;

    /// <summary>One statement for the whole event set; the unique (run, producer, sequence) key is the replay dedupe.</summary>
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
        """;

    /// <summary>The synthetic slot's high-water mark (the column grant 0034 adds for the ingest path).</summary>
    private const string UpdateSlotSql = """
        update public.producer_slot
           set last_sequence = greatest(last_sequence, @last_sequence)
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
        """;

    /// <summary>What the read scope resolved before the fetch: everything the parse needs and no live connection.</summary>
    /// <param name="ObjectKey">The artifact's stored key.</param>
    /// <param name="MaxBytes">The kind's effective byte cap.</param>
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

    /// <summary>The run's two acceptance flags, as the one lock read returns them.</summary>
    /// <param name="AfterCancel">True when the run is <c>canceled</c>: the event is post-cancel evidence.</param>
    /// <param name="Late">True when the run finished incomplete and its late window is still open.</param>
    private sealed record RunGate(bool AfterCancel, bool Late);

    /// <summary>One event as <c>jsonb_to_recordset</c> reads it (the wire names are the column names).</summary>
    /// <param name="Id">The row id.</param>
    /// <param name="EventId">The producer's id, now the deterministic dedupe identity.</param>
    /// <param name="ProducerId">The synthetic producer id.</param>
    /// <param name="Sequence">The testcase's document position.</param>
    /// <param name="ExecutionKey">The planned execution the case resolves to.</param>
    /// <param name="RawStatus">The report's raw status, verbatim.</param>
    /// <param name="NormalizedStatus">The mapped status (<c>passed</c>/<c>failed</c>/<c>error</c>/<c>skipped</c>).</param>
    /// <param name="DurationMs">The testcase's duration, or null when the report gave none.</param>
    /// <param name="PayloadHash">The canonical hash of <paramref name="Payload"/>.</param>
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
}
