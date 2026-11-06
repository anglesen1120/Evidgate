using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// What one freeze of a run's evidence produced: the completeness it persisted, the revision it appended and that
/// revision's <c>revision_no</c> (1 for the first). The handler logs completeness and <c>revision_no</c> only.
/// </summary>
/// <param name="Completeness">The 00 §8.5 completeness the row carries: <c>complete</c> or <c>incomplete</c>.</param>
/// <param name="RevisionId">The <c>evidence_revision.id</c> the run's pointer now names.</param>
/// <param name="RevisionNo">The <c>revision_no</c> the insert allocated (<c>1</c> for the first revision of a run).</param>
internal sealed record ReconcileFinalization(string Completeness, Guid RevisionId, int RevisionNo);

/// <summary>
/// The run's newest <c>evidence_revision</c> as the late-append guard reads it: enough to answer for an attempt whose
/// evidence the recorded revision already counts, so a redelivered attempt appends nothing.
/// </summary>
internal sealed record LatestRevision(
    Guid Id,
    int RevisionNo,
    string Completeness,
    long EventWatermark,
    string? ArtifactSetHash);

/// <summary>Reads the project and manifest together so the revision and its event describe the same run.</summary>
internal sealed record RevisionSource(Guid ProjectId, string? ManifestHash);

/// <summary>
/// The second half of one reconcile attempt: after <see cref="IRunReconciler"/> has locked the run
/// <c>FOR NO KEY UPDATE</c> and compared the evidence, the finisher freezes the artifact set, appends ONE
/// <c>evidence_revision</c> row, moves the run's pointer to it, announces that revision in the outbox and moves
/// <c>collecting → complete|incomplete</c> through the guarded run-state transition — all in the caller's ambient
/// tenant transaction, which the consumer commits only when the whole attempt returns.
/// <para>
/// It never opens, commits or rolls back a transaction (00 §5.5 C3): the caller's rollback undoes the revision, the
/// pointer move, the outbox row and the state change together, so a failed attempt leaves no half-written revision and a
/// retry starts from the state it began in. Three settlements reach this type: the collection decision of a
/// <c>collecting</c> run, the late revision of one already <c>incomplete</c> and the reprocessed revision of a
/// <c>canceled</c> one. Either way one run appends at most one
/// revision per attempt and never rewrites one (00 §8.5 C6/AC9).
/// </para>
/// </summary>
internal interface IReconcileFinisher
{
    /// <summary>
    /// Freezes the evidence of <paramref name="runId"/> and persists the revision of the comparison.
    /// </summary>
    /// <param name="runId">The run the reconciler just compared; its row is already locked by this transaction.</param>
    /// <param name="comparison">The event-evidence result of the attempt (never null; only a compared outcome has one).</param>
    /// <param name="settlement">
    /// Which settled run the comparison belongs to: <see cref="ReconcileSettlement.DecideCollection"/> and
    /// <see cref="ReconcileSettlement.CloseOverdue"/> move the run state, <see cref="ReconcileSettlement.AppendLateEvidence"/>
    /// and <see cref="ReconcileSettlement.AppendReprocessEvidence"/> leave it alone.
    /// </param>
    /// <param name="cancellationToken">Cancelled on shutdown or lease loss.</param>
    /// <returns>The persisted completeness and revision.</returns>
    /// <exception cref="InvalidOperationException">
    /// Fail closed, never a partial revision: <see cref="IEvidenceFreeze.FreezeAsync"/> refused the freeze (an
    /// unresolved or vanished artifact, a short resolution <c>UPDATE</c>, a run above
    /// <see cref="FreezeRequest.MaxReadyArtifacts"/>), the run's manifest hash cannot be read, the pointer update
    /// touched no row, or the state transition failed. The caller MUST let the exception escape so the consumer rolls
    /// the whole attempt back and retries it (docs/status-carried.md:116/117 fail-closed). A requirement set above
    /// <see cref="FreezeRequest.MaxRequired"/> is NOT an exception: it resolves as unsatisfiable and becomes an
    /// <c>artifact_missing</c> reason, so the attempt records an <c>incomplete</c> revision instead of retrying forever.
    /// </exception>
    ValueTask<ReconcileFinalization> FinishAsync(
        Guid runId,
        ReconcileComparison comparison,
        ReconcileSettlement settlement,
        CancellationToken cancellationToken);
}

