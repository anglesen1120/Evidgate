using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The write half of <see cref="ReprocessReportHandler"/>: one transaction that admits the evidence, writes the closed
/// synthetic slot and the event set, attempts what no earlier report attempted, enqueues the reconcile trigger and stores
/// the report's own row — or commits nothing. The run's state machine, its batch receipts and its metering are not part
/// of it: a reprocess adds evidence, it never re-drives the run.
/// </summary>
internal sealed partial class ReprocessReportHandler
{
    /// <summary>
    /// The binding every synthetic slot carries. The column is a token id; this word cannot be one, so a synthetic shard
    /// is recognisable as holding no CI producer and no credential.
    /// </summary>
    private const string SyntheticBinding = "synthetic:junit-report";

    /// <summary>
    /// The highest shard index the column admits. A run at the cap cannot take another synthetic shard, and retrying
    /// cannot free one.
    /// </summary>
    private const int MaxShardIndex = 256;

    /// <summary>
    /// The run's state, its project's state and its late window, read under the run's own lock (the same admission facts
    /// the parse path reads, plus the lock the synthetic slot's shard index needs).
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

    /// <summary>The admission facts of the run, as the one lock read returns them.</summary>
    /// <param name="RunState">The run's state (0031's closed vocabulary).</param>
    /// <param name="ProjectState">The project's state (<c>active</c>, <c>suspended</c>, <c>deleted</c>).</param>
    /// <param name="LateWindowOpen">True when <c>now()</c> is at or before the run's late-window end.</param>
    private sealed record RunGate(string RunState, string ProjectState, bool LateWindowOpen);

    /// <summary>
    /// The one write transaction. Its order is the order the locks and keys require: the artifact is re-read, the run's
    /// row is locked, the admission policy decides, the slot and the events go in, the attempts follow the events that
    /// were really inserted, the trigger names the batch, and the report's own row is the last verdict — so a row that
    /// already holds a refusal rolls every write above it back.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The pinned release, the producer identity and the run the writes name.</param>
    /// <param name="events">The serialized, hashed event set built before this scope opened.</param>
    /// <param name="report">The report's own row, built before this scope opened.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The named outcome of the write.</returns>
    private async Task<ParseRawReportOutcome> WriteAsync(
        Guid orgId,
        Prepared prepared,
        SerializedEventSet events,
        RawReportDraft report,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        var (admission, stopped) = await AdmitAsync(context, orgId, prepared, cancellationToken).ConfigureAwait(false);
        if (admission is not { } allowed)
        {
            return stopped!;
        }

        if (events.Events.Length == 0)
        {
            return await WriteNothingMappedAsync(scope, orgId, prepared, report, cancellationToken).ConfigureAwait(false);
        }

        var (insertedIds, refused) = await InsertEventSetAsync(allowed, orgId, prepared, events, cancellationToken)
            .ConfigureAwait(false);
        if (insertedIds is not { } inserted)
        {
            return refused!;
        }

        if (inserted.Count > 0)
        {
            if (!await WriteAttemptsAsync(orgId, prepared, events, inserted, cancellationToken).ConfigureAwait(false))
            {
                return new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "case_attempt_conflict");
            }

            // One identity for this attempt's accepted set: the reconcile trigger is named by it, so it cannot collide
            // with a later attempt's trigger for this same request.
            await EnqueueReconcileAsync(
                    prepared, events, allowed.Late, allowed.AfterCancel, Guid.CreateVersion7(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (await StoreParsedRowAsync(orgId, prepared, report, cancellationToken).ConfigureAwait(false)
            is { } rejected)
        {
            return rejected;
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogMapped(logger, prepared.ParsingId, events.Events.Length, inserted.Count);

        return new ParseRawReportOutcome(ParseRawReportOutcome.Parsed, Events: inserted.Count);
    }

    /// <summary>
    /// The report that mapped nothing (every testcase unmapped, or a document with no testcase): no slot, no event and
    /// nothing for the reconciler. The row still leaves, so a document that mapped nothing is distinguishable from one
    /// that was never parsed.
    /// </summary>
    /// <param name="scope">The write scope this attempt opened.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The request, its run and its pinned release.</param>
    /// <param name="report">The report's own row.</param>
    /// <param name="cancellationToken">Cancels the write and the commit.</param>
    /// <returns>The parsed outcome, with no events.</returns>
    private async Task<ParseRawReportOutcome> WriteNothingMappedAsync(
        ITenantScope scope,
        Guid orgId,
        Prepared prepared,
        RawReportDraft report,
        CancellationToken cancellationToken)
    {
        if (await StoreParsedRowAsync(orgId, prepared, report, cancellationToken).ConfigureAwait(false)
            is { } conflicted)
        {
            return conflicted;
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogMapped(logger, prepared.ParsingId, 0, 0);

        return new ParseRawReportOutcome(ParseRawReportOutcome.Parsed, Events: 0);
    }

    /// <summary>
    /// The write scope's opening checks, cheapest first: the artifact is still this run's ready raw report, the run
    /// exists under its own lock, and the shared admission policy accepts the evidence. A refusal here has written
    /// nothing, so disposing the scope rolls back nothing.
    /// </summary>
    /// <param name="context">The tenant write model created inside the scope.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The request, its artifact and its run.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The admission, or the skipped outcome that ended the attempt.</returns>
    private async Task<(RunAdmission? Admission, ParseRawReportOutcome? Stopped)> AdmitAsync(
        EvidenceDbContext context,
        Guid orgId,
        Prepared prepared,
        CancellationToken cancellationToken)
    {
        // Read again inside the write scope rather than trusting the preparation: a concurrent attempt may have decided
        // the artifact while the bytes were mapped, and a report that left `ready` is out of evidence.
        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == prepared.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is null
            || !string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal)
            || !string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal)
            || artifact.RunId != prepared.RunId)
        {
            LogSkipped(logger, prepared.ParsingId, ArtifactLeftReady);
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, ArtifactLeftReady));
        }

