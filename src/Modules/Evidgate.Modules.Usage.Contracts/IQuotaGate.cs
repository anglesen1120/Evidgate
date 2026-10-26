namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// The quota gate the metering hot paths check before they commit work (ruling T53: 04.1.2's event batch and
/// 06.5.1's artifact completion). It runs inside the caller's tenant transaction, so its one indexed read of
/// <c>quota_counter</c> sees the caller's uncommitted state and a <see cref="QuotaDecision.Block"/> is the caller's
/// cue to refuse the write, never a rollback of work already committed.
/// <para>
/// This interface is the contract 12.3.1 exports; its implementation lands with 12.3.1-T4 (quota counters and
/// enforcement), so no host registers it yet. <b>An unregistered gate is a wiring error, not "no limit":</b> a caller
/// that resolves it in a host that never registered the implementation fails at resolution (a container throws for the
/// missing service), and that failure is the documented, fail-closed behaviour. A host must never register a stub that
/// answers <see cref="QuotaDecision.Ok"/> to paper over the gap.
/// </para>
/// <para>
/// <see cref="QuotaDecision.Ok"/> therefore means exactly one thing: the gate ran and the org has no counter row for
/// the requested <c>(org, project, period, metric)</c> (or holds one below the warning line). It never means the gate
/// was skipped or never existed.
/// </para>
/// </summary>
public interface IQuotaGate
{
    /// <summary>
    /// Answers whether <paramref name="request"/> may proceed in the caller's tenant transaction: one indexed read of
    /// the <c>quota_counter</c> row of <c>(org, project, period, metric)</c>, never per event (ruling T53).
    /// </summary>
    /// <param name="request">The metric, period, project and quantity the call is about to commit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see cref="QuotaDecision.Ok"/>, <see cref="QuotaDecision.Warn"/> or <see cref="QuotaDecision.Block"/>.</returns>
    Task<QuotaDecision> CheckAsync(QuotaCheckRequest request, CancellationToken cancellationToken);
}
