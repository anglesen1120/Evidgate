using System.Data.Common;

namespace Evidgate.Modules.Healing.Application;

internal sealed partial class HealingLlmGateway
{
    /// <summary>
    /// Gives the hold back and throws when the caller has cancelled. Only valid while nothing has left the process, so
    /// nothing can have been billed: after the first byte is sent the estimate stays charged instead.
    /// </summary>
    /// <param name="orgId">The tenant the call belongs to.</param>
    /// <param name="projectId">The project the hold was taken on.</param>
    /// <param name="reservation">The hold, or null when the adapter runs without a budget row.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    private async Task ReleaseIfCancelledAsync(
        Guid orgId,
        Guid projectId,
        HealingLlmReservation? reservation,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await ReleaseAsync(orgId, projectId, reservation).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Gives the hold back in a fresh scope, whatever the caller's token says. A release that itself fails is logged and
    /// not thrown, so it cannot mask the failure that made the release necessary; the leak is the bounded over-count
    /// described on the class.
    /// </summary>
    private async Task ReleaseAsync(Guid orgId, Guid projectId, HealingLlmReservation? reservation)
    {
        if (reservation is null)
        {
            return;
        }

        try
        {
            await transactions
                .RunAsync(
                    orgId,
                    projectId,
                    async ct =>
                    {
                        await ledger.SettleAsync(projectId, reservation, 0, 0m, ct).ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            LogReleaseFailed(exception, projectId);
        }
    }
}
