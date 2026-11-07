using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>
/// <see cref="IQuotaBlockStamp"/>: the gate's refusal stamp, written after its caller rolled the transaction that held
/// it back. The scope is the service's own — a refusal has already ended the caller's — and the statements are the
/// gate's, so the stamp names exactly the counters the refused call charged.
/// <para>
/// A refusal at the first call of a period is carried by the same rollback, so the stamp creates the call's missing
/// counters first (from the previous period, in lock order, before any is locked) and stamps the ones without room in
/// the same transaction: without the carry the stamp would match no row and no operator would ever see the block.
/// </para>
/// <para>
/// The stamp is a report of a refusal that already happened, not part of the decision: a scope that the block did not
/// leave without room, a counter that already carries the stamp and a scope with no counter row all write nothing. The
/// carry-forward always runs first — a stamp at the first call of a period must create the counters it reports — and it
/// is the only other statement; the stamp itself writes no row and takes no lock when the counter already carries it.
/// </para>
/// </summary>
internal sealed class QuotaBlockStamp(ITenantScopeFactory scopes, QuotaCounterStore store) : IQuotaBlockStamp
{
    /// <inheritdoc />
    public async Task StampAsync(Guid orgId, QuotaCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A question the gate would have refused before writing cannot have produced a block, so a stamp for it
        // would report nothing: the same bound the gate enforces keeps a caller bug from writing a counter here. The
        // month must be spelled out, because the stamp names the month that refused, not the one it runs in.
        if (request.Period is not { } period || !QuotaCheckRequestRules.IsValid(request))
        {
            return;
        }

        var scopeKeys = QuotaScopeKeys.Of(request);
        var metric = UsageMetricWire.ToWireValue(request.Metric);

        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);

        await store
            .CarryForwardAsync(orgId, period, metric, scopeKeys, cancellationToken)
            .ConfigureAwait(false);

        await store
            .StampBlockedAsync(orgId, period, metric, scopeKeys, request.Quantity, cancellationToken)
            .ConfigureAwait(false);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
