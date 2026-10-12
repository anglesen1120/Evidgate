using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;
using Evidgate.Modules.Ingestion.Domain.Events;
using Evidgate.Modules.Orchestrator.Contracts;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Ingestion.Application.Commands;

/// <summary>
/// The 04.1.1-T1 hot path: one atomic event batch (<c>POST /v1/runs/{r}/events</c>, 00 §9.3/§9.4).
/// The command carries the route's run id, the raw body bytes and the request id; the handler owns
/// the whole decision list of the task file §5 (edge validation → scope → producer binding →
/// first-send capability → one insert → case attempts → slot/ledger rows) inside the C2 pipeline's
/// tenant transaction, so a refusal leaves no row behind.
/// <para>
/// Not an <c>IIdempotentCommand</c> (D2): the route is exempt from <c>Idempotency-Key</c>
/// (contracts/README.md), the batch carries its own per-event <c>event_id</c> dedupe, and a resend
/// must be answered with the per-event <c>duplicate</c> acks rather than a cached response.
/// </para>
/// </summary>
/// <param name="RunId">The run of the route's <c>{r}</c> segment.</param>
/// <param name="Body">The raw UTF-8 request body, already capped by the endpoint (1 MiB).</param>
/// <param name="RequestId">The <c>X-Request-Id</c> the host echoed; the batch's audit row carries it.</param>
internal sealed record IngestEventBatch(
    Guid RunId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string RequestId) : ICommand<Result<BatchAckResult>>;

/// <summary>The 200 body of <c>POST /v1/runs/{r}/events</c> (00 §9.4, D3): the batch row, its counts and one ack per event in request order.</summary>
/// <param name="BatchId">The <c>event_batch</c> row written by this accepted batch.</param>
/// <param name="Accepted">How many events were newly committed.</param>
/// <param name="Duplicates">How many events were already on file with the identical canonical payload.</param>
/// <param name="Acks">One ack per event of the request, in request order.</param>
internal sealed record BatchAckResult(
    Guid BatchId,
    int Accepted,
    int Duplicates,
    IReadOnlyList<EventAck> Acks);

/// <summary>One event's per-event ack (00 §9.4): <c>accepted</c> when the row was committed by this batch, <c>duplicate</c> when it was already on file.</summary>
/// <param name="Index">The 0-based position of the event in the request (00 §9.2 detail index).</param>
/// <param name="EventId">The event's client-assigned id.</param>
/// <param name="Ack"><c>accepted</c> or <c>duplicate</c>.</param>
internal sealed record EventAck(int Index, Guid EventId, string Ack);

