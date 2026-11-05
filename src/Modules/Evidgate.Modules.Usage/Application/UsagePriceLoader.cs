using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Parsing;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application;

/// <summary>
/// Loads one rate card into the database: parse and validate the whole card first, then write every row of it
/// as one unit of work. It is deliberately not a Mediator command and has no permission surface — no route serves a card
/// yet, so the only callers are in-process and the operator route that will own one is a later task.
/// <para>
/// The scope decides the write path, and the org is never a card member: <see cref="UsagePriceScope.Org"/> writes for the
/// open tenant scope, so a card cannot name another tenant, while <see cref="UsagePriceScope.Global"/> writes rows that
/// belong to no tenant through the security-definer function.
/// </para>
/// </summary>
internal sealed class UsagePriceLoader(
    UsagePriceStore store,
    ITenantScopeAccessor scopes)
{
    /// <summary>Loads <paramref name="cardJson"/> under <paramref name="scope"/>.</summary>
    /// <param name="scope">The card the load writes: the caller's override or the global fallback.</param>
    /// <param name="cardJson">The raw card body; it is bounded and parsed before any statement runs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The inserted ids, or the refusals that wrote nothing.</returns>
    /// <exception cref="InvalidOperationException">
    /// No tenant transaction is open, for <see cref="UsagePriceScope.Org"/>: the scope guard throws before any statement
    /// runs.
    /// </exception>
    internal async Task<UsagePriceLoadResult> LoadAsync(
        UsagePriceScope scope,
        ReadOnlyMemory<byte> cardJson,
        CancellationToken cancellationToken)
    {
        var parsed = UsagePriceCardParser.Parse(cardJson);
        if (parsed.Card is not { } card)
        {
            return UsagePriceLoadResult.Refused(parsed.Violations);
        }

        return scope switch
        {
            UsagePriceScope.Org => await store
                .InsertOrgAsync(scopes.Require().OrgId, card, cancellationToken)
                .ConfigureAwait(false),
            UsagePriceScope.Global => await store
                .InsertGlobalAsync(card, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                "the usage price scope is not one the loader writes"),
        };
    }
}
