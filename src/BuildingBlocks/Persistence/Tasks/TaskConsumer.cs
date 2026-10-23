using System.Diagnostics;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The consumer side of the queue (01.5.1 §7): claim a batch, run one handler per
/// row outside the claim transaction, move the row on. The claim, the handler and
/// the completion are three separate transactions on purpose (§8 P5): no row lock
/// is held while a handler works, and the tenant transaction spans exactly the
/// handler's business work.
/// </summary>
/// <remarks>
/// Order of the two durable steps is the mechanism's at-least-once story: the
/// handler's tenant scope commits first, then the row is marked `done`. A crash
/// between them redelivers the task instead of losing it, which is why a handler
/// must be idempotent on the work's natural key (A10). The reverse order would
/// lose the work.
/// </remarks>
public sealed partial class TaskConsumer
{
    private readonly ITaskClaimer _claimer;
    private readonly ITaskLeaseWriter _leases;
    private readonly ITaskHandlerRegistry _handlers;
    /// <summary>The <c>last_error</c> of a tenant-handler row that carries no <c>org_id</c> (02.6.2 AC3).</summary>
    public const string MissingTenantReason = "missing_tenant: the task row has no org_id but its handler is tenant-bound";

    private readonly ITenantScopeFactory _scopes;
    private readonly ITaskAlertSink _alerts;
    private readonly TasksOptions _options;
    private readonly TimeSpan _leaseLength;
    private readonly ILogger<TaskConsumer> _logger;

    /// <summary>
    /// Budget for the failure transition. It runs with a token the shutdown cannot
    /// cancel (the row must end in a state the next worker can pick up), so it needs a
    /// bound of its own: the host's <c>ShutdownTimeout</c> is 10 s (01.1.2-T1.4) and a
    /// transition against an unreachable database would otherwise spend the whole
    /// budget waiting on a connect.
    /// </summary>
    private static readonly TimeSpan TransitionWriteBudget = TimeSpan.FromSeconds(2);

    /// <summary>Creates the consumer over the registered claim/lease/handler/scope services.</summary>
    public TaskConsumer(
        ITaskClaimer claimer,
        ITaskLeaseWriter leases,
        ITaskHandlerRegistry handlers,
        ITenantScopeFactory scopes,
        ITaskAlertSink alerts,
        IOptions<TasksOptions> options,
        ILogger<TaskConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(claimer);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _claimer = claimer;
        _leases = leases;
        _handlers = handlers;
        _scopes = scopes;
        _alerts = alerts;
        _options = options.Value;

        _leaseLength = TimeSpan.FromSeconds(_options.VisibilityTimeoutSeconds);
        _logger = logger;
    }

    /// <summary>
    /// One claim-and-run pass for <paramref name="queue"/>: takes at most
    /// <see cref="TasksOptions.BatchSize"/> of the queue's oldest due rows it has a
    /// handler for, runs them in order, and returns how many it ran. Rows of a queue
    /// this process has no handler for stay `queued` (§11).
    /// </summary>
    public async Task<int> PollOnceAsync(string queue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);

        // The lease is stamped by the database (`locked_until = now() + timeout`) and
        // the reclaim answers the same question against the database clock, so "did
        // this claim run out while it waited in the batch" must not be answered by
        // comparing that deadline with this host's clock: a host whose clock is stepped
        // or skewed would either skip every fresh claim or miss a real expiry. A
        // monotonic reading from just before the claim is immune to both.
        var claimStarted = Stopwatch.GetTimestamp();

        var batch = await _claimer
            .ClaimAsync(queue, _handlers.TaskTypes, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        var processed = 0;
        foreach (var claim in batch)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Shutdown: stop starting new work. The rows that never started keep
                // their lease instead of being failed, because a cancelled attempt
                // would burn a retry and record an error that says nothing about the
                // handler — their lease is what redelivers them (T1.2's "finish
                // in-flight, stop claiming", §7's consumer contract).
                LogShutdownBeforeStarting(claim.Id, batch.Count - processed);
                break;
            }

            if (Stopwatch.GetElapsedTime(claimStarted) >= _leaseLength)
            {
                // The lease of everything still waiting ran out (one claim statement
                // stamps one deadline for the whole batch, so the first row past it means
                // all of them are). The handlers never ran, so §6's cap must not count
                // these as attempts: release them and let the next pump — or another
                // Worker — take them with their retries intact.
                await ReleaseAsync(batch, processed, cancellationToken).ConfigureAwait(false);
                break;
            }