/// <summary>What one finished attempt settles over the run's evidence.</summary>
internal enum ReconcileSettlement
{
    /// <summary>
    /// The run is <c>collecting</c>: this revision decides it, so the attempt moves the run to
    /// <c>complete</c> or <c>incomplete</c> after the pointer move.
    /// </summary>
    DecideCollection,

    /// <summary>
    /// The run already finished as <c>incomplete</c> and late evidence arrived: this revision records it, and the run
    /// state stays where it is — a finished run has no forward edge, so the revision alone carries the new completeness.
    /// </summary>
    AppendLateEvidence,

    /// <summary>
    /// The run was <c>canceled</c> and a reprocess its admission accepted re-read its own stored report: this revision
    /// records that evidence, and the run state stays <c>canceled</c> — a canceled run is terminal and never becomes
    /// complete (00 §8.1). Same append as <see cref="AppendLateEvidence"/>: one revision, no edge.
    /// </summary>
    AppendReprocessEvidence,

    /// <summary>
    /// The run is <c>queued</c> or <c>running</c> and its collection deadline has passed, so this revision is its
    /// verdict: the attempt closes it <c>incomplete</c> through the declared <c>queued|running → incomplete</c> edge.
    /// It never reads <c>complete</c> — the run never reached collecting, so its evidence proves nothing about the plan —
    /// and its <c>missing[]</c> is never empty: a comparison that named nothing still records what is absent
    /// (<c>missing_shard</c> while a slot exists, <c>missing_result</c> when the plan has none).
    /// </summary>
    CloseOverdue,
}

