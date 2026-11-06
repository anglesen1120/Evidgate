namespace Evidgate.Modules.Healing.Contracts;

/// <summary>
/// The read another module asks of Healing for one org's token spend over a month: the attempts ledger is
/// Healing's table, so the aggregate that answers a cost question is served here
/// rather than read across a module boundary.
/// </summary>
/// <remarks>
/// The read joins the caller's already-open tenant scope, so row security narrows it to that tenant and a caller
/// seeing uncommitted rows of the same transaction sees the spend they just caused. The implementation also
/// predicates on the org explicitly, because a scope opened without a GUC is not the boundary itself.
/// </remarks>
public interface IHealingTokenUsage
{
    /// <summary>
    /// Reads the org's attempts inside the half-open window [<paramref name="monthStart"/>,
    /// <paramref name="monthEnd"/>) in UTC; an attempt recorded at the end instant is outside it.
    /// </summary>
    /// <param name="orgId">The tenant of the caller's open scope.</param>
    /// <param name="monthStart">The window's first instant, UTC.</param>
    /// <param name="monthEnd">The window's exclusive end instant, UTC.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The window's token sum and the count of attempts whose token use is known.</returns>
    Task<HealingTokenUsage> ReadAsync(
        Guid orgId,
        DateTimeOffset monthStart,
        DateTimeOffset monthEnd,
        CancellationToken cancellationToken);
}
