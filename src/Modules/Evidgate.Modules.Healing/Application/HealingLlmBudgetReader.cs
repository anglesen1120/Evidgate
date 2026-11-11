using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Healing.Application;

/// <summary>One project's daily budget row, as the refusal check reads it.</summary>
/// <param name="PeriodCapCost">The most this project may spend in the period.</param>
/// <param name="PeriodCapTokens">The most tokens the period allows.</param>
/// <param name="UsedCost">What the period has spent so far.</param>
/// <param name="UsedTokens">The tokens the period has spent so far.</param>
internal sealed record HealingLlmBudgetRow(
    decimal PeriodCapCost,
    int PeriodCapTokens,
    decimal UsedCost,
    int UsedTokens);

/// <summary>What the project's budget allows right now.</summary>
internal enum HealingLlmBudgetVerdict
{
    /// <summary>The row exists and both caps still leave room.</summary>
    Room = 0,

    /// <summary>The period spent its cost cap or its token cap.</summary>
    Exceeded = 1,

    /// <summary>The project has no daily row, so nothing measured its spend.</summary>
    NotProvisioned = 2,

    /// <summary>The read failed; the call must not be placed on an unmeasured budget.</summary>
    Unreadable = 3,
}

/// <summary>
/// The budget check a healing call passes before any provider is called: one statement on the caller's tenant
/// transaction, naming the trusted org and the project, so row level security and the unique
/// <c>(org_id, project_id, period, period_start)</c> key both scope it.
/// </summary>
/// <remarks>
/// The check is advisory: it reads, and the provider call follows, so it is not atomic with the spend it guards and two
/// concurrent calls can both pass on the last unit of a cap. Consuming the budget atomically replaces this read; until
/// then a refusal is a fail-closed early exit, never a reservation.
/// </remarks>
internal sealed class HealingLlmBudgetReader(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>Reads the project's daily row for today, in UTC.</summary>
    /// <param name="projectId">The project the call is costed against.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the call may spend, and the reason it may not.</returns>
    internal async Task<HealingLlmBudgetVerdict> ReadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        HealingLlmBudgetRow? row;

        try
        {
            row = await db.QuerySingleOrDefaultAsync<HealingLlmBudgetRow>(
                    HealingLlmBudgetSql.Today,
                    new { org_id = scopes.Require().OrgId, project_id = projectId },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Fail closed: a budget nobody could read is not a budget with room, and a call placed on it is unmeasured.
            return HealingLlmBudgetVerdict.Unreadable;
        }

        if (row is null)
        {
            return HealingLlmBudgetVerdict.NotProvisioned;
        }

        // A cap of zero admits nothing, which is what "used >= cap" already says.
        return row.UsedCost >= row.PeriodCapCost || row.UsedTokens >= row.PeriodCapTokens
            ? HealingLlmBudgetVerdict.Exceeded
            : HealingLlmBudgetVerdict.Room;
    }
}

/// <summary>The statements of <see cref="HealingLlmBudgetReader"/>.</summary>
internal static class HealingLlmBudgetSql
{
    /// <summary>
    /// Today's row of one project: the unique key's four columns, so the read is one index probe. The period start is
    /// the database's own UTC date, so two hosts cannot disagree about which day a call belongs to.
    /// </summary>
    internal const string Today = """
        select period_cap_cost as "PeriodCapCost",
               period_cap_tokens as "PeriodCapTokens",
               used_cost as "UsedCost",
               used_tokens as "UsedTokens"
          from public.healing_llm_budget
         where org_id = @org_id::uuid
           and project_id = @project_id::uuid
           and period = 'daily'
           and period_start = (now() at time zone 'utc')::date
        """;
}