            if (await ExecuteAsync(claim, claimStarted, cancellationToken).ConfigureAwait(false))
            {
                processed++;
            }
        }

        return processed;
    }

    /// <summary>Gives back the attempts of the claims of <paramref name="batch"/> that were never started.</summary>
    private async Task ReleaseAsync(
        IReadOnlyList<TaskClaim> batch,
        int started,
        CancellationToken cancellationToken)
    {
        var released = 0;

        for (var index = started; index < batch.Count; index++)
        {
            try
            {
                if (await _leases.ReleaseAsync(batch[index], cancellationToken).ConfigureAwait(false))
                {
                    released++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The lease is the backstop: an unreleased row is reclaimed like any
                // other expired lease, it just pays for one attempt it did not use.
                LogReleaseFailed(batch[index].Id, exception);
            }
        }

        LogReleased(batch.Count - started, released);
    }

    /// <summary>
    /// Runs <paramref name="concurrency"/> independent claim loops for
    /// <paramref name="queue"/> until <paramref name="stoppingToken"/> is cancelled.
    /// The returned task is also the graceful-shutdown contract of 01.5.1-T1.2: it
    /// completes only when every in-flight handler has finished. Loops stop
    /// claiming as soon as the token is cancelled, so a shutdown does not start new
    /// work.
    /// </summary>
    public Task RunAsync(string queue, int concurrency, CancellationToken stoppingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        var loops = Enumerable
            .Range(0, concurrency)
            .Select(_ => RunLoopAsync(queue, stoppingToken))
            .ToArray();

        return Task.WhenAll(loops);
    }

    /// <summary>
    /// One loop: poll, wait, poll again. A failed pass is not fatal — the database
    /// can be unreachable, or a migration can be missing, and both are conditions
    /// the loop must survive; the first failure of a run of failures is logged at
    /// warning and the rest at debug, so an outage cannot fill the log.
    /// </summary>
    private async Task RunLoopAsync(string queue, CancellationToken stoppingToken)
    {
        var reportedFailure = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(queue, stoppingToken).ConfigureAwait(false);
                reportedFailure = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (reportedFailure)
                {
                    LogPollFailedAgain(queue, exception);
                }
                else
                {
                    LogPollFailed(queue, exception);
                    reportedFailure = true;
                }
            }

            try
            {
                await Task.Delay(_options.PollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One row, one attempt: handler inside the tenant scope opened from the row's
    /// <c>org_id</c>, then either the completion or the failure transition. Returns
    /// whether this consumer ran the row; false means the row was skipped because its
    /// lease was already gone.
    /// </summary>
    private async Task<bool> ExecuteAsync(
        TaskClaim claim,
        long claimStarted,
        CancellationToken cancellationToken)
    {
        // A claim's lease starts when the claim statement runs, not when this row's
        // turn comes: with a batch, a slow handler ahead of it can outlive the lease
        // and another consumer legitimately owns the row by then. Running it here
        // would execute the work twice (the completion's lease check would refuse the
        // second `done`, but the handler would already have run), so the row is left
        // to the claimer's reclaim path. AC7's "exactly one consumer" holds for the
        // whole batch this way, however slow the batch is.
        // measured against the monotonic stamp taken before the claim, never
        // against this host's clock: a worker whose clock leads the database by
        // at least the visibility timeout would read every fresh claim as expired
        // and drain no queue (the deadline itself is `now() + timeout` on the DB).
        if (Stopwatch.GetElapsedTime(claimStarted) >= _leaseLength)
        {
            LogLeaseExpiredBeforeRun(claim.Id, claim.TaskType);
            return false;
        }

        Exception? failure = null;

        try
        {
            if (!_handlers.TryGet(claim.TaskType, out var invoker))
            {
                // Unreachable by construction (the claim filtered on exactly this
                // handler set) and therefore an error rather than a release path:
                // it means the registry changed under a claimed batch.
                throw new InvalidOperationException(
                    $"no handler is registered for the claimed task_type '{claim.TaskType}' (task {claim.Id})");
            }

            if (claim.OrgId is null && invoker.RequiresTenant)
            {
                // 02.6.2 AC3: a tenant handler never runs unscoped. The row cannot
                // become valid by retrying, so it goes straight to the dead letter.
                await DeadLetterMissingTenantAsync(claim).ConfigureAwait(false);

                return true;
            }

            if (claim.OrgId is { } orgId)
            {
                await using var scope = await _scopes
                    .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
                    .ConfigureAwait(false);

                await invoker.InvokeAsync(claim.Payload, scope, cancellationToken).ConfigureAwait(false);

                // The handler's work is durable before the mechanism says `done`.
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A platform task: no tenant, so no scope — the mechanism's own
                // sweeps run across orgs by design (01.5.1 §3).
                await invoker.InvokeAsync(claim.Payload, null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null)
        {
            await MarkDoneAsync(claim).ConfigureAwait(false);
            return true;
        }

        await MarkFailedAsync(claim, failure).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// `active → dead_letter` with reason <c>missing_tenant</c>, skipping the retry
    /// budget (02.6.2 AC3). The alert hook fires like any other dead letter.
    /// </summary>
    private async Task DeadLetterMissingTenantAsync(TaskClaim claim)
    {
        using var budget = new CancellationTokenSource(TransitionWriteBudget);

        try
        {
            if (await _leases.MarkDeadLetterAsync(claim, MissingTenantReason, budget.Token).ConfigureAwait(false))
            {
                LogDeadLetter(claim.Id, claim.TaskType, claim.Attempts, MissingTenantReason);

                await TaskAlertNotifier
                    .NotifySafelyAsync(
                        _alerts,
                        _logger,
                        new TaskDeadLetterAlert(
                            TaskId: claim.Id,
                            Queue: claim.Queue,
                            TaskType: claim.TaskType,
                            OrgId: claim.OrgId,
                            Attempts: claim.Attempts,
                            MaxAttempts: claim.MaxAttempts,
                            LastError: MissingTenantReason,
                            Source: TaskDeadLetterSource.Consumer),
                        budget.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                LogLeaseLost(claim.Id, claim.TaskType);
            }
        }
        catch (Exception transitionFailure)
        {
            LogTransitionFailed(claim.Id, transitionFailure);
        }
    }

    private async Task MarkDoneAsync(TaskClaim claim)
    {
        // The same bounded, shutdown-independent budget as the failure transition:
        // an unbounded completion can wait out the Npgsql defaults (pool Timeout
        // 15 s + command Timeout 30 s) and overrun the host's 10 s ShutdownTimeout.
        using var budget = new CancellationTokenSource(TransitionWriteBudget);

        try
        {
            var completed = await _leases.MarkDoneAsync(claim, budget.Token).ConfigureAwait(false);
            if (completed)
            {
                LogDone(claim.Id, claim.TaskType);
            }
            else
            {
                // The lease had already run out and another consumer owns the row:
                // its attempt is the one that counts. Redelivery is the design.
                LogLeaseLost(claim.Id, claim.TaskType);
            }
        }
        catch (Exception exception)
        {
            // The handler's work is committed; only the bookkeeping failed. The row
            // stays `active` and the next claim pass reclaims it after the lease —
            // at-least-once, and the handler's idempotency key absorbs the replay.
            LogCompletionFailed(claim.Id, exception);
        }
    }

    /// <summary>
    /// The failure transition of §6: `retry` with the backoff while attempts are
    /// left, `dead_letter` at the cap. It runs with
    /// <see cref="CancellationToken.None"/> on purpose — a shutdown that cancelled
    /// the handler must still leave the row in a state the next worker can pick up
    /// instead of leaving it `active` for a full lease.
    /// </summary>
    private async Task MarkFailedAsync(TaskClaim claim, Exception failure)
    {
        var error = TaskErrorText.Redact(failure);

        // A bounded, shutdown-independent budget: the transition must land inside the
        // host's 10 s shutdown window even when the database is unreachable.
        using var budget = new CancellationTokenSource(TransitionWriteBudget);

        try
        {
            if (claim.Attempts >= claim.MaxAttempts)
            {
                var deadLettered = await _leases
                    .MarkDeadLetterAsync(claim, error, budget.Token)
                    .ConfigureAwait(false);

                if (deadLettered)
                {
                    LogDeadLetter(claim.Id, claim.TaskType, claim.Attempts, error);

                    // The alert hook of 01.5.1 §7 fires only on a transition that
                    // committed — the row's last_error is already redacted (00
                    // §5.7 S3), so the sink receives the stored value, not the
                    // exception. The bounded budget is deliberate: a slow sink
                    // cannot outlive the host's shutdown window.
                    await TaskAlertNotifier
                        .NotifySafelyAsync(
                            _alerts,
                            _logger,
                            new TaskDeadLetterAlert(
                                TaskId: claim.Id,
                                Queue: claim.Queue,
                                TaskType: claim.TaskType,
                                OrgId: claim.OrgId,
                                Attempts: claim.Attempts,
                                MaxAttempts: claim.MaxAttempts,
                                LastError: error,
                                Source: TaskDeadLetterSource.Consumer),
                            budget.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    LogLeaseLost(claim.Id, claim.TaskType);
                }

                return;
            }

            var backoff = TaskBackoff.Compute(
                claim.Attempts,
                TimeSpan.FromSeconds(_options.BackoffBaseSeconds),
                TimeSpan.FromSeconds(_options.BackoffMaxSeconds));

            var retried = await _leases
                .MarkRetryAsync(claim, backoff, error, budget.Token)
                .ConfigureAwait(false);

            if (retried)
            {
                LogRetry(claim.Id, claim.TaskType, claim.Attempts, backoff.TotalSeconds, error);
            }
            else
            {
                LogLeaseLost(claim.Id, claim.TaskType);
            }
        }
        catch (Exception transitionFailure)
        {
            // The lease is the backstop: a row whose transition could not be written
            // stays `active` until `locked_until` passes and the next claim pass
            // reclaims it.
            LogTransitionFailed(claim.Id, transitionFailure);
        }
    }

    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Warning,
        Message = "Claim pass failed for queue {Queue}; retrying after the poll interval")]
    private partial void LogPollFailed(string queue, Exception exception);

    [LoggerMessage(
        EventId = 2312,
        Level = LogLevel.Information,
        Message = "Released {Released} of {Waiting} claim(s) whose lease expired while they waited in the batch; their attempts are given back")]
    private partial void LogReleased(int waiting, int released);

    [LoggerMessage(
        EventId = 2313,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} could not be released; the reclaim path will redeliver it after its lease")]
    private partial void LogReleaseFailed(Guid taskId, Exception exception);

    [LoggerMessage(
        EventId = 2309,
        Level = LogLevel.Information,
        Message = "Shutdown: {Remaining} claimed task row(s) left for the next worker (first is {TaskId}); their leases redeliver them")]
    private partial void LogShutdownBeforeStarting(Guid taskId, int remaining);

    [LoggerMessage(
        EventId = 2308,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} ({TaskType}) skipped: its lease expired while it waited in the batch, so the claimer's reclaim path owns it now")]
    private partial void LogLeaseExpiredBeforeRun(Guid taskId, string taskType);

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Debug,
        Message = "Claim pass still failing for queue {Queue}")]
    private partial void LogPollFailedAgain(string queue, Exception exception);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Information,
        Message = "Task {TaskId} ({TaskType}) done")]
    private partial void LogDone(Guid taskId, string taskType);

    [LoggerMessage(
        EventId = 2303,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} ({TaskType}) finished after its lease was lost; the newer attempt owns the row")]
    private partial void LogLeaseLost(Guid taskId, string taskType);

    [LoggerMessage(
        EventId = 2304,
        Level = LogLevel.Error,
        Message = "Task {TaskId} ran but its completion could not be written; the lease will redeliver it")]
    private partial void LogCompletionFailed(Guid taskId, Exception exception);

    [LoggerMessage(
        EventId = 2305,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} ({TaskType}) attempt {Attempt} failed, retrying in {BackoffSeconds}s: {Error}")]
    private partial void LogRetry(Guid taskId, string taskType, int attempt, double backoffSeconds, string error);

    [LoggerMessage(
        EventId = 2306,
        Level = LogLevel.Error,
        Message = "Task {TaskId} ({TaskType}) dead-lettered after {Attempts} attempts: {Error}")]
    private partial void LogDeadLetter(Guid taskId, string taskType, int attempts, string error);

    [LoggerMessage(
        EventId = 2307,
        Level = LogLevel.Error,
        Message = "Task {TaskId}: the failure transition could not be written; the lease will redeliver it")]
    private partial void LogTransitionFailed(Guid taskId, Exception exception);
}
