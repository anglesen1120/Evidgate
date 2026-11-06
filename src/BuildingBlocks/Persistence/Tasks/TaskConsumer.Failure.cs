namespace Evidgate.BuildingBlocks.Persistence.Tasks;

public sealed partial class TaskConsumer
{
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
}
