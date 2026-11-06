namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// Keeps a refusal visible after the caller that earned it rolled back. <see cref="IQuotaGate"/> stamps
/// <c>blocked_at</c> inside the caller's tenant transaction, so a caller that answers a block by rolling its work
/// back — the ingest route's refused batch — discards that stamp with everything else.
/// <para>
/// This is the same stamp, written in the scope of its own short transaction after the rollback. It is a report of a
/// refusal that already happened, so a caller keeps its 429 when the stamp cannot be written: nothing here admits
/// work, and the gate's own check stays the only thing that does.
/// </para>
/// </summary>
public interface IQuotaBlockStamp
{
    /// <summary>
    /// Stamps <c>blocked_at</c> on every counter the request's scopes own that still has no room, in a transaction of
    /// its own. A scope with room again (an operator raise committed), a counter that already carries the stamp, and a
    /// scope with no counter row are all left untouched.
    /// </summary>
    /// <param name="orgId">The tenant the refused call ran as.</param>
    /// <param name="request">The question the gate blocked: its scopes, period, metric and quantity.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task StampAsync(Guid orgId, QuotaCheckRequest request, CancellationToken cancellationToken);
}
