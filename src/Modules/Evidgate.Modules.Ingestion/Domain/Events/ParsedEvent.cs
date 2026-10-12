using System.Text.Json;

namespace Evidgate.Modules.Ingestion.Domain.Events;

/// <summary>
/// One accepted envelope of a batch, as the ingest handler (04.1.1-T1.3) and the `event` insert
/// (04.1.1-T2) need it: the 00 §9.4 members mapped onto the `event` columns, the RFC 8785
/// <c>payload_hash</c> of the event exactly as received, the <c>ext</c> allow-list residue for the
/// <c>payload</c> jsonb column, and the parsed element itself for the first-send seam.
/// </summary>
/// <param name="Index">0-based position of the envelope in <c>events[]</c> (00 §9.2 <c>details[].index</c>).</param>
/// <param name="EventId">Producer-supplied <c>event_id</c>; the <c>(org_id, run_id, event_id)</c> dedupe identity.</param>
/// <param name="ProducerId">The batch's single producer identity (D4).</param>
/// <param name="Sequence">Producer sequence, never reused after a restart.</param>
/// <param name="JobId">Optional <c>job_id</c>.</param>
/// <param name="Epoch">Optional <c>epoch</c>.</param>
/// <param name="ExecutionKey">The 64-hex <c>execution_key</c> of the planned execution.</param>
/// <param name="AttemptNo">The producer's attempt number (1-based).</param>
/// <param name="Type">The closed-set event type (<c>test.started</c> … <c>producer.heartbeat</c>).</param>
/// <param name="RawStatus">The producer's verbatim <c>status</c>; required for <c>test.finished</c>.</param>
/// <param name="DurationMs">Optional <c>duration_ms</c>.</param>
/// <param name="OccurredAt">The producer's <c>occurred_at</c> (runner clock, informative only).</param>
/// <param name="ArtifactIds">Optional <c>artifact_ids</c>, in the producer's order.</param>
/// <param name="PayloadHash"><c>sha256:&lt;lowercase hex&gt;</c> over the RFC 8785 canonical form of the received event element (00 §7).</param>
/// <param name="PayloadJson">The <c>ext</c> allow-list residue as compact JSON; <c>{}</c> when nothing is kept (04 §6).</param>
/// <param name="Xfail">True only for an allow-listed <c>test.finished</c> <c>ext.xfail</c> (04.3.2-T2).</param>
/// <param name="Element">
/// The received event element as an independent clone, so it stays valid after the parse document is
/// disposed (the first-send seam of 04.7.1-T2.3 reads <c>ext.capability</c> from it without a re-parse).
/// </param>
internal sealed record ParsedEvent(
    int Index,
    Guid EventId,
    string ProducerId,
    long Sequence,
    Guid? JobId,
    long? Epoch,
    string ExecutionKey,
    int AttemptNo,
    string Type,
    string? RawStatus,
    long? DurationMs,
    DateTimeOffset OccurredAt,
    IReadOnlyList<Guid> ArtifactIds,
    string PayloadHash,
    string PayloadJson,
    bool Xfail,
    JsonElement Element);
