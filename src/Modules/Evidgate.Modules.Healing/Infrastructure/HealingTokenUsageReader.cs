using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Contracts;

namespace Evidgate.Modules.Healing.Infrastructure;

/// <summary>
/// The healing side of a tenant's AI-token cost: one aggregate over the module's own attempt ledger, run through
/// <see cref="ITenantDb"/> so it joins the caller's open tenant transaction and the row security that guards it.
/// </summary>
/// <remarks>
/// The attempt ledger records when an attempt happened, not which billing period it belonged to, so the window is
/// the UTC month's half-open range and the <c>(org_id, project_id, created_at)</c> index serves it. The sum covers
/// every attempt of the window; the count is deliberately narrower, because an attempt that failed and reported no
/// tokens does not turn the month into a measured zero.
/// </remarks>
internal sealed class HealingTokenUsageReader(ITenantDb db) : IHealingTokenUsage
{
    /// <inheritdoc />
    public async Task<HealingTokenUsage> ReadAsync(
        Guid orgId,
        DateTimeOffset monthStart,
        DateTimeOffset monthEnd,
        CancellationToken cancellationToken)
    {
        var usage = await db.QuerySingleOrDefaultAsync<HealingTokenUsage>(
                HealingTokenUsageSql.Month,
                new { org_id = orgId, month_start = monthStart, month_end = monthEnd },
                cancellationToken)
            .ConfigureAwait(false);

        // One aggregate row always comes back; the null arm is the shape a failed query would dig Dapper out of.
        return usage ?? new HealingTokenUsage(null, 0);
    }
}

/// <summary>The statements of <see cref="HealingTokenUsageReader"/> (migration 0054).</summary>
internal static class HealingTokenUsageSql
{
    /// <summary>
    /// The month's spend for one org: the token sum every attempt contributed, and the count of the attempts whose
    /// token use is known. <c>org_id</c> is named as well as the scope's row security, so no argument widens the
    /// tenant.
    /// </summary>
    internal const string Month = """
        select sum(tokens)::numeric as tokens,
               count(*) filter (where status = 'ok' or tokens > 0) as known_attempts
          from public.healing_llm_call
         where org_id = @org_id
           and created_at >= @month_start
           and created_at < @month_end
        """;
}
