using System.Data.Common;
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
/// lease on a read and a parse that would roll back. That answer never admits work: a block writes the stamp when the
/// peek reports the counter unstamped — a raise may have cleared it and still left too little room — a transient
/// database fault of the probe defers the attempt and stamps nothing, a permanent fault fails the attempt like any
/// other, and a run the admission already refuses is not asked at all, because its own refusal is the loss to report.
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
    /// The reason code word a pre-check that could not read its facts carries out: nothing refused, so the attempt
    /// waits again with the queue's floor instead of spending a lease on a database fault.
    /// </summary>
    private const string PreCheckUnavailableReason = "quota_precheck_unavailable";

    /// <summary>The telemetry kind of a pre-check that could not read its facts, kept apart from a refusal's rate.</summary>
    private const string ReportQuotaPreCheckFault = "report_quota_precheck_fault";

    /// <summary>
    /// How long a pre-check that could not read its facts waits: long enough for a restart or a failover to finish
    /// before the retry, and identical for every report of every tenant, so one outage cannot become a retry storm that
    /// keeps the recovering database busy.
    /// </summary>
    internal static readonly TimeSpan PreCheckFaultDelay = TimeSpan.FromSeconds(15);

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

        // The month is left open: the gate reads it from this transaction's own clock inside its first statement, the
        // clock that stamps the batch's rows and the ledger row metering them, so neither is ever a month behind.
        var request = new QuotaCheckRequest(
            UsageMetric.Results,
            Period: null,
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
            QuotaRequest = StampRequest(request, answer.Period),
        };
    }

    /// <summary>
    /// The question the post-rollback stamp writes, named with the month the gate decided in. The stamp writes a
    /// counter row in a transaction of its own, so it cannot resolve the month itself and a block that names none is a
    /// gate bug, not a refusal to guess around.
    /// </summary>
    /// <param name="request">The question the gate blocked, with its month still open.</param>
    /// <param name="period">The month the gate's answer named.</param>
    /// <returns>The same question, fixed to that month.</returns>
    /// <exception cref="InvalidOperationException">The gate blocked without naming the month it decided in.</exception>
    private static QuotaCheckRequest StampRequest(QuotaCheckRequest request, string? period) =>
        request with
        {
            Period = period
                ?? throw new InvalidOperationException("the quota gate blocked without naming the billing month"),
        };

    /// <summary>
    /// The advisory stop before the bytes are read, for a report the queue has already deferred once: a counter with
    /// no room for even one event cannot admit the batch whatever the document holds, and reading and parsing it only
    /// to roll it back spends the lease on a wait that is already known.
    /// <para>
    /// It decides nothing. <see cref="IQuotaGate.PeekAsync"/> writes no counter, takes no lock and admits nobody, and
    /// an unblocked answer only means the transactional gate still decides after the insert. A block writes the stamp
    /// the transaction's block does only when the peek reports the counter still unstamped: a raise that left too
    /// little room cleared the stamp and must see it again, while a counter that already carries it needs no write at
    /// all. A first attempt is never probed, so evidence that fits is never refused at the door.
    /// </para>
    /// <para>
    /// Only the probe's own reads are guarded: a transient database fault there defers the attempt and stamps nothing,
    /// because a read that failed decided nothing. A permanent fault — a permission denied, a schema that no longer
    /// matches — is a failed attempt, never a deferral that would retry a broken deployment until it dead-letters. The
    /// deferred-report check and the run facts are one unguarded read: it is two indexed lookups in one statement, and a
    /// fault there is the attempt's own failure to report.
    /// </para>
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind; the artifact is the task row's probe key.</param>
    /// <param name="cancellationToken">Cancels the deferred check, the probe and the advisory question.</param>
    /// <returns>The blocked outcome to defer, or null when the report continues to the fetch.</returns>
    private async Task<ParseRawReportOutcome?> PreCheckQuotaAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        var facts = await ReadDeferredRunAsync(orgId, payload, cancellationToken).ConfigureAwait(false);
        if (!facts.Deferred)
        {
            return null;
        }

        try
        {
            return await ProbeQuotaAsync(payload, facts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProbeFault(exception, cancellationToken))
        {
            // The probe never answered, so the attempt goes back to the queue rather than spending a lease on a fault;
            // no stamp is written because nothing refused. Only the type and the SQLSTATE are logged: a statement's text
            // can quote counter values and ids, while a SQLSTATE is a code.
            EvidgateTelemetry.RecordIngestQuotaDenied(ReportQuotaPreCheckFault);
            LogQuotaPreCheckFault(logger, exception.GetType().Name, SqlState(exception));
            return new ParseRawReportOutcome(ParseRawReportOutcome.QuotaBlocked, PreCheckUnavailableReason)
            {
                RetryAfter = PreCheckFaultDelay,
            };
        }
    }

    /// <summary>
    /// The probe itself: whether the run still admits evidence and whether a counter of the month has room for one
    /// event. The run facts are read once and answer both questions, so the counter this asks about is the one the
    /// write transaction will charge.
    /// </summary>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="facts">What the one deferred-and-run read answered.</param>
    /// <param name="cancellationToken">Cancels the advisory question.</param>
    /// <returns>The blocked outcome to defer, or null when the report continues to the fetch.</returns>
    private async Task<ParseRawReportOutcome?> ProbeQuotaAsync(
        ParseRawReportPayload payload,
        DeferredRunFacts facts,
        CancellationToken cancellationToken)
    {
        if (facts.Run is not { } admitted)
        {
            // The run could not be read, so neither an admission nor a counter owner exists yet: the write transaction
            // names the absence the way it always did instead of this read guessing at a project.
            return null;
        }

        if (!RunAdmissionPolicy.Decide(admitted.ProjectState, admitted.RunState, admitted.LateWindowOpen).Allowed)
        {
            // A closed run refuses this report whatever the counter says, so waiting for room would bury the terminal
            // refusal and the loss it names: the attempt goes on and the write transaction answers it as before.
            return null;
        }

        await using var gateScope = scopeFactory.CreateAsyncScope();
        var gate = gateScope.ServiceProvider.GetRequiredService<IQuotaGate>();
        var request = new QuotaCheckRequest(
            UsageMetric.Results,
            Period: null,
            admitted.ProjectId,
            // One event is the smallest batch. A deferred report's real count is unknown before it is parsed, and a
            // counter with no room for one event has no room for the report either.
            1,
            IncludeTenantScope: true);
        var answer = await gate.PeekAsync(request, cancellationToken).ConfigureAwait(false);

        if (!answer.Blocked)
        {
            // Also a period without a counter yet: only the authoritative check may refuse or admit.
            return null;
        }

        EvidgateTelemetry.RecordIngestQuotaDenied(ReportQuotaPreCheck);
        LogQuotaPreChecked(logger, payload.ArtifactId, RetrySeconds(answer.RetryAfter));

        return new ParseRawReportOutcome(ParseRawReportOutcome.QuotaBlocked, QuotaExceededReason)
        {
            RetryAfter = answer.RetryAfter,
            // A counter that already carries the stamp needs no write: the queue's next wait changes nothing an operator
            // can see, so the stamp's own transaction is not opened at all.
            QuotaRequest = answer.Unstamped ? StampRequest(request, answer.Period) : null,
        };
    }

    /// <summary>
    /// True only for a fault the retry can plausibly clear: a timeout, or a driver fault the provider itself classifies
    /// as transient. Everything else — a denied privilege, a missing table, a shape mismatch — is a permanent condition
    /// of this deployment, and deferring it would re-run the same failure on the queue's floor until the task
    /// dead-letters without ever naming the fault. A cancelled attempt is never deferred either: the caller's shutdown
    /// or lost lease is what stopped it, and masking that as a quota wait would hide the real reason.
    /// </summary>
    /// <param name="exception">What the probe threw.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>True when the attempt should defer instead of failing.</returns>
    private static bool IsProbeFault(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && (exception is TimeoutException || (exception is DbException fault && fault.IsTransient));

    /// <summary>
    /// The SQLSTATE a database fault carries, for the fault line: a five-character code, never a statement's text or a
    /// value the statement read.
    /// </summary>
    /// <param name="exception">What the probe threw.</param>
    /// <returns>The SQLSTATE, or <c>none</c> for a fault that is not a database fault.</returns>
    private static string SqlState(Exception exception) =>
        exception is DbException { SqlState: { } state } ? state : "none";

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

    [LoggerMessage(
        EventId = 2474,
        Level = LogLevel.Warning,
        Message = "raw report parse pre-check could not read the quota facts ({ExceptionType}, sqlstate {SqlState}): the task defers instead of spending an attempt")]
    private static partial void LogQuotaPreCheckFault(ILogger logger, string exceptionType, string sqlState);
}
