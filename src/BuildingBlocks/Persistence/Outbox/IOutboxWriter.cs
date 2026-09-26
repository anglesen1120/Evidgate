namespace Evidgate.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// The write side of the transactional outbox (01.5.1-T2.2, 00 §5.3 / §5.5 C7):
/// one row, written on the caller's tenant connection and transaction, so the work
/// to be done later commits together with the business change that asked for it —
/// or not at all. This is the only way a command hands work to the background
/// (there is no in-process bus that could lose it on a crash, A10); the worker's
/// dispatcher (01.5.1-T3) moves committed rows into the <c>task</c> queue.
/// </summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Appends one outbox row inside the caller's tenant transaction. The row is
    /// invisible to the dispatcher until that transaction commits, and disappears
    /// with it if the scope is disposed without <c>CommitAsync</c>.
    /// </summary>
    /// <param name="taskType">
    /// The handler key the dispatcher will queue, e.g. <c>identity.organization.provision</c>.
    /// </param>
    /// <param name="payloadJson">
    /// The task payload as a JSON object. Payloads reference aggregates by id and
    /// never carry secrets (00 §5.7 S3); the database rejects anything that is not
    /// valid JSON.
    /// </param>
    /// <param name="aggregateType">The aggregate the work belongs to, e.g. <c>organization</c>.</param>
    /// <param name="aggregateId">The id of that aggregate.</param>
    /// <param name="idempotencyKey">
    /// The dedupe key, unique per organization (<c>outbox_org_idem_uq</c>). The
    /// convention is <c>&lt;module&gt;:&lt;aggregate&gt;:&lt;op&gt;</c>, e.g.
    /// <c>identity:organization:created</c>, so a producer that flushes the same
    /// command twice gets a unique-violation instead of a second piece of work.
    /// </param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The generated UUIDv7 id of the row (00 §7).</returns>
    /// <exception cref="Evidgate.BuildingBlocks.Persistence.Tenancy.TenantScopeMissingException">
    /// No tenant scope is open on the current async flow: an outbox row without a
    /// tenant transaction would be exactly the second write path this contract
    /// forbids.
    /// </exception>
    Task<Guid> EnqueueAsync(
        string taskType,
        string payloadJson,
        string aggregateType,
        Guid aggregateId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