/// <summary>
/// The Dapper implementation of <see cref="IReconcileFinisher"/>: resolve, freeze, compute, insert, move the pointer,
/// announce the revision, and — for a collecting run — transition, in that order.
/// <para>
/// <b>Completeness</b> (00 §8.5, AC4): <c>complete</c> only when no reason was named, the run planned at least one shard
/// and every planned shard closed. Missing evidence never reads as complete, and a run with zero planned shards is
/// <c>incomplete</c> with no reason: nothing was proven, so nothing is claimed (fail closed). The comparison already
/// names a planned execution that recorded no attempt, so a producer that finalized with a matching declared count can
/// never complete a run whose plan says more cases ran than its attempts prove. A forced close
/// (<see cref="ReconcileSettlement.CloseOverdue"/>) is never complete and never reasonless, whatever the comparison and
/// the freeze answered — the run never reached collecting, so its evidence proves nothing about the plan.
/// </para>
/// <para>
/// <b>missing[]</b>: the comparison's codes in the order the comparer produced them (producer id, then code, then
/// detail ordinal), followed by the freeze's own reasons in the order <see cref="FreezeResult.Missing"/> gives them
/// (orphans first, then unmet requirements — <c>artifact_missing</c> for either, or the requirement's own code), and
/// finally one <c>artifact_missing</c> when the requirement set was unsatisfiable (the plan times the policy exceeded the
/// freeze bound, <see cref="RequiredSet.Unsatisfiable"/>).
/// <b>Codes are de-duplicated, first occurrence kept</b> (PerfRev P3): the vocabulary is the closed 00 §8.5 list, so a
/// hundred orphans, an unmet requirement per execution or a planned execution per case all add up to the one code that
/// says the same thing, while the revision's per-producer and per-execution detail stays in the comparison the reconciler
/// reports. The result is deterministic because both halves are.
/// </para>
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
/// <param name="scopes">The tenant-scope accessor; <c>Require()</c> is the guard that throws before any statement.</param>
/// <param name="requirements">The requirement seam (D-S123-Q3: the pin's policy kinds for every planned execution).</param>
/// <param name="freeze">06.5.1's freeze call, which runs inside this transaction and never opens one of its own.</param>
/// <param name="runStates">05.3.1's guarded transition; it runs after the pointer move, under the lock this transaction holds.</param>
/// <param name="audit">
/// The audit buffer of this attempt. The transition describes and records the <c>run.state_changed</c> edge, which
/// needs an open unit; a task consumer has no pipeline unit (the tenant transaction is the consumer's, not a mediator
/// pipeline's), so the finisher opens one and flushes it before it returns — the
/// <see cref="Evidgate.Modules.Identity.MembershipBinder"/> / <c>IdentityAuditWriter</c> pattern of a component that
/// audits its own write outside the pipeline. The flush runs in the consumer's transaction, so the audit row and the
/// revision commit or roll back together.
/// </param>
/// <param name="outbox">
/// Shares the revision's transaction so consumers cannot observe an event for a rolled-back revision.
/// </param>
internal sealed class ReconcileFinisher(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequiredArtifactResolver requirements,
    IEvidenceFreeze freeze,
    IRunStateTransition runStates,
    IAuditTrail audit,
    IOutboxWriter outbox) : IReconcileFinisher
{
    /// <summary>The 00 §8.5 completeness of a revision whose evidence is whole (0049's CHECK pins the vocabulary).</summary>
    internal const string Complete = "complete";

    /// <summary>The 00 §8.5 completeness of a revision that names at least one reason, or that proved nothing.</summary>
    internal const string Incomplete = "incomplete";

    /// <inheritdoc />
    public async ValueTask<ReconcileFinalization> FinishAsync(
        Guid runId,
        ReconcileComparison comparison,
        ReconcileSettlement settlement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var orgId = scopes.Require().OrgId;

        // The attempt's audit buffer: the transition below records the run.state_changed edge, and a task consumer has no
        // pipeline unit to inherit. A nested BeginUnit joins an open unit, and the flush below appends in this transaction.
        using var auditUnit = audit.BeginUnit();

        // (a) The requirement set the pinned policy names for every planned execution. An empty set is legitimate; a set
        // over the freeze bound arrives unsatisfiable with no specs, so the freeze below is asked for nothing and the
        // reason list below names the one artifact_missing that keeps the revision incomplete (never a truncated set).
        var required = await requirements.ResolveAsync(runId, cancellationToken).ConfigureAwait(false);

        // (b) The freeze: one read, at most one resolution UPDATE, in this transaction. Its exceptions (a short UPDATE, a
        // vanished artifact, a run above MaxReadyArtifacts) are deliberately not caught: the attempt must roll back and
        // retry, never persist a revision that disagrees with the rows (IEvidenceFreeze's fail-closed contract). The one
        // exception is a requirement set the freeze refuses by shape: Validate runs before the first statement, so the
        // transaction is untouched when it throws, and the set cannot become satisfiable however the evidence arrives
        // (an unknown artifact kind in the pinned policy body — the closed kind list lives in the Evidence module and is
        // not exported, so this module cannot pre-filter). The attempt then freezes with no requirements and records the
        // one artifact_missing marker below, instead of failing the attempt until the queue dead-letters it.
        var unsatisfiable = required.Unsatisfiable;
        FreezeResult frozen;
        try
        {
            frozen = await freeze
                .FreezeAsync(new FreezeRequest(runId, required.Specs), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException refused) when (!unsatisfiable && required.Specs.Count > 0 && refused.ParamName == "request")
        {
            unsatisfiable = true;
            frozen = await freeze
                .FreezeAsync(new FreezeRequest(runId, []), cancellationToken)
                .ConfigureAwait(false);
        }

        // (c) The codes, in the stable order: event reasons first (as compared), then the freeze's own reasons, then the
        // unsatisfiable marker. De-duplicated with the first occurrence kept, because the vocabulary is the closed 00 §8.5
        // list and a repeated code says nothing a single one does not (PerfRev P3).
        var missing = new List<string>(comparison.Missing.Count + frozen.Missing.Count + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < comparison.Missing.Count; index++)
        {
            AddCode(missing, seen, comparison.Missing[index].Code);
        }

        for (var index = 0; index < frozen.Missing.Count; index++)
        {
            AddCode(missing, seen, frozen.Missing[index].Code);
        }

        if (unsatisfiable)
        {
            AddCode(missing, seen, ReconcileMissingCodes.ArtifactMissing);
        }

        // (d) A run the deadline closed never reads complete and never carries an empty missing[]: the run never reached
        // collecting, so its evidence proves nothing about the plan. The one reason such a comparison can lack is added
        // here, whichever way the comparison came out.
        AddForcedReason(comparison, settlement, missing, seen);

        var isComplete = settlement != ReconcileSettlement.CloseOverdue
            && missing.Count == 0
            && comparison.ShardsPlanned > 0
            && comparison.ShardsClosed == comparison.ShardsPlanned;
        var completeness = isComplete ? Complete : Incomplete;

        var parameters = new { org_id = orgId, run_id = runId };

        // (e) An append is idempotent against its own evidence. The consumer commits this transaction and only then
        // completes the task row, so a crash in between re-claims the same attempt; appending a second, identical
        // revision would break "one accepted batch, one revision". Equal evidence answers with the revision already
        // recorded instead of a copy of it. Both append settlements share this half: the run they leave alone is the
        // only difference between late evidence and reprocessed evidence, and the append itself is the same write.
        if (AppendsEvidence(settlement))
        {
            var recorded = await LatestRevisionCoversAsync(orgId, runId, comparison, frozen, cancellationToken)
                .ConfigureAwait(false);

            if (recorded is not null)
            {
                return recorded;
            }
        }

        // (f) The manifest hash the revision records and the project the frozen event names, read from the run's own row
        // and its manifest, tenant-first.
        var source = await db
            .QuerySingleOrDefaultAsync<RevisionSource>(
                EvidenceRevisionSql.SelectRevisionSource,
                parameters,
                cancellationToken)
            .ConfigureAwait(false);

        if (source is not { ManifestHash: { } manifestHash })
        {
            throw new InvalidOperationException(
                $"run {runId:D} has no manifest visible in this tenant: a revision cannot be recorded without its hash");
        }

        var revisionId = Guid.CreateVersion7();
        var revisionNo = await db
            .QuerySingleOrDefaultAsync<int>(
                EvidenceRevisionSql.InsertRevision,
                new
                {
                    id = revisionId,
                    org_id = orgId,
                    run_id = runId,
                    completeness,
                    // One delimited string: the 00 §8.5 codes are [a-z0-9_] and the tenant Dapper path refuses a client
                    // string[] by design (see EvidenceRevisionSql.InsertRevision).
                    missing = string.Join(',', missing),
                    artifact_set_hash = frozen.ArtifactSetHash,
                    event_watermark = comparison.EventWatermark,
                    manifest_hash = manifestHash,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (revisionNo < 1)
        {
            throw new InvalidOperationException(
                $"the evidence_revision insert of run {runId:D} answered no revision number");
        }

        // (g) The pointer move, in the same transaction and after the insert: 0049's guard refuses anything but the latest
        // revision of this run, so a short update (no row) is an integrity failure, not a silent no-op.
        var moved = await db
            .ExecuteAsync(
                EvidenceRevisionSql.UpdateRunPointer,
                new { revision_id = revisionId, org_id = orgId, run_id = runId },
                cancellationToken)
            .ConfigureAwait(false);

        if (moved != 1)
        {
            throw new InvalidOperationException(
                $"run {runId:D} did not accept the evidence pointer in this tenant ({moved} row(s) updated)");
        }

        // (h) Same transaction as the revision it announces, so rollback leaves no event; keyed by revision,
        // so a redelivered attempt cannot announce one revision twice.
        await outbox.EnqueueAsync(
                EvidenceRevisionFrozenContract.OutboxTaskType,
                EvidenceRevisionFrozenContract.PayloadJson(
                    orgId, source.ProjectId, runId, revisionId, revisionNo),
                EvidenceRevisionFrozenContract.AggregateType,
                revisionId,
                EvidenceRevisionFrozenContract.IdempotencyKey(revisionId),
                cancellationToken)
            .ConfigureAwait(false);

        // (i) The state edge, last: collecting -> complete|incomplete from a collection decision, queued|running ->
        // incomplete from a forced close, both through the guarded run-state transition (it re-takes the run row this
        // transaction already holds FOR NO KEY UPDATE, which is a no-op for the holder). An applied edge is credited to
        // the system actor, which is what a task consumer is, and its outbox row and audit row join this unit of work;
        // a run that left queued/running meanwhile answers NoOp, not an applied edge. An append — late evidence into an
        // incomplete run, reprocessed evidence into a canceled one — never moves the state: both terminal states are the
        // run's last word, so the revision, not the state, is what a later reader follows. `incomplete -> complete` and
        // `canceled -> *` are not edges the machine allows, and inventing one would let new evidence rewrite a finished
        // verdict.
        if (!AppendsEvidence(settlement))
        {
            var transition = await runStates
                .TransitionAsync(
                    new TransitionRunStateCommand(runId, isComplete ? RunStates.Complete : RunStates.Incomplete),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!transition.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"run {runId:D} did not move to {(isComplete ? RunStates.Complete : RunStates.Incomplete)}: "
                    + $"{transition.Error!.Code} {transition.Error.Message}");
            }
        }

        // The audit row of that edge joins the same transaction: the consumer's commit makes the revision, the pointer, the
        // frozen event and the audit entry durable together, and a failed attempt rolls all four back. A late attempt
        // describes no edge, so its unit flushes nothing.
        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);

        return new ReconcileFinalization(completeness, revisionId, revisionNo);
    }

    /// <summary>
    /// Whether this settlement only appends a revision: the two append settlements record evidence on a run that already
    /// holds its verdict, so they share the idempotency guard and leave the state alone. Every other settlement decides
    /// the run through a declared edge.
    /// </summary>
    /// <param name="settlement">How the attempt settles the run.</param>
    private static bool AppendsEvidence(ReconcileSettlement settlement) =>
        settlement is ReconcileSettlement.AppendLateEvidence or ReconcileSettlement.AppendReprocessEvidence;

    /// <summary>
    /// The latest revision of the run when it already counts exactly this evidence, or null when the attempt must append.
    /// Both halves must match: the watermark counts the run's stored events, and the artifact-set hash catches a
    /// re-freeze that resolved an artifact with no event moving — accepting either one alone would drop real evidence.
    /// </summary>
    /// <param name="orgId">The tenant of this attempt, from the scope it runs in.</param>
    /// <param name="runId">The run whose latest revision is read.</param>
    /// <param name="comparison">The comparison this attempt would persist.</param>
    /// <param name="frozen">The freeze result this attempt would persist.</param>
    /// <param name="cancellationToken">Cancelled on shutdown or lease loss.</param>
    private async ValueTask<ReconcileFinalization?> LatestRevisionCoversAsync(
        Guid orgId,
        Guid runId,
        ReconcileComparison comparison,
        FreezeResult frozen,
        CancellationToken cancellationToken)
    {
        var latest = await db
            .QuerySingleOrDefaultAsync<LatestRevision>(
                EvidenceRevisionSql.SelectLatestRevision,
                new { org_id = orgId, run_id = runId },
                cancellationToken)
            .ConfigureAwait(false);

        if (latest is null
            || latest.EventWatermark != comparison.EventWatermark
            || !string.Equals(latest.ArtifactSetHash, frozen.ArtifactSetHash, StringComparison.Ordinal))
        {
            return null;
        }

        return new ReconcileFinalization(latest.Completeness, latest.Id, latest.RevisionNo);
    }

    /// <summary>
    /// Records the reason a forced close can never omit. The run never reached collecting, so a comparison that named
    /// nothing still says what is absent: <c>missing_shard</c> while the plan holds a slot, <c>missing_result</c> when it
    /// holds none — both closed-list codes, so neither invents a reason the CHECK would refuse.
    /// </summary>
    /// <param name="comparison">The comparison this attempt persists.</param>
    /// <param name="settlement">How the attempt settles the run; only a forced close adds the reason.</param>
    /// <param name="missing">The codes kept so far, in order.</param>
    /// <param name="seen">The set of the codes kept so far.</param>
    private static void AddForcedReason(
        ReconcileComparison comparison,
        ReconcileSettlement settlement,
        List<string> missing,
        HashSet<string> seen)
    {
        if (settlement != ReconcileSettlement.CloseOverdue || missing.Count > 0)
        {
            return;
        }

        AddCode(
            missing,
            seen,
            comparison.ShardsPlanned > 0 ? ReconcileMissingCodes.MissingShard : ReconcileMissingCodes.MissingResult);
    }

    /// <summary>
    /// Appends <paramref name="code"/> unless an equal code was already kept, so the stored <c>missing[]</c> is the
    /// de-duplicated first-seen order of every reason this attempt named (PerfRev P3).
    /// </summary>
    /// <param name="missing">The codes kept so far, in order.</param>
    /// <param name="seen">The set of the codes kept so far.</param>
    /// <param name="code">The code this reason names (one of the closed 00 §8.5 literals).</param>
    private static void AddCode(List<string> missing, HashSet<string> seen, string code)
    {
        if (seen.Add(code))
        {
            missing.Add(code);
        }
    }
}
