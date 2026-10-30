using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

public sealed partial class TaskConsumer
{
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
    /// Host shutdown cancelled the handler: hand the row back (`queued`, the attempt
    /// given back) instead of failing it, so a deploy neither burns a retry nor
    /// dead-letters healthy work. Bounded and shutdown-independent like the other
    /// transitions; if it cannot be written the lease redelivers the row.
    /// </summary>
    private async Task ReleaseOnShutdownAsync(TaskClaim claim)
    {
        using var budget = new CancellationTokenSource(TransitionWriteBudget);

        try
        {
            if (await _leases.ReleaseAsync(claim, budget.Token).ConfigureAwait(false))
            {
                LogReleasedOnShutdown(claim.Id, claim.TaskType);
            }
            else
            {
                LogLeaseLost(claim.Id, claim.TaskType);
            }
        }
        catch (Exception exception)
        {
            LogReleaseFailed(claim.Id, exception);
        }
    }
}
