using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tasks;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The deferred-report half of <see cref="ParseRawReportHandler"/>: the queue's own sign that it already waited for a
/// report, read alone by the skip line and folded with the run's admission facts for the pre-check.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>
    /// The deferred probe and the run's admission facts in one statement: the pre-check needs the run only for a report
    /// the queue already deferred, so the run join is attached to the probe's answer and a first attempt still pays
    /// the probe alone. The facts are read unlocked: this answer only chooses between waiting and the terminal refusal
    /// the write decides, and a run that cannot be read leaves them null so the attempt names its absence the way it
    /// always did.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run and the report artifact the probe key derives from.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether the report is deferred and, when it is, the run's facts.</returns>
    private async Task<DeferredRunFacts> ReadDeferredRunAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        var row = await db
            .QuerySingleOrDefaultAsync<DeferredRunRow>(
                DeferredRunSql,
                new
                {
                    org_id = orgId,
                    run_id = payload.RunId,
                    idempotency_key = ReportParseKey(payload.ArtifactId),
                    last_error = TaskDeferredException.PersistedError,
                },
                cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("the deferred probe returned no row");

        return new DeferredRunFacts(
            row.Deferred,
            row is { RunState: { } state, ProjectState: { } projectState, LateWindowOpen: { } late, ProjectId: { } projectId }
                ? new RunGate(state, projectState, late, projectId)
                : null);
    }

    /// <summary>The deferred probe with the run facts joined inside a lateral whose own filter is the probe, so a first attempt never reaches the run; no lock is taken.</summary>
    private const string DeferredRunSql = $"""
        with deferred as (select {DeferredProbeSql})
        select d.deferred as "Deferred",
               f."RunState", f."ProjectState", f."LateWindowOpen", f."ProjectId"
          from deferred d
          left join lateral ({RunFactsSql}
           and d.deferred
         offset 0) f on true
        """;

    /// <summary>The raw columns of <see cref="DeferredRunSql"/>; the run columns are null for a first attempt or an absent run.</summary>
    private sealed record DeferredRunRow(
        bool Deferred,
        string? RunState,
        string? ProjectState,
        bool? LateWindowOpen,
        Guid? ProjectId);

    /// <summary>What the pre-check's one read answered.</summary>
    /// <param name="Deferred">True when the queue already deferred this report.</param>
    /// <param name="Run">The run's facts, or null when the report is not deferred or the run is not readable.</param>
    private sealed record DeferredRunFacts(bool Deferred, RunGate? Run);

    /// <summary>The telemetry kind of a deferred report a closed run then skipped: a loss the plain refusal word hides.</summary>
    private const string BlockedThenClosed = "quota_blocked_then_closed";

    /// <summary>
    /// Whether this report's task row carries a deferral, the only sign the queue has already waited for this report:
    /// the row's idempotency key is derived from the outbox row (<c>'outbox:' || outbox.id</c>), so the probe joins
    /// the two on that derivation and both sides are unique-index lookups.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="artifactId">The report artifact the key derives from.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>True when the task row's last error is the deferral's own fixed text.</returns>
    private async Task<bool> IsDeferredAsync(Guid orgId, Guid artifactId, CancellationToken cancellationToken) =>
        await db
            .QuerySingleOrDefaultAsync<bool>(
                DeferredTaskSql,
                new
                {
                    org_id = orgId,
                    idempotency_key = ReportParseKey(artifactId),
                    last_error = TaskDeferredException.PersistedError,
                },
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The probe the skip line asks alone and the pre-check folds into <see cref="DeferredRunSql"/>.</summary>
    private const string DeferredTaskSql = "select " + DeferredProbeSql;

    /// <summary>
    /// The deferred-report probe as one boolean expression named <c>deferred</c>. Both
    /// sides are the scoped views, so the predicate that narrows them is the caller's own
    /// session scope rather than a table grant.
    /// </summary>
    private const string DeferredProbeSql = """
        exists (
            select 1
            from public.outbox_in_scope as o
            join public.task_in_scope as t
              on t.idempotency_key = 'outbox:' || o.id::text
             and t.org_id = o.org_id
            where o.org_id = @org_id
              and o.idempotency_key = @idempotency_key
              and t.last_error = @last_error) as deferred
        """;

    /// <summary>
    /// Logs a terminal admission refusal on the refusal path only, naming a loss the plain code word hides: a report
    /// the queue had deferred and the run closed before its retry.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="artifactId">The report artifact, the only id the line names.</param>
    /// <param name="reason">The admission's own code word, which the outcome record keeps.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    private async Task LogAdmissionSkipAsync(
        Guid orgId,
        Guid artifactId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!await IsDeferredAsync(orgId, artifactId, cancellationToken).ConfigureAwait(false))
        {
            LogSkipped(logger, artifactId, reason);

            return;
        }

        EvidgateTelemetry.RecordIngestQuotaDenied(BlockedThenClosed);
        LogBlockedThenClosed(logger, artifactId, BlockedThenClosed, reason);
    }

    /// <summary>The idempotency key the upload enqueues this report's task with, the row the probe looks for.</summary>
    private static string ReportParseKey(Guid artifactId) =>
        $"evidence:artifact:{artifactId:D}:{EvidenceTaskTypes.ReportParse}";
}