/// <summary>
/// Handler of <see cref="IngestEventBatch"/> (04.1.1-T1, task file §5/§8): the whole batch is validated,
/// canonicalised and committed atomically, and the ack of an event exists only after the commit.
/// <para>
/// Audit: an ordinary batch declares the rule's <c>[AuditedAction]</c> and then suppresses the row itself.
/// The ingest route is the highest-frequency mutation in the system (500 events per call) and carries no
/// security decision of its own, so the durable record of the batch is its <c>event_batch</c> row
/// (actor-visible counts, bytes, request id) plus the <c>usage.batch_accepted</c> outbox row — one audit
/// row per accepted batch would flood the trail for no reviewer value (00 §5.7 P5). The one exception is
/// the org-level <c>adapter_capability</c> write of a first-send batch: it is audited exactly like the
/// 04.7.1-T2.3 route's own registration, so the suppression decision is taken only after the capability
/// step (S-1).
/// </para>
/// </summary>
[AuditedAction("event.batch.ingest")]
[Permission(Permissions.Event.Write)]
internal sealed class IngestEventBatchHandler(
    Lazy<ContractSchemaSet> schemas,
    FirstSendCapabilityService firstSend,
    IRunEventScope runScopes,
    IProducerRegistry producers,
    ICaseAttemptWriter attempts,
    IOutboxWriter outbox,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IAuditTrail audit) : ICommandHandler<IngestEventBatch, Result<BatchAckResult>>
{
    /// <summary>The per-event ack of a row this batch committed (00 §9.4).</summary>
    internal const string AcceptedAck = "accepted";

    /// <summary>The per-event ack of a row already on file with the identical canonical payload (00 §9.4).</summary>
    internal const string DuplicateAck = "duplicate";

    /// <summary>The reason the parser reports for a body over the route's transport cap (04.1.1-T1.2).</summary>
    internal const string TooLargeReason = "too_large";

    /// <summary>The V1 usage metric name (04.8.1 §4: one metric, <c>event</c>).</summary>
    internal const string UsageMetric = "event";

    /// <summary>The outbox task type of the usage row (04.8.1 §4, 04.1.1 §7).</summary>
    internal const string UsageTaskType = "usage.batch_accepted";

    /// <summary>One event's row of the <c>jsonb_to_recordset</c> insert; property names are the column names.</summary>
    private sealed record EventRow(
        Guid id,
        Guid event_id,
        string producer_id,
        long sequence,
        Guid? job_id,
        long? epoch,
        string execution_key,
        int attempt_no,
        string type,
        string? raw_status,
        string? normalized_status,
        long? duration_ms,
        DateTimeOffset occurred_at,
        string payload_hash,
        string payload,
        IReadOnlyList<Guid> artifact_ids);

    /// <summary>One already-stored event of the batch, read back to tell a duplicate from a reused sequence.</summary>
    private sealed record StoredEvent(Guid EventId, string PayloadHash);

    /// <summary>One producer slot's high-water mark of this batch.</summary>
    private sealed record SlotMax(string producer_id, long max_sequence);

    /// <summary>One event's attempt slot, index-aligned with <see cref="CaseAttemptInput"/>.</summary>
    private sealed record AttemptSlot(int EventIndex, CaseAttemptInput Input);

    /// <inheritdoc />
    public async ValueTask<Result<BatchAckResult>> Handle(
        IngestEventBatch command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(IngestEventBatch));

        // (a) The edge cap comes from the schema itself (event-envelope.v1's x-max-bytes), so the limit has
        // one home. The parser enforces the per-event and per-batch rules below it (00 §5.5 C6).
        if (schemas.Value.MaxBytes(ContractSchemaKeys.EventEnvelope) is { } maxBytes
            && command.Body.Length > maxBytes)
        {
            return TooLarge();
        }

        // (b) One parse of the body: EventBatchParser is the hand-written edge validator of this route and
        // already reports the closed type set, the member shapes and the per-event details with their index,
        // so a second Corvus pass over the same ≤1 MiB body would add no accepted input to the hot path
        // (04.1.1 §8; the Ajv/Corvus parity corpus still governs the published schema).
        var parsed = EventBatchParser.Parse(command.Body);
        if (!parsed.IsAccepted)
        {
            return parsed.Errors.Any(error => error.Reason == TooLargeReason)
                ? TooLarge()
                : Result.Failure<BatchAckResult>(Error.Unprocessable(
                    ApiErrorCode.EventInvalid,
                    "the event batch was rejected",
                    parsed.Errors));
        }

        var events = parsed.Events;
        var principal = principals.Current;
        if (principal is null || string.IsNullOrEmpty(principal.CredentialId))
        {
            // Fail closed: the batch's producer binding needs the caller's token identity (T9).
            return Result.Failure<BatchAckResult>(new Error(
                ApiErrorCode.Unauthenticated,
                "no authenticated producer could be resolved",
                StatusCodes.Status401Unauthorized));
        }

        var producerId = events[0].ProducerId;

        // (c) Scope: one read answers the run's existence, its state and the planned execution keys (T90).
        var distinctKeys = events.Select(e => e.ExecutionKey).ToHashSet(StringComparer.Ordinal);
        var scope = await runScopes.ReadAsync(command.RunId, distinctKeys, cancellationToken).ConfigureAwait(false);
        if (scope is null)
        {
            return Result.Failure<BatchAckResult>(Error.NotFound("no such run"));
        }

        // The token's project scope is the boundary of a ci_producer credential (FR-02-7, 00 §9.1); the
        // project's own lifecycle state mirrors 06.1.2's upload-url decision (suspended refuses writes,
        // deleted is the 06.4.4 conflict).
        if (principal.ProjectScope is { } tokenProject && tokenProject != scope.ProjectId)
        {
            return Result.Failure<BatchAckResult>(Error.Denied(
                ApiErrorCode.ProjectScopeMismatch,
                "the run belongs to a project outside the credential's scope"));
        }

        if (string.Equals(scope.ProjectState, "deleted", StringComparison.Ordinal))
        {
            return Result.Failure<BatchAckResult>(Error.Conflict(
                ApiErrorCode.ProjectDeleting,
                "the project is being deleted"));
        }

        if (string.Equals(scope.ProjectState, "suspended", StringComparison.Ordinal))
        {
            return Result.Failure<BatchAckResult>(Error.Denied(
                ApiErrorCode.OrgSuspended,
                "the project is suspended"));
        }

        // D6 (fail closed): a complete or incomplete run has closed its event window; a canceled run still
        // accepts events and marks them after_cancel (T12). The late-window write is 04.1.1-T4.2's.
        var afterCancel = string.Equals(scope.RunState, "canceled", StringComparison.Ordinal);
        if (scope.RunState is "complete" or "incomplete")
        {
            return Result.Failure<BatchAckResult>(Error.Conflict(
                ApiErrorCode.RunClosed,
                "the run no longer accepts events"));
        }

        // (c) Every execution_key must be a planned execution of the run; all failing indices are named (T10).
        var unknown = events.Where(e => !scope.PlannedKeys.Contains(e.ExecutionKey)).ToList();
        if (unknown.Count > 0)
        {
            return Result.Failure<BatchAckResult>(Error.Unprocessable(
                ApiErrorCode.UnknownExecutionKey,
                "the batch names execution keys that are not planned executions of the run",
                [.. unknown.Select(e => new ErrorDetail($"/events/{e.Index}/execution_key", "unknown_execution_key", e.Index))]));
        }

        // (d) One bind-or-check per batch: the first accepted write of a slot binds it to this token (T9).
        var bound = await producers
            .BindOrCheckAsync(command.RunId, producerId, principal.CredentialId, principal.CiRun?.ToBindingReference(), cancellationToken)
            .ConfigureAwait(false);
        if (!bound.IsSuccess)
        {
            return Result.Failure<BatchAckResult>(bound.Error!);
        }

        // (e) The first-send capability seam (04.7.1-T2.3, D11): a sequence-1 run.log carrier registers its
        // capability before any event row, and a refusal rolls the whole batch back. The envelope is already
        // parsed, so the service runs on the JsonElement and the body is never parsed twice.
        Guid? capabilityId = null;
        if (events[0] is { Type: EventBatchParser.RunLog, Sequence: 1 } carrier
            && carrier.Element.ValueKind == JsonValueKind.Object
            && carrier.Element.TryGetProperty("ext", out var carrierExt)
            && carrierExt.ValueKind == JsonValueKind.Object
            && carrierExt.TryGetProperty("capability", out _))
        {
            var capability = await firstSend.RegisterAsync(carrier.Element, cancellationToken).ConfigureAwait(false);
            if (!capability.IsSuccess)
            {
                return Result.Failure<BatchAckResult>(capability.Error!);
            }

            capabilityId = capability.Value.CapabilityId;
        }

        // The audit decision of the batch (S-1), taken only now that the capability step is known: a batch that
        // registered an org-level adapter_capability row is audited like the 04.7.1-T2.3 route (the row names the
        // capability), while an ordinary batch suppresses the [AuditedAction] row (its event_batch row and the
        // usage outbox row are the durable record; see the class doc).
        if (capabilityId is { } registeredCapability)
        {
            audit.Describe(new AuditObject("adapter_capability", registeredCapability.ToString("D")));
        }
        else
        {
            audit.Suppress();
        }

        var orgId = scopes.Require().OrgId;

        // (f0) The batch's own event_id dedupe (AC2/AC5): the first occurrence of an event_id is the accepted
        // one; a later occurrence with the identical canonical payload is an in-batch duplicate, excluded from
        // the insert, the attempt writer and the sequence read-back so the unique indexes and the attempt writer
        // see the event once, while a later occurrence whose canonical payload differs refuses the whole batch
        // before any write. One in-memory pass; it adds no statement.
        var firstByEventId = new Dictionary<Guid, ParsedEvent>(events.Count);
        var inBatchDuplicate = new bool[events.Count];
        var toInsert = new List<ParsedEvent>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var candidate = events[i];
            if (firstByEventId.TryGetValue(candidate.EventId, out var first))
            {
                if (!string.Equals(first.PayloadHash, candidate.PayloadHash, StringComparison.Ordinal))
                {
                    // AC5: one event_id, two canonical payloads inside one batch — the whole batch is refused.
                    return Result.Failure<BatchAckResult>(Error.Conflict(
                        ApiErrorCode.DuplicateDifferentPayload,
                        "an event_id of this batch is repeated with a different payload",
                        [new ErrorDetail($"/events/{candidate.Index}/event_id", ApiErrorCode.DuplicateDifferentPayload, candidate.Index)]));
                }

                inBatchDuplicate[i] = true;
                continue;
            }

            firstByEventId.Add(candidate.EventId, candidate);
            toInsert.Add(candidate);
        }

        // (f) One round trip inserts the whole batch. `on conflict do nothing` (no target) covers both unique
        // keys of `event`, so a duplicate and a reused sequence never raise: a caught 23505 would abort the
        // transaction and could not be probed from inside it (05.3.3 decision 4). The read-back below tells
        // the three cases apart.
        var rows = toInsert.Select(e => new EventRow(
            Guid.CreateVersion7(),
            e.EventId,
            e.ProducerId,
            e.Sequence,
            e.JobId,
            e.Epoch,
            e.ExecutionKey,
            e.AttemptNo,
            e.Type,
            e.RawStatus,
            // 00 §8.3 defines a normalised status only for a finished attempt's outcome; the other types
            // (test.started/test.step/run.log/…) carry no verdict, so the column stays NULL for them (F3).
            string.Equals(e.Type, EventBatchParser.TestFinished, StringComparison.Ordinal)
                ? Normalize(e.RawStatus, e.Xfail)
                : null,
            e.DurationMs,
            e.OccurredAt,
            e.PayloadHash,
            e.PayloadJson,
            e.ArtifactIds)).ToList();

        var insertedIds = (await db.QueryAsync<Guid>(
            InsertEventsSql,
            new
            {
                org_id = orgId,
                run_id = command.RunId,
                after_cancel = afterCancel,
                events = JsonSerializer.Serialize(rows),
            },
            cancellationToken).ConfigureAwait(false)).ToHashSet();

        var acks = new string[events.Count];
        var duplicates = 0;
        var accepted = 0;

        // An in-batch duplicate is acked without a row of its own: its event_id is on file exactly once.
        for (var i = 0; i < events.Count; i++)
        {
            if (inBatchDuplicate[i])
            {
                acks[events[i].Index] = DuplicateAck;
                duplicates++;
            }
        }

        if (insertedIds.Count == toInsert.Count)
        {
            foreach (var e in toInsert)
            {
                acks[e.Index] = AcceptedAck;
                accepted++;
            }
        }
        else
        {
            var stored = (await db.QueryAsync<StoredEvent>(
                StoredEventsSql,
                new
                {
                    org_id = orgId,
                    run_id = command.RunId,
                    // jsonb_to_recordset takes an array of OBJECTS, not an array of strings: a bare uuid
                    // array answers 22023 'argument of jsonb_to_recordset must be an array of objects'.
                    ids = JsonSerializer.Serialize(
                        toInsert.Where(e => !insertedIds.Contains(e.EventId)).Select(e => new { event_id = e.EventId })),
                },
                cancellationToken).ConfigureAwait(false))
                .ToDictionary(row => row.EventId, row => row.PayloadHash);

            foreach (var e in toInsert)
            {
                if (insertedIds.Contains(e.EventId))
                {
                    acks[e.Index] = AcceptedAck;
                    accepted++;
                    continue;
                }

                if (stored.TryGetValue(e.EventId, out var hash))
                {
                    if (!string.Equals(hash, e.PayloadHash, StringComparison.Ordinal))
                    {
                        // AC5: the same event_id with a different canonical payload rejects the whole batch.
                        return Result.Failure<BatchAckResult>(Error.Conflict(
                            ApiErrorCode.DuplicateDifferentPayload,
                            "an event_id of this batch is already stored with a different payload",
                            [new ErrorDetail($"/events/{e.Index}/event_id", ApiErrorCode.DuplicateDifferentPayload, e.Index)]));
                    }

                    acks[e.Index] = DuplicateAck;
                    duplicates++;
                    continue;
                }

                // AC6: not inserted and no row with this event_id exists, so the (run, producer, sequence)
                // key of another event holds this sequence — a reused sequence is refused, never a 500.
                return Result.Failure<BatchAckResult>(Error.Unprocessable(
                    ApiErrorCode.SequenceReused,
                    "a sequence of this batch is already stored for the producer",
                    [new ErrorDetail($"/events/{e.Index}/sequence", ApiErrorCode.SequenceReused, e.Index)]));
            }
        }

        // (g) Case attempts: the writer dedupes itself, so every test.finished event this batch actually
        // inserted is passed and the caller rolls back on any refusal (AC5). An event already stored by an
        // earlier batch (acked `duplicate` after the read-back) is excluded: only newly accepted events reach
        // the attempt writer (P2).
        var attemptSlots = toInsert
            .Where(e => insertedIds.Contains(e.EventId)
                && string.Equals(e.Type, EventBatchParser.TestFinished, StringComparison.Ordinal)
                && e.RawStatus is not null)
            .Select(e => new AttemptSlot(e.Index, new CaseAttemptInput(
                e.ExecutionKey,
                e.AttemptNo,
                e.RawStatus!,
                StartedAt: null,
                FinishedAt: e.OccurredAt,
                // The attempt column is int while the envelope's duration is a JSON number; a value past
                // int.MaxValue is dropped rather than truncated (the event row keeps the exact value).
                DurationMs: e.DurationMs is { } ms and <= int.MaxValue ? (int)ms : null,
                Xfail: e.Xfail,
                JobId: e.JobId)))
            .ToList();

        if (attemptSlots.Count > 0)
        {
            var outcomes = await attempts
                .WriteAttemptsAsync(command.RunId, [.. attemptSlots.Select(slot => slot.Input)], cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < outcomes.Count; i++)
            {
                var outcome = outcomes[i];
                if (!outcome.IsRefusal)
                {
                    continue;
                }

                var index = attemptSlots[i].EventIndex;
                return outcome.Status switch
                {
                    CaseAttemptWriteStatus.UnknownExecution => Result.Failure<BatchAckResult>(Error.Unprocessable(
                        ApiErrorCode.UnknownExecutionKey,
                        "an execution key of this batch is not a planned execution of the run",
                        [new ErrorDetail($"/events/{index}/execution_key", "unknown_execution_key", index)])),
                    CaseAttemptWriteStatus.DuplicateDifferentPayload => Result.Failure<BatchAckResult>(Error.Conflict(
                        ApiErrorCode.DuplicateDifferentPayload,
                        "the case attempt slot already holds a different payload",
                        [new ErrorDetail($"/events/{index}/status", "duplicate_different_payload", index)])),
                    _ => Result.Failure<BatchAckResult>(Error.Unprocessable(
                        ApiErrorCode.EventInvalid,
                        "the case attempt of this event was rejected",
                        [new ErrorDetail($"/events/{index}/status", outcome.Reason ?? "invalid", index)])),
                };
            }
        }

        // (h) The producer's high-water mark, one statement for every producer of the batch. Only newly
        // accepted events count, and a batch that accepted nothing (every event a duplicate of an earlier
        // batch) writes no slot row at all: the update would be a no-op that still bumps
        // producer_slot.version through the shared trigger (P2).
        if (accepted > 0)
        {
            var slotMaxima = toInsert
                .Where(e => insertedIds.Contains(e.EventId))
                .GroupBy(e => e.ProducerId, StringComparer.Ordinal)
                .Select(group => new SlotMax(group.Key, group.Max(e => e.Sequence)))
                .ToList();

            await db.ExecuteAsync(
                UpdateSlotSql,
                new
                {
                    org_id = orgId,
                    run_id = command.RunId,
                    slots = JsonSerializer.Serialize(slotMaxima),
                },
                cancellationToken).ConfigureAwait(false);
        }

        // (i) The batch's own audit row (C5).
        var batchId = Guid.CreateVersion7();
        await db.ExecuteAsync(
            InsertBatchSql,
            new
            {
                id = batchId,
                org_id = orgId,
                run_id = command.RunId,
                producer_id = producerId,
                event_count = events.Count,
                bytes_in = (long)command.Body.Length,
                // Gzip is refused until 04.1.1-T3, so the expanded size equals the received size.
                bytes_expanded = (long)command.Body.Length,
                accepted,
                duplicate = duplicates,
                request_id = command.RequestId,
            },
            cancellationToken).ConfigureAwait(false);

        // (j) Usage is metered once per accepted batch per metric, and a resend with accepted = 0 emits no
        // row at all (T83, 04.8.1 §4).
        if (accepted > 0)
        {
            await outbox.EnqueueAsync(
                UsageTaskType,
                JsonSerializer.Serialize(new { event_batch_id = batchId, metric = UsageMetric, quantity = accepted }),
                "event_batch",
                batchId,
                $"usage:batch:{batchId}:{UsageMetric}",
                cancellationToken).ConfigureAwait(false);
        }

        // Counters only, never a body: the span carries the shape of the batch, the rows carry the detail.
        activity?.SetTag("ingestion.batch.accepted", accepted);
        activity?.SetTag("ingestion.batch.duplicates", duplicates);

        // (k) The ack exists only now, after the last row of the batch was written in this transaction.
        return Result.Success(new BatchAckResult(
            batchId,
            accepted,
            duplicates,
            [.. events.Select(e => new EventAck(e.Index, e.EventId, acks[e.Index]))]));
    }

    private static Result<BatchAckResult> TooLarge() => Result.Failure<BatchAckResult>(new Error(
        ApiErrorCode.BatchTooLarge,
        "the event batch exceeds the route's transport cap",
        StatusCodes.Status413PayloadTooLarge));

    /// <summary>
    /// The server-side status normalisation of 05.3.3 (<c>CaseAttemptStatus.Normalize</c>) mirrored for the
    /// one reader that lives in this module: the <c>event.normalized_status</c> column of a <c>test.finished</c>
    /// event (00 §8.3). The rule lives in the Orchestrator module's domain and is not reachable across the
    /// module edge at runtime, so the table is repeated here; it is the closed vocabulary of
    /// <c>contracts/adapter/playwright-mapping.md</c> §status and must change with it (recorded as an open item
    /// in the task file's Notes). It is an exact mirror — including null/empty/unknown mapping to <c>error</c> —
    /// and a parity unit test (<c>IngestEventBatchNormalizeParityTests</c>) pins it to
    /// <c>CaseAttemptStatus.Normalize</c> over the whole vocabulary.
    /// </summary>
    internal static string Normalize(string? raw, bool xfail) => raw switch
    {
        "passed" => xfail ? "failed" : "passed",
        "failed" => xfail ? "passed" : "failed",
        "timedOut" or "timed_out" => "timed_out",
        "skipped" => "skipped",
        "interrupted" or "error" => "error",
        // Anything else normalises to error: an unknown producer value is a failure, never a pass.
        _ => "error",
    };

    /// <summary>One statement for the whole batch; the parameter is the batch as a JSON array (S1).</summary>
    private const string InsertEventsSql = """
        insert into public.event (
            id, org_id, run_id, event_id, producer_id, sequence, job_id, epoch, execution_key,
            attempt_no, type, raw_status, normalized_status, duration_ms, occurred_at, payload_hash,
            payload, artifact_ids, after_cancel)
        select
            x.id, @org_id, @run_id, x.event_id, x.producer_id, x.sequence, x.job_id, x.epoch,
            x.execution_key, x.attempt_no, x.type, x.raw_status, x.normalized_status, x.duration_ms,
            x.occurred_at, x.payload_hash, x.payload::jsonb, x.artifact_ids, @after_cancel
        from jsonb_to_recordset(@events::jsonb) as x (
            id uuid, event_id uuid, producer_id text, sequence bigint, job_id uuid, epoch bigint,
            execution_key text, attempt_no integer, type text, raw_status text, normalized_status text,
            duration_ms bigint, occurred_at timestamptz, payload_hash text, payload text, artifact_ids uuid[])
        on conflict do nothing
        returning event_id
        """;

    /// <summary>The bounded read-back of the events this batch did not insert (duplicate vs reused sequence).</summary>
    private const string StoredEventsSql = """
        select e.event_id as "EventId", e.payload_hash as "PayloadHash"
        from public.event e
        join jsonb_to_recordset(@ids::jsonb) as i (event_id uuid) on i.event_id = e.event_id
        where e.org_id = @org_id and e.run_id = @run_id
        """;

    /// <summary>The high-water mark of every producer of the batch (the <c>last_sequence</c> column grant).</summary>
    private const string UpdateSlotSql = """
        update public.producer_slot s
        set last_sequence = greatest(s.last_sequence, m.max_sequence)
        from jsonb_to_recordset(@slots::jsonb) as m (producer_id text, max_sequence bigint)
        where s.org_id = @org_id and s.run_id = @run_id and s.producer_id = m.producer_id
        """;

    /// <summary>The batch's own audit row (04.1.1 §4, C5).</summary>
    private const string InsertBatchSql = """
        insert into public.event_batch (
            id, org_id, run_id, producer_id, event_count, bytes_in, bytes_expanded, accepted,
            duplicate, request_id)
        values (
            @id, @org_id, @run_id, @producer_id, @event_count, @bytes_in, @bytes_expanded, @accepted,
            @duplicate, @request_id)
        """;
}
