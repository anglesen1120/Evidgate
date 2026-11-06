using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>
/// <see cref="IQuotaBlockStamp"/>: the gate's refusal stamp, written after its caller rolled the transaction that held
/// it back. The scope is the service's own — a refusal has already ended the caller's — and the statement is the
/// gate's, so the stamp names exactly the counters the refused call charged.
/// <para>
/// The stamp is a report of a refusal that already happened, not part of the decision: a scope that the block did not
/// leave without room, a counter that already carries the stamp and a scope with no counter row all write nothing, so a
/// repeated refusal costs one statement and no version bump.
/// </para>
/// </summary>
internal sealed class QuotaBlockStamp(ITenantScopeFactory scopes, QuotaCounterStore store) : IQuotaBlockStamp
{
    /// <inheritdoc />
    public async Task StampAsync(Guid orgId, QuotaCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A call that could not add a unit was never refused by a limit, and the statement's predicate would agree
        // with an empty quantity: skipping it keeps a caller bug (the gate throws on it) from writing a stamp.
        if (request.Quantity <= 0)
        {
            return;
        }

        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);

        await store
            .StampBlockedAsync(
                orgId,
                request.Period,
                UsageMetricWire.ToWireValue(request.Metric),
                QuotaScopeKeys.Of(request),
                request.Quantity,
                cancellationToken)
            .ConfigureAwait(false);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
