using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The quota half of <see cref="ParseRawReportHandler"/>: the events one report attempt inserted reserve the reporting
/// project's billing period before they are metered, so a report can never commit evidence the project's counter has no
/// room for.
/// <para>
/// The question is the ingest route's, once per report batch and only for the events this attempt inserted: a replay
/// inserted nothing and reserves nothing. A block leaves the transaction uncommitted — the events, the slot, the
/// attempts, the run's transition and its audit entries — and the report's own row is never written.
/// </para>
/// <para>
/// The gate stamps the refusal inside this transaction, so the stamp rolls back with it; the caller writes that stamp
/// again in a scope of its own once the transaction is gone, because a refusal nobody can see is not diagnosable.
/// </para>
/// <para>
/// A report the queue already deferred is asked once more, before its bytes are fetched: a counter with no room for a
/// single event cannot admit the batch whatever the document holds, so the attempt waits again instead of spending the
/// lease on a read and a parse that would roll back. That answer is advisory only — it never admits, writes or stamps —
/// and a run the admission already refuses is not asked at all, because its own refusal is the loss to report.
/// </para>
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>The telemetry kind of a report batch refused for quota, beside the ingest route's <c>quota_exceeded</c>.</summary>
    private const string ReportQuotaExceeded = "report_quota_exceeded";

    /// <summary>The telemetry kind of a report deferred before it was fetched, so the advisory refusal is its own rate.</summary>
    private const string ReportQuotaPreCheck = "report_quota_precheck";

    /// <summary>The reason code word a quota refusal carries out of the write transaction.</summary>
    private const string QuotaExceededReason = "quota_exceeded";

    /// <summary>
    /// The longest a blocked report may wait. A month-long wait outlives every chance to recover the batch: an
    /// operator raise and the run's late window both need the attempt back before the run closes it as
    /// <c>run_closed</c>, and a blocked report waits for neither side's schedule.
    /// </summary>
    private static readonly TimeSpan MaximumDeferral = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far the deferral is pulled back, as a fraction of it. Many reports of one tenant blocked in the same
    /// instant would otherwise become claimable in the same instant and race for the one unit of room a raise
    /// created, so their wake-ups are spread over the window instead of piled on its start.
    /// </summary>
    private const double DeferralJitterFraction = 0.10;

    /// <summary>The absolute cap of that pull-back: a long deferral spreads over half a minute, never more.</summary>
    private static readonly TimeSpan MaximumDeferralJitter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Asks the gate once for the events this attempt inserted, in the ingest route's request shape. The quantity is
    /// exactly what the batch receipt meters, and the tenant counter is charged too, so an org-wide limit applies.
    /// </summary>
    /// <param name="projectId">The project the run belongs to, read from the run row under its lock.</param>
    /// <param name="artifactId">The report artifact, the only id the refusal line names.</param>
    /// <param name="quantity">The events this attempt inserted; the caller never asks about zero.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>The blocked outcome to answer, or null when the batch may proceed.</returns>
    private async Task<ParseRawReportOutcome?> CheckQuotaAsync(
        Guid projectId,
        Guid artifactId,
        int quantity,
        CancellationToken cancellationToken)
    {
        // The gate is scoped while this handler is a singleton, so it comes from the attempt's own scope and joins the
        // ambient tenant transaction: a block can then roll the batch back, and an exception refuses it unmetered.
        await using var gateScope = scopeFactory.CreateAsyncScope();
        var gate = gateScope.ServiceProvider.GetRequiredService<IQuotaGate>();

        // The period is the transaction's own instant — the one the batch's rows and the ledger row metering them are
        // stamped with — never an application clock, so a batch never gates one month and meters the next.
        var request = new QuotaCheckRequest(
            UsageMetric.Results,
            await gate.CurrentPeriodAsync(cancellationToken).ConfigureAwait(false),
            projectId,
            quantity,
            IncludeTenantScope: true);

        var answer = await gate.CheckAsync(request, cancellationToken).ConfigureAwait(false);

        if (answer.Decision != QuotaDecision.Block)
        {
            // Ok and Warn both proceed: the gate has already enqueued its one warning for a crossing.
            return null;
        }

        EvidgateTelemetry.RecordIngestQuotaDenied(ReportQuotaExceeded);
        LogQuotaBlocked(logger, artifactId, RetrySeconds(answer.RetryAfter));

        return new ParseRawReportOutcome(ParseRawReportOutcome.QuotaBlocked, QuotaExceededReason)
        {
            RetryAfter = answer.RetryAfter,
            QuotaRequest = request,
        };
    }

    /// <summary>
    /// The advisory stop before the bytes are read, for a report the queue has already deferred once: a counter with
    /// no room for even one event cannot admit the batch whatever the document holds, and reading and parsing it only
    /// to roll it back spends the lease on a wait that is already known.
    /// <para>
    /// It decides nothing. <see cref="IQuotaGate.PeekAsync"/> writes no counter, takes no lock and admits nobody, the
    /// stamp is left to the block that already earned it, and an Ok answer only means the transactional gate still
    /// decides after the insert. A first attempt is never probed, so evidence that fits is never refused at the door.
    /// </para>
    /// <para>
    /// It runs in the caller's read-only scope, so the probe, the admission facts and the gate share one connection and
    /// the attempt pays a first-attempt cost of one statement.
    /// </para>
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="projectId">The project whose counter the report's events would charge.</param>
    /// <param name="payload">The run, artifact and producer kind; the artifact is the task row's probe key.</param>
    /// <param name="cancellationToken">Cancels the probe, the admission read and the advisory question.</param>
    /// <returns>The blocked outcome to defer, or null when the report continues to the fetch.</returns>
    private async Task<ParseRawReportOutcome?> PreCheckQuotaAsync(
        Guid orgId,
        Guid projectId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        if (!await IsDeferredAsync(orgId, payload.ArtifactId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (await AdmissionRefusesAsync(orgId, payload.RunId, cancellationToken).ConfigureAwait(false))
        {
            // A closed run refuses this report whatever the counter says, so waiting for room would bury the terminal
            // refusal and the loss it names: the attempt goes on and the write transaction answers it as before.
            return null;
        }

        await using var gateScope = scopeFactory.CreateAsyncScope();
        var gate = gateScope.ServiceProvider.GetRequiredService<IQuotaGate>();
        var request = new QuotaCheckRequest(
            UsageMetric.Results,
            await gate.CurrentPeriodAsync(cancellationToken).ConfigureAwait(false),
            projectId,
            // One event is the smallest batch. A deferred report's real count is unknown before it is parsed, and a
            // counter with no room for one event has no room for the report either.
            1,
            IncludeTenantScope: true);
        var answer = await gate.PeekAsync(request, cancellationToken).ConfigureAwait(false);

        if (answer.Decision != QuotaDecision.Block)
        {
            // Ok, and also a period without a counter yet: only the authoritative check may refuse or admit.
            return null;
        }

        EvidgateTelemetry.RecordIngestQuotaDenied(ReportQuotaPreCheck);
        LogQuotaPreChecked(logger, payload.ArtifactId, RetrySeconds(answer.RetryAfter));

        return new ParseRawReportOutcome(ParseRawReportOutcome.QuotaBlocked, QuotaExceededReason)
        {
            RetryAfter = answer.RetryAfter,
            QuotaRequest = request,
        };
    }

    /// <summary>
    /// Whether the run already refuses evidence, decided by the same rule the write transaction applies
    /// (<see cref="RunAdmissionPolicy"/>) so the pre-check cannot defer a report the admission has already closed off.
    /// The facts are read unlocked: this answer only chooses between waiting and the terminal refusal the write decides,
    /// and a run that cannot be read answers false so the attempt names its absence the way it always did.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="runId">The run the report belongs to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the admission would refuse the evidence.</returns>
    private async Task<bool> AdmissionRefusesAsync(Guid orgId, Guid runId, CancellationToken cancellationToken)
    {
        var run = await db
            .QuerySingleOrDefaultAsync<RunGate>(RunFactsSql, new { org_id = orgId, run_id = runId }, cancellationToken)
            .ConfigureAwait(false);

        return run is not null
            && !RunAdmissionPolicy.Decide(run.ProjectState, run.RunState, run.LateWindowOpen).Allowed;
    }

    /// <summary>
    /// The refusal stamp, written after the rolled-back transaction: a block's own stamp rolls back with the batch, so
    /// the counter an operator must raise would keep no trace of the refusal without this second write. Best effort
    /// only — the refusal already happened, and nothing here admits work.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="outcome">The write's outcome; anything but a block is returned untouched.</param>
    /// <param name="cancellationToken">Cancels the stamp.</param>
    /// <returns>The same outcome.</returns>
    private async Task<ParseRawReportOutcome> StampQuotaBlockAsync(
        Guid orgId,
        ParseRawReportOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.Status != ParseRawReportOutcome.QuotaBlocked || outcome.QuotaRequest is not { } request)
        {
            return outcome;
        }

        await using var stampScope = scopeFactory.CreateAsyncScope();
        try
        {
            await stampScope.ServiceProvider
                .GetRequiredService<IQuotaBlockStamp>()
                .StampAsync(orgId, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The refusal needs no stamp to stand, and losing it must never turn a deferral into a failure. Only the
            // type is logged: the statement's own text can quote counter values and ids.
            LogBlockStampFailed(logger, exception.GetType().Name);
        }

        return outcome;
    }

    /// <summary>
    /// The delay a blocked report defers for: the gate's wait, never under one second, never past the start of the
    /// next UTC billing month, and pulled back by a jitter so the reports a period refused do not all wake together.
    /// Pure given the sample, so the clamped band is asserted without a clock; the handler passes a real sample.
    /// </summary>
    /// <param name="retryAfter">The gate's answer, null or non-positive when it blocked without naming a wait.</param>
    /// <param name="now">The attempt's clock.</param>
    /// <param name="jitterSample">Uniform sample in [0, 1): 0 is the undiminished delay, near 1 the shortest one.</param>
    /// <returns>The clamped and jittered delay.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The sample is outside [0, 1).</exception>
    /// <exception cref="InvalidOperationException">The gate blocked without a usable wait.</exception>
    internal static TimeSpan QuotaRetryDelay(TimeSpan? retryAfter, DateTimeOffset now, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jitterSample);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitterSample, 1);

        if (retryAfter is not { } wait || wait <= TimeSpan.Zero)
        {
            // A block with no wait names no retry either, so the attempt becomes an ordinary failure: the queue's
            // backoff and dead-letter path answer it instead of this handler inventing a delay.
            throw new InvalidOperationException("the quota gate blocked the report batch without a retry delay");
        }

        var utcNow = now.ToUniversalTime();
        var nextPeriod = new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1) - utcNow;
        // The queue's own floor is the lower bound: a shorter wait would only spin the claim loop.
        var floor = TaskDeferredException.MinimumDelay;
        // The shortest of the three bounds: the floor, the month's end, and the cap that brings the attempt back in
        // time for a raise or the run's late window. The floor wins a month that ends within it.
        var ceiling = nextPeriod < MaximumDeferral ? nextPeriod : MaximumDeferral;
        ceiling = ceiling < floor ? floor : ceiling;
        var delay = wait < floor ? floor : wait;
        var clamped = delay > ceiling ? ceiling : delay;

        // Subtractive only: the delay can never pass the ceiling or fall under the floor, so the jitter spreads the
        // wake-ups inside the window the clamp already chose.
        var pullBack = Math.Min(clamped.TotalSeconds * DeferralJitterFraction, MaximumDeferralJitter.TotalSeconds);
        var jittered = clamped - TimeSpan.FromSeconds(pullBack * jitterSample);

        return jittered < floor ? floor : jittered;
    }

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

    /// <summary>The probe both the pre-check and the skip line ask: one equivalent of the deferred-report pair.</summary>
    private const string DeferredTaskSql = """
        select exists (
            select 1
            from public.outbox as o
            join public.task as t
              on t.idempotency_key = 'outbox:' || o.id::text
             and t.org_id = o.org_id
            where o.org_id = @org_id
              and o.idempotency_key = @idempotency_key
              and t.last_error = @last_error)
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

    /// <summary>The gate's wait as whole seconds for the refusal line; zero when it named none.</summary>
    private static int RetrySeconds(TimeSpan? retryAfter) =>
        retryAfter is { } wait ? (int)Math.Ceiling(wait.TotalSeconds) : 0;

    [LoggerMessage(
        EventId = 2467,
        Level = LogLevel.Warning,
        Message = "raw report parse of artifact {ArtifactId} was refused by the project's quota: the inserted events were not committed, the task defers for {RetrySeconds}s")]
    private static partial void LogQuotaBlocked(ILogger logger, Guid artifactId, int retrySeconds);

    [LoggerMessage(
        EventId = 2447,
        Level = LogLevel.Warning,
        Message = "raw report parse of artifact {ArtifactId} was deferred before the fetch: the project's quota has no room for one event, the task defers for {RetrySeconds}s")]
    private static partial void LogQuotaPreChecked(ILogger logger, Guid artifactId, int retrySeconds);

    [LoggerMessage(
        EventId = 2468,
        Level = LogLevel.Warning,
        Message = "quota block stamp not written for a refused raw report ({ExceptionType})")]
    private static partial void LogBlockStampFailed(ILogger logger, string exceptionType);

    [LoggerMessage(
        EventId = 2469,
        Level = LogLevel.Warning,
        Message = "raw report parse of artifact {ArtifactId} was skipped after a quota deferral: {Deferral}, the admission answered {Reason}")]
    private static partial void LogBlockedThenClosed(
        ILogger logger,
        Guid artifactId,
        string deferral,
        string reason);
}