        // The run lock serialises the writes of one run, so the slot's next shard index cannot be taken twice, and it
        // also answers the admission facts under a lock: the run's and its project's state, and the late window.
        var run = await db
            .QuerySingleOrDefaultAsync<RunGate>(RunGateSql, new { org_id = orgId, run_id = prepared.RunId }, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            LogSkipped(logger, prepared.ParsingId, "run_absent");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "run_absent"));
        }

        var admission = ReprocessAdmission.Decide(run.ProjectState, run.RunState, run.LateWindowOpen);
        if (!admission.Allowed)
        {
            // Terminal: a closed run or a project that is going away will not admit this evidence on a retry either.
            LogSkipped(logger, prepared.ParsingId, admission.Refusal!);
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
    /// <param name="prepared">The request, its run and its synthetic producer identity.</param>
    /// <param name="events">The serialized, hashed event set built before this scope opened.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The ids this attempt inserted (empty on a replay), or the skipped outcome that ended it.</returns>
    private async Task<(HashSet<Guid>? Inserted, ParseRawReportOutcome? Stopped)> InsertEventSetAsync(
        RunAdmission admission,
        Guid orgId,
        Prepared prepared,
        SerializedEventSet events,
        CancellationToken cancellationToken)
    {
        var shardIndex = await db
            .ScalarAsync<int?>(NextShardIndexSql, new { org_id = orgId, run_id = prepared.RunId }, cancellationToken)
            .ConfigureAwait(false);

        if (shardIndex is null or > MaxShardIndex)
        {
            // Every shard index is taken, so no slot can name this synthetic producer. Retrying cannot free one; the
            // attempt is reported for an operator instead of silently dropping the report.
            LogSkipped(logger, prepared.ParsingId, "no_producer_slot");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "no_producer_slot"));
        }

        await InsertSlotAsync(orgId, prepared, shardIndex.Value, events, cancellationToken).ConfigureAwait(false);

        var insertedIds = (await db.QueryAsync<Guid>(
                InsertEventsSql,
                new
                {
                    org_id = orgId,
                    run_id = prepared.RunId,
                    after_cancel = admission.AfterCancel,
                    late = admission.Late,
                    events = events.Json,
                },
                cancellationToken).ConfigureAwait(false))
            .ToHashSet();

        if (insertedIds.Count < events.Events.Length
            && !await ReplayMatchesAsync(orgId, prepared, events, insertedIds, cancellationToken).ConfigureAwait(false))
        {
            // The transaction rolls back on dispose: the conflicting set is never half-written. The reason is named so
            // the operator can find the request; retrying the same bytes will refuse the same way.
            LogSkipped(logger, prepared.ParsingId, "duplicate_different_payload");
            return (null, new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "duplicate_different_payload"));
        }

        return (insertedIds, null);
    }

    /// <summary>
    /// The synthetic producer's slot, written closed and declared in one statement with no token binding: no producer
    /// will ever finalize a reprocess, so an open slot would hold the reconciler on a shard that can never close, and
    /// 0035's three-column check is satisfied because the declaration and the close are written together.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The request, its run and its synthetic producer identity.</param>
    /// <param name="shardIndex">The free shard index the slot takes.</param>
    /// <param name="events">The serialized set whose declaration the slot states.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task InsertSlotAsync(
        Guid orgId,
        Prepared prepared,
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
                    run_id = prepared.RunId,
                    shard_index = shardIndex,
                    producer_id = prepared.ProducerId,
                    bound_token_jti = SyntheticBinding,
                    last_sequence = events.MaxSequence,
                    declared_last_sequence = events.MaxSequence,
                    declared_count = events.DeclaredCount,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Compares the stored payload hash of every event this attempt could not insert (the replay case) with the hash it
    /// computed for the same identity. Only an exact match may be a no-op; a mismatch — and an identity held by a
    /// different event — refuses the attempt.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The run the rows are read under.</param>
    /// <param name="events">The serialized set this attempt built.</param>
    /// <param name="insertedIds">The event ids this attempt inserted.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when every stored row carries this attempt's hash.</returns>
    private async Task<bool> ReplayMatchesAsync(
        Guid orgId,
        Prepared prepared,
        SerializedEventSet events,
        HashSet<Guid> insertedIds,
        CancellationToken cancellationToken)
    {
        var stored = (await db.QueryAsync<StoredEventRow>(
                StoredEventHashesSql,
                new
                {
                    org_id = orgId,
                    run_id = prepared.RunId,
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
    /// Writes one attempt per newly inserted finish through the shared writer, in batches the writer accepts, and only
    /// for executions no earlier report already attempted: this report speaks for a plan that may already carry a
    /// verdict, and a second verdict on one execution would overwrite a standing one. A refusal is reported to the
    /// caller, which then commits nothing.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="prepared">The run the attempts are written under.</param>
    /// <param name="events">The serialized set, whose attempt inputs were built with the rows.</param>
    /// <param name="insertedIds">The event ids this attempt inserted.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>True when the writer accepted every attempt.</returns>
    private async Task<bool> WriteAttemptsAsync(
        Guid orgId,
        Prepared prepared,
        SerializedEventSet events,
        HashSet<Guid> insertedIds,
        CancellationToken cancellationToken)
    {
        var inputs = events.Events
            .Where(item => insertedIds.Contains(item.Row.EventId) && item.Attempt is not null)
            .Select(item => item.Attempt!)
            .ToArray();
        if (inputs.Length == 0)
        {
            return true;
        }

        var covered = (await db.QueryAsync<string>(
                CoveredExecutionsSql,
                new { org_id = orgId, run_id = prepared.RunId },
                cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        // One attempt per execution, and only for the executions no earlier report attempted: the writer holds one
        // verdict per (run, execution, attempt), and re-stating a standing verdict is not this attempt's business.
        var pending = inputs
            .Where(input => !covered.Contains(input.ExecutionKey))
            .DistinctBy(input => input.ExecutionKey, StringComparer.Ordinal)
            .ToArray();
        if (pending.Length == 0)
        {
            return true;
        }

        await using var attemptScope = scopeFactory.CreateAsyncScope();
        var attempts = attemptScope.ServiceProvider.GetRequiredService<ICaseAttemptWriter>();

        for (var offset = 0; offset < pending.Length; offset += ICaseAttemptWriter.MaxBatchSize)
        {
            var batch = pending
                .Skip(offset)
                .Take(ICaseAttemptWriter.MaxBatchSize)
                .ToArray();
            var outcomes = await attempts
                .WriteAttemptsAsync(prepared.RunId, batch, cancellationToken)
                .ConfigureAwait(false);

            for (var index = 0; index < outcomes.Count; index++)
            {
                if (!outcomes[index].IsRefusal)
                {
                    continue;
                }

                LogAttemptRefused(logger, prepared.ParsingId, outcomes[index].Status.ToString());
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Whether a run still accepts a <em>reprocess</em>, as distinct from whether it still accepts new ingest evidence.
/// <para>
/// It shares the intake policy's verdict everywhere except one case: a run that finished <c>incomplete</c> stays open to a
/// reprocess after its late window closed. That window bounds what an ingest route or a parse task may still add to a run
/// the clock already judged; a reprocess is a deliberate re-reading of bytes the run itself holds, and it is the one path
/// that can turn an incomplete verdict into an evidence-backed one. A <c>complete</c> run stays closed, and a deleted or
/// suspended project refuses as it does for every writer.
/// </para>
/// </summary>
internal static class ReprocessAdmission
{
    /// <summary>Decides one reprocess against the run's and its project's state.</summary>
    /// <param name="projectState">The project's state: <c>active</c>, <c>suspended</c> or <c>deleted</c>.</param>
    /// <param name="runState">The run's state (0031's closed vocabulary).</param>
    /// <param name="lateWindowOpen">True when <c>now()</c> is at or before the run's late-window end.</param>
    /// <returns>The decision, with the rows' <c>after_cancel</c>/<c>late</c> markers already resolved.</returns>
    public static RunAdmission Decide(string projectState, string runState, bool lateWindowOpen)
    {
        var intake = RunAdmissionPolicy.Decide(projectState, runState, lateWindowOpen);
        if (intake.Allowed || !string.Equals(intake.Refusal, RunAdmissionPolicy.RunClosed, StringComparison.Ordinal))
        {
            return intake;
        }

        // Closed because the clock ran out, not because the evidence is final: the run stays where it is and the attempt's
        // rows are marked late, so the freeze path records a new revision instead of moving the run.
        return string.Equals(runState, "incomplete", StringComparison.Ordinal)
            ? new RunAdmission(Allowed: true, Refusal: null, AfterCancel: false, Late: true)
            : intake;
    }
}
