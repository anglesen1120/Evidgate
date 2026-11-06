using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tasks;
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
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>The telemetry kind of a report batch refused for quota, beside the ingest route's <c>quota_exceeded</c>.</summary>
    private const string ReportQuotaExceeded = "report_quota_exceeded";

    /// <summary>The reason code word a quota refusal carries out of the write transaction.</summary>
    private const string QuotaExceededReason = "quota_exceeded";

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
        // The billing month of the admitted work, in the UTC form the counter and the ledger both store.
        var period = time.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var request = new QuotaCheckRequest(
            UsageMetric.Results, period, projectId, quantity, IncludeTenantScope: true);

        // The gate is scoped while this handler is a singleton, so it comes from the attempt's own scope and joins the
        // ambient tenant transaction: a block can then roll the batch back, and an exception refuses it unmetered.
        await using var gateScope = scopeFactory.CreateAsyncScope();
        var answer = await gateScope.ServiceProvider
            .GetRequiredService<IQuotaGate>()
            .CheckAsync(request, cancellationToken)
            .ConfigureAwait(false);

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
            // The refusal needs no stamp to stand, and losing it must never turn a deferral into a failure.
            LogBlockStampFailed(logger, exception);
        }

        return outcome;
    }

    /// <summary>
    /// The delay a blocked report defers for: the gate's wait, never under one second, and never past the start of the
    /// next UTC billing month, because only a rollover or an operator raise can admit the batch.
    /// </summary>
    /// <param name="retryAfter">The gate's answer, null or non-positive when it blocked without naming a wait.</param>
    /// <param name="now">The attempt's clock.</param>
    /// <returns>The clamped delay.</returns>
    /// <exception cref="InvalidOperationException">The gate blocked without a usable wait.</exception>
    internal static TimeSpan QuotaRetryDelay(TimeSpan? retryAfter, DateTimeOffset now)
    {
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
        var ceiling = nextPeriod > floor ? nextPeriod : floor;
        var delay = wait < floor ? floor : wait;

        return delay > ceiling ? ceiling : delay;
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
        EventId = 2468,
        Level = LogLevel.Warning,
        Message = "quota block stamp not written for a refused raw report")]
    private static partial void LogBlockStampFailed(ILogger logger, Exception exception);
}
