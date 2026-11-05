using Microsoft.EntityFrameworkCore;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The write half of <see cref="ParseRawReportHandler"/>: one transaction that admits the report, closes the synthetic
/// producer slot, inserts the serialized event set, writes the new finishes' attempts, enqueues the reconcile trigger and
/// stores the report's own row — or commits nothing. Kept apart from the read-and-map half so each pipeline stage stays
/// one file to read.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>
    /// The one write transaction: the run's lock, the closed synthetic producer slot, every event in one statement, the
    /// attempts of the newly inserted finishes, the slot's high-water mark, the reconcile trigger and the report's row —
    /// committed together or not at all.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="producerId">The synthetic producer id of this report.</param>
    /// <param name="events">The serialized, hashed event set built before this scope opened.</param>
    /// <param name="report">The report's own row, built before this scope opened.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The named outcome of the write.</returns>
    private async Task<ParseRawReportOutcome> WriteAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        string producerId,
        SerializedEventSet events,
        RawReportDraft report,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        var (admission, stopped) = await AdmitAsync(context, orgId, payload, cancellationToken).ConfigureAwait(false);
        if (admission is not { } allowed)
        {
            return stopped!;
        }

        if (events.Events.Length == 0)
        {
            // Nothing mapped (every testcase unmapped, or a document with no testcase): no slot, no event, no trigger.
            // The report still leaves its own row, so a document that mapped nothing is distinguishable from one that
            // was never parsed.
            await InsertRawReportAsync(orgId, payload, report, cancellationToken).ConfigureAwait(false);
            LogMapped(logger, payload.ArtifactId, 0, 0);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ParseRawReportOutcome(ParseRawReportOutcome.Parsed, Events: 0);
        }

        var (insertedIds, refused) = await InsertEventSetAsync(
                allowed, orgId, payload, producerId, events, cancellationToken)
            .ConfigureAwait(false);
        if (insertedIds is not { } inserted)
        {
            return refused!;
        }

        if (!await WriteAttemptsAsync(payload, events, inserted, cancellationToken).ConfigureAwait(false))
        {
            return new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "case_attempt_conflict");
        }

        await AdvanceSlotAsync(allowed, orgId, payload, producerId, events, inserted, cancellationToken)
            .ConfigureAwait(false);

        // The row is the transaction's last insert: every earlier refusal above has returned without committing, so a
        // skipped attempt leaves no row and a committed one always has exactly one.
        await InsertRawReportAsync(orgId, payload, report, cancellationToken).ConfigureAwait(false);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogMapped(logger, payload.ArtifactId, events.Events.Length, inserted.Count);

        return new ParseRawReportOutcome(ParseRawReportOutcome.Parsed, Events: inserted.Count);
    }

    /// <summary>
    /// The write scope's opening checks, cheapest first: the artifact is still this run's ready raw report, the run
    /// exists under its own lock, and the shared admission policy accepts the evidence. A refusal here has written
    /// nothing, so disposing the scope rolls back nothing.
    /// </summary>
    /// <param name="context">The tenant write model created inside the scope.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The admission, or the skipped outcome that ended the attempt.</returns>
    private async Task<(RunAdmission? Admission, ParseRawReportOutcome? Stopped)> AdmitAsync(
        EvidenceDbContext context,
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        // Read again inside the write scope rather than trusting the preparation: a concurrent attempt may have
        // decided the artifact while the bytes were mapped, and a report that left `ready` is out of evidence.
        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == payload.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null
            || !string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal)
            || !string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal)
            || artifact.RunId != payload.RunId)
        {
            LogSkipped(logger, payload.ArtifactId, "left_ready");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "left_ready"));
        }

        // The run lock serialises two reports of one run, so the slot's next shard index cannot be taken twice, and it
        // also answers the admission facts under a lock: the run's and its project's state, and the late window.
        var run = await db
            .QuerySingleOrDefaultAsync<RunGate>(RunGateSql, new { org_id = orgId, run_id = payload.RunId }, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            LogSkipped(logger, payload.ArtifactId, "run_absent");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "run_absent"));
        }

        var admission = RunAdmissionPolicy.Decide(run.ProjectState, run.RunState, run.LateWindowOpen);
        if (!admission.Allowed)
        {
            // Terminal: a closed run or a project that is going away will not admit this evidence on a retry either.
            LogSkipped(logger, payload.ArtifactId, admission.Refusal!);
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, admission.Refusal));
        }

        return (admission, null);
    }

    /// <summary>
    /// The slot and the event set: the next free shard index, the synthetic slot written closed and declared, every row
    /// in one statement, and the replay comparison for each row that statement could not insert.
    /// </summary>
    /// <param name="admission">The decision the event rows carry as <c>after_cancel</c> and <c>late</c>.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="producerId">The synthetic producer id of this report.</param>
    /// <param name="events">The serialized, hashed event set built before this scope opened.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The ids this attempt inserted, or the skipped outcome that ended it.</returns>
    private async Task<(HashSet<Guid>? Inserted, ParseRawReportOutcome? Stopped)> InsertEventSetAsync(
        RunAdmission admission,
        Guid orgId,
        ParseRawReportPayload payload,
        string producerId,
        SerializedEventSet events,
        CancellationToken cancellationToken)
    {
        var shardIndex = await db
            .ScalarAsync<int?>(NextShardIndexSql, new { org_id = orgId, run_id = payload.RunId }, cancellationToken)
            .ConfigureAwait(false);

        if (shardIndex is null or > MaxShardIndex)
        {
            // Every manifest shard index is taken, so no slot can name this synthetic producer. Retrying cannot free
            // one; the attempt is reported for an operator instead of silently dropping the report.
            LogSkipped(logger, payload.ArtifactId, "no_producer_slot");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "no_producer_slot"));
        }

        await InsertSlotAsync(orgId, payload, producerId, shardIndex.Value, events, cancellationToken)
            .ConfigureAwait(false);

        var insertedIds = (await db.QueryAsync<Guid>(
                InsertEventsSql,
                new
                {
                    org_id = orgId,
                    run_id = payload.RunId,
                    after_cancel = admission.AfterCancel,
                    late = admission.Late,
                    events = events.Json,
                },
                cancellationToken).ConfigureAwait(false))
            .ToHashSet();

        if (insertedIds.Count < events.Events.Length
            && !await ReplayMatchesAsync(orgId, payload, events, insertedIds, cancellationToken).ConfigureAwait(false))
        {
            // The transaction rolls back on dispose: the conflicting set is never half-written. The reason is named so
            // the operator can find the artifact; retrying the same bytes will refuse the same way.
            LogSkipped(logger, payload.ArtifactId, "duplicate_different_payload");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "duplicate_different_payload"));
        }

        return (insertedIds, null);
    }

    /// <summary>
    /// The synthetic producer's slot, written closed and declared in one statement: no producer will ever finalize a
    /// report, so an open slot would hold the reconciler on a shard that can never close, and 0035's three-column check
    /// is satisfied because the declaration and the close are written together.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="producerId">The synthetic producer id of this report.</param>
    /// <param name="shardIndex">The free shard index the slot takes.</param>
    /// <param name="events">The serialized set whose declaration the slot states.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task InsertSlotAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        string producerId,
        int shardIndex,
        SerializedEventSet events,
        CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(
                InsertSlotSql,
                new
                {
                    id = Guid.CreateVersion7(),
                    org_id = orgId,
                    run_id = payload.RunId,
                    shard_index = shardIndex,
                    producer_id = producerId,
                    bound_token_jti = SyntheticBinding,
                    last_sequence = events.MaxSequence,
                    declared_last_sequence = events.MaxSequence,
                    declared_count = events.DeclaredCount,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The slot's high-water mark and the one reconcile trigger the new events need: the late-evidence shape for a run
    /// that already finished as incomplete, the ordinary slot-closed shape for every other run, so the freeze path sees
    /// the events without waiting for a finalize a synthetic producer can never trigger. A replay that inserted nothing
    /// advances neither, because the stored shard is already the whole set.
    /// </summary>
    /// <param name="admission">The decision, whose late flag names the trigger shape.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="producerId">The synthetic producer id of this report.</param>
    /// <param name="events">The serialized set, whose declared pair the trigger names.</param>
    /// <param name="insertedIds">The ids this attempt inserted; nothing is advanced for an empty set.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    private async Task AdvanceSlotAsync(
        RunAdmission admission,
        Guid orgId,
        ParseRawReportPayload payload,
        string producerId,
        SerializedEventSet events,
        HashSet<Guid> insertedIds,
        CancellationToken cancellationToken)
    {
        if (insertedIds.Count == 0)
        {
            return;
        }

        await db.ExecuteAsync(
                UpdateSlotSql,
                new { org_id = orgId, run_id = payload.RunId, producer_id = producerId, last_sequence = events.MaxSequence },
                cancellationToken)
            .ConfigureAwait(false);

        await EnqueueReconcileAsync(payload, producerId, events, admission.Late, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Compares the stored payload hash of every event this attempt could not insert (the replay case) with the hash it
    /// computed for the same identity. Only an exact match may be a no-op; a mismatch — and an identity held by a
    /// different event — refuses the attempt.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="events">The serialized set this attempt built.</param>
    /// <param name="insertedIds">The event ids this attempt inserted.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when every stored row carries this attempt's hash.</returns>
    private async Task<bool> ReplayMatchesAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        SerializedEventSet events,
        HashSet<Guid> insertedIds,
        CancellationToken cancellationToken)
    {
        var stored = (await db.QueryAsync<StoredEventRow>(
                StoredEventHashesSql,
                new
                {
                    org_id = orgId,
                    run_id = payload.RunId,
                    events = SerializeEventIds(events.Events
                        .Where(item => !insertedIds.Contains(item.Row.EventId))
                        .Select(item => item.Row.EventId)),
                },
                cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row.EventId, row => row.PayloadHash);

        foreach (var item in events.Events)
        {
            if (insertedIds.Contains(item.Row.EventId))
            {
                continue;
            }

            if (!stored.TryGetValue(item.Row.EventId, out var storedHash)
                || !string.Equals(storedHash, item.Row.PayloadHash, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes one attempt per newly inserted finish through the shared writer, in batches the writer accepts. A refusal
    /// is reported to the caller, which then commits nothing.
    /// </summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="events">The serialized set, whose attempt inputs were built with the rows.</param>
    /// <param name="insertedIds">The event ids this attempt inserted.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>True when the writer accepted every attempt.</returns>
    private async Task<bool> WriteAttemptsAsync(
        ParseRawReportPayload payload,
        SerializedEventSet events,
        HashSet<Guid> insertedIds,
        CancellationToken cancellationToken)
    {
        var inputs = events.Events
            .Where(item => insertedIds.Contains(item.Row.EventId) && item.Attempt is not null)
            .Select(item => item.Attempt!)
            .ToArray();

        await using var attemptScope = scopeFactory.CreateAsyncScope();
        var attempts = attemptScope.ServiceProvider.GetRequiredService<ICaseAttemptWriter>();

        for (var offset = 0; offset < inputs.Length; offset += ICaseAttemptWriter.MaxBatchSize)
        {
            var batch = inputs
                .Skip(offset)
                .Take(ICaseAttemptWriter.MaxBatchSize)
                .ToArray();
            var outcomes = await attempts
                .WriteAttemptsAsync(payload.RunId, batch, cancellationToken)
                .ConfigureAwait(false);

            for (var index = 0; index < outcomes.Count; index++)
            {
                if (!outcomes[index].IsRefusal)
                {
                    continue;
                }

                LogAttemptRefused(logger, payload.ArtifactId, outcomes[index].Status.ToString());
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Enqueues the one reconcile trigger this attempt's new events need: the late-evidence shape for a run that already
    /// finished as incomplete, the ordinary slot-closed shape for every other run, so the freeze path sees the events
    /// without waiting for a finalize that a synthetic producer can never trigger.
    /// </summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="producerId">The synthetic producer id of this report.</param>
    /// <param name="events">The serialized set, whose declared pair the trigger names.</param>
    /// <param name="late">True when this evidence arrived after the run finished incomplete.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task EnqueueReconcileAsync(
        ParseRawReportPayload payload,
        string producerId,
        SerializedEventSet events,
        bool late,
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
                    RunReconcileContract.LateIdempotencyKey(payload.RunId, producerId, events.MaxSequence),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await outbox.EnqueueAsync(
                taskType,
                RunReconcileContract.PayloadJson(payload.RunId, producerId, events.MaxSequence, events.DeclaredCount),
                aggregateType,
                payload.RunId,
                RunReconcileContract.IdempotencyKey(payload.RunId, producerId),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
