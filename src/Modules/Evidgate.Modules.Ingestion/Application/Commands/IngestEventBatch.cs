using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Observability.Correlation;
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
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Ingestion.Application.Commands;

/// <summary>
/// The 04.1.1-T1 hot path: one atomic event batch (<c>POST /v1/runs/{r}/events</c>, 00 §9.3/§9.4).
/// The command carries the route's run id, the raw body bytes and the request id; the handler owns
/// the whole decision list of the task file §5 (edge validation → scope → producer binding →
/// planned-key gate → first-send capability → one insert → case attempts → slot/ledger rows) inside the
/// C2 pipeline's tenant transaction, so a refusal leaves no row behind.
/// <para>
/// Not an <c>IIdempotentCommand</c> (D2): the route is exempt from <c>Idempotency-Key</c>
/// (contracts/README.md), the batch carries its own per-event <c>event_id</c> dedupe, and a resend
/// must be answered with the per-event <c>duplicate</c> acks rather than a cached response.
/// </para>
/// </summary>
/// <param name="RunId">The run of the route's <c>{r}</c> segment.</param>
/// <param name="Body">The raw UTF-8 request body: the bytes as sent when identity, the decoded bytes when <c>Content-Encoding: gzip</c> (04.1.1-T3).</param>
/// <param name="RequestId">The <c>X-Request-Id</c> the host echoed; the batch's audit row carries it.</param>
/// <param name="BytesIn">The as-sent (compressed, for gzip) body size, already capped at 1 MiB by the endpoint; the handler's transport cap compares against this, never the decoded size.</param>
/// <param name="BytesExpanded">The decoded body size: equal to <paramref name="BytesIn"/> for identity, the inflated size for gzip.</param>
internal sealed record IngestEventBatch(
    Guid RunId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string RequestId,
    int BytesIn,
    int BytesExpanded) : ICommand<Result<BatchAckResult>>;

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
/// step (S-1). The transition's own <c>run.state_changed</c> audit row is written by 05's
/// <c>IRunStateTransition</c> before that decision and is never suppressed by it.
/// </para>
/// <para>
/// <b>Lock order</b> (04.1.1-T4, ruling T13 + the 05.3.1 lock-order rule): the first accepted batch of a
/// <c>queued</c> run drives <c>queued → running</c> through 05's <c>IRunStateTransition</c>, which loads
/// the run row <c>FOR NO KEY UPDATE</c>; the scope read below loads the same row <c>FOR SHARE</c>. Because
/// two callers that share-lock a row and then upgrade to <c>NO KEY UPDATE</c> deadlock (40P01), this
/// handler takes the stronger lock <b>first</b>: one unlocked, RLS-scoped read of <c>run.state</c> decides
/// whether the run is <c>queued</c>, and only then is the transition called — before any shared read. A
/// batch on a <c>running</c>/<c>collecting</c>/terminal run skips the transition entirely, so the ingest
/// hot path keeps its single <c>FOR SHARE</c> read (T101) and a run is never re-transitioned. A batch this
/// handler refuses after the transition rolls it back with everything else, so the run is <c>queued</c>
/// again; the only residual cost of that path is that the abandoned transaction keeps the run's
/// <c>FOR NO KEY UPDATE</c> lock for the rest of its statements (QA finding O1, S43).
/// </para>
/// </summary>
[AuditedAction("event.batch.ingest")]
[Permission(Permissions.Event.Write)]
internal sealed class IngestEventBatchHandler(
    Lazy<ContractSchemaSet> schemas,
    FirstSendCapabilityService firstSend,
    IRunEventScope runScopes,
    IRunStateTransition runStates,
    IProducerRegistry producers,
    ICaseAttemptWriter attempts,
    IOutboxWriter outbox,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IAuditTrail audit,
    ILogger<IngestEventBatchHandler> logger) : ICommandHandler<IngestEventBatch, Result<BatchAckResult>>
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

    /// <summary>The 00 §8.1 state whose first accepted event drives the transition to <see cref="RunningState"/> (T13).</summary>
    internal const string QueuedState = "queued";

    /// <summary>The 00 §8.1 state the first accepted event moves a queued run to (T13).</summary>
    internal const string RunningState = "running";

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

        // The ids a trace needs exist before parsing, so a refused batch is still attributable
        // without carrying any body value; producer and event count follow the parse. The run id is
        // tagged in the canonical D form, the same text the edge and the task carrier write.
        activity?.SetTag(CorrelationNames.RunId, command.RunId.ToString("D", CultureInfo.InvariantCulture));
        activity?.SetTag(CorrelationNames.RequestId, command.RequestId);

        // The caller's identity is settled before the body is looked at: neither guard depends on it, and a
        // credential that cannot be attributed to a project is refused without spending the parse on it.
        var principal = principals.Current;
        if (principal is null || string.IsNullOrEmpty(principal.CredentialId))
        {
            // Fail closed: the batch's producer binding needs the caller's token identity (T9).
            return Result.Failure<BatchAckResult>(new Error(
                ApiErrorCode.Unauthenticated,
                "no authenticated producer could be resolved",
                StatusCodes.Status401Unauthorized));
        }

        // A producer credential names the one project it writes for. Without that claim the batch
        // cannot be attributed to a project, so it is refused in the handler, before any read or
        // write, instead of by the burst limiter, which has no partition to charge such a caller.
        if (principal.Kind == PrincipalKind.ServicePrincipal && principal.ProjectScope is null)
        {
            return Result.Failure<BatchAckResult>(Error.Forbidden(
                "the events route requires a project-scoped producer token"));
        }

        // (a) The edge cap comes from the schema itself (event-envelope.v1's x-max-bytes), so the limit has
        // one home. It bounds the body as sent, not the decoded size: a gzip body may legitimately inflate
        // past 1 MiB up to the 04.1.1-T3 expanded cap, so the comparison uses BytesIn. The parser enforces
        // the per-event and per-batch rules below it (00 §5.5 C6).
        if (schemas.Value.MaxBytes(ContractSchemaKeys.EventEnvelope) is { } maxBytes
            && command.BytesIn > maxBytes)
        {
            IngestBatchRejection.Record(
                logger, IngestBatchRejection.BodyTooLarge, command.RequestId, command.RunId, command.BytesIn, command.BytesExpanded);

            return TooLarge();
        }

        // (b) One parse of the body: EventBatchParser is the hand-written edge validator of this route and
        // already reports the closed type set, the member shapes and the per-event details with their index,
        // so a second Corvus pass over the same ≤1 MiB body would add no accepted input to the hot path
        // (04.1.1 §8; the Ajv/Corvus parity corpus still governs the published schema).
        var parsed = EventBatchParser.Parse(command.Body);
        if (!parsed.IsAccepted)
        {
            if (parsed.Errors.Any(error => error.Reason == TooLargeReason))
            {
                // The bytes cap of (a) already passed, so the only too_large the parser can report is the
                // 500-event cap (AC7): one refusal, one counter bump and one log line (04.1.1-T3).
                IngestBatchRejection.Record(
                    logger, IngestBatchRejection.EventCount, command.RequestId, command.RunId, command.BytesIn, command.BytesExpanded);

                return TooLarge();
            }

            return Result.Failure<BatchAckResult>(Error.Unprocessable(
                ApiErrorCode.EventInvalid,
                "the event batch was rejected",
                parsed.Errors));
        }

        var events = parsed.Events;

        var producerId = events[0].ProducerId;
        activity?.SetTag(EvidgateTelemetry.ProducerIdTag, producerId);
        activity?.SetTag(EvidgateTelemetry.EventCountTag, events.Count);
        var orgId = scopes.Require().OrgId;

        // (b1) Lock order (04.1.1-T4, ruling T13 + the 05.3.1 lock-order rule; see the class doc): the first
        // accepted batch of a queued run moves it queued -> running through 05's IRunStateTransition, which
        // loads the run row FOR NO KEY UPDATE. The scope read at (c) takes the same row FOR SHARE, and two
        // racing first batches that each hold FOR SHARE and then upgrade to NO KEY UPDATE deadlock (40P01), so
        // the transition runs BEFORE any shared read. The unlocked, RLS-scoped read below decides whether the
        // run is queued at all: a batch on a running/collecting/terminal run skips the transition, so the hot
        // path keeps its single FOR SHARE read (T101) and a run is never re-transitioned. The transition
        // re-checks the state under its own lock, so the racing loser is the T82 no-op (one winner, one no-op);
        // a batch this handler refuses later rolls the applied transition back with everything else, and a
        // canceled or complete run is untouched.
        var runState = await db
            .ScalarAsync<string>(ReadRunStateSql, new { org_id = orgId, run_id = command.RunId }, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(runState, QueuedState, StringComparison.Ordinal))
        {
            var transition = await runStates
                .TransitionAsync(new TransitionRunStateCommand(command.RunId, RunningState), cancellationToken)
                .ConfigureAwait(false);

            if (!transition.IsSuccess)
            {
                // The only reachable failure of this edge is 404 (the run is not visible to this tenant, which
                // the scope read would answer the same way): a queued source cannot produce a forward-skip, and
                // no ExpectedVersion is passed, so no 409/412 is reachable. A no-op outcome is a success.
                return Result.Failure<BatchAckResult>(transition.Error!);
            }
        }

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

        // T12/T81 (replacing 04.1.1's D6 fail-closed for the incomplete case): a canceled run still accepts
        // events and marks them after_cancel (T12); an incomplete run still accepts late evidence inside its
        // late window and marks it late = true, because the run row's own immutable window (T93) is what
        // closes it - after the window the run answers 409 run_closed (T81). A complete run is closed: its
        // window runs "since completion" and the run row stores no completion instant (T101's run.completed_at
        // is 05.3.1's to add), so nothing here could open it. The state and the window are read from the run
        // row taken FOR SHARE (T91), so a committed cancel or completion can never be missed. The decision
        // itself is shared with every other evidence writer, so no route can disagree on what is closed.
        var admission = RunAdmissionPolicy.Decide(scope.ProjectState, scope.RunState, scope.LateWindowOpen);

        if (string.Equals(admission.Refusal, RunAdmissionPolicy.ProjectDeleted, StringComparison.Ordinal))
        {
            return Result.Failure<BatchAckResult>(Error.Conflict(
                ApiErrorCode.ProjectDeleting,
                "the project is being deleted"));
        }

        if (string.Equals(admission.Refusal, RunAdmissionPolicy.ProjectSuspended, StringComparison.Ordinal))
        {
            return Result.Failure<BatchAckResult>(Error.Denied(
                ApiErrorCode.OrgSuspended,
                "the project is suspended"));
        }

        if (string.Equals(admission.Refusal, RunAdmissionPolicy.RunClosed, StringComparison.Ordinal))
        {
            return Result.Failure<BatchAckResult>(Error.Conflict(
                ApiErrorCode.RunClosed,
                "the run no longer accepts events"));
        }

        var afterCancel = admission.AfterCancel;
        var late = admission.Late;

        // (c) One bind-or-check per batch: the first accepted write of a slot binds it to this token (T9).
        // It runs before the planned-key gate below (shared contract, T9/T10): a foreign or mismatching
        // token always gets 403 producer_slot_taken - or 422 unknown_producer when the run has no such slot
        // - and never learns which execution keys the run planned. A refusal after the bind rolls the bind
        // back, because this handler runs inside the C2 pipeline's one tenant transaction.
        var bound = await producers
            .BindOrCheckAsync(command.RunId, producerId, principal.CredentialId, principal.CiRun?.ToBindingReference(), cancellationToken)
            .ConfigureAwait(false);
        if (!bound.IsSuccess)
        {
            return Result.Failure<BatchAckResult>(bound.Error!);
        }

        // (d) Every execution_key must be a planned execution of the run; all failing indices are named (T10).
        // The membership test rides the scope read's single `execution_key = ANY(@keys)` probe above, so this
        // gate costs no extra statement.
        var unknown = events.Where(e => !scope.PlannedKeys.Contains(e.ExecutionKey)).ToList();
        if (unknown.Count > 0)
        {
            return Result.Failure<BatchAckResult>(Error.Unprocessable(
                ApiErrorCode.UnknownExecutionKey,
                "the batch names execution keys that are not planned executions of the run",
                [.. unknown.Select(e => new ErrorDetail($"/events/{e.Index}/execution_key", "unknown_execution_key", e.Index))]));
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
                late,
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
        var slotMaxima = accepted > 0
            ? toInsert
                .Where(e => insertedIds.Contains(e.EventId))
                .GroupBy(e => e.ProducerId, StringComparer.Ordinal)
                .Select(group => new SlotMax(group.Key, group.Max(e => e.Sequence)))
                .ToList()
            : [];

        if (slotMaxima.Count > 0)
        {
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

        // (h2) Late evidence needs a reader: the run already finished as incomplete, so no finalize will close another
        // slot and nothing else would compare these rows into a revision. One trigger per shard, through the outbox in
        // this transaction, so the row and the evidence commit or roll back together. Only accepted rows enqueue, so a
        // resend of evidence already stored (accepted = 0) can never append a second revision.
        if (late)
        {
            foreach (var slot in slotMaxima)
            {
                await outbox.EnqueueAsync(
                    RunReconcileContract.OutboxTaskType,
                    RunReconcileContract.LatePayloadJson(command.RunId, slot.producer_id),
                    RunReconcileContract.AggregateType,
                    command.RunId,
                    RunReconcileContract.LateIdempotencyKey(command.RunId, slot.producer_id, slot.max_sequence),
                    cancellationToken).ConfigureAwait(false);
            }
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
                bytes_in = (long)command.BytesIn,
                // The decoded size: equal to bytes_in for an identity body, the inflated size for gzip
                // (04.1.1-T3); the endpoint bounded both before this handler ran.
                bytes_expanded = (long)command.BytesExpanded,
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

    /// <summary>
    /// The one unlocked, RLS-scoped read of the run's state (04.1.1-T4): it decides whether the first accepted
    /// batch must take the run row <c>FOR NO KEY UPDATE</c> up front (the <c>queued → running</c> transition) or
    /// can keep the plain <c>FOR SHARE</c> scope read. It is a primary-key read with no locking clause, so it
    /// never blocks on a cancel or a concurrent batch; the transition re-checks the state under its own lock, so
    /// a stale answer here is harmless. A missing or foreign-org run answers no row, exactly like the scope read.
    /// </summary>
    private const string ReadRunStateSql = """
        select state from public.run where org_id = @org_id and id = @run_id
        """;

    /// <summary>One statement for the whole batch; the parameter is the batch as a JSON array (S1).</summary>
    private const string InsertEventsSql = """
        insert into public.event (
            id, org_id, run_id, event_id, producer_id, sequence, job_id, epoch, execution_key,
            attempt_no, type, raw_status, normalized_status, duration_ms, occurred_at, payload_hash,
            payload, artifact_ids, after_cancel, late)
        select
            x.id, @org_id, @run_id, x.event_id, x.producer_id, x.sequence, x.job_id, x.epoch,
            x.execution_key, x.attempt_no, x.type, x.raw_status, x.normalized_status, x.duration_ms,
            x.occurred_at, x.payload_hash, x.payload::jsonb, x.artifact_ids, @after_cancel, @late
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
