using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Healing.Application;

/// <summary>What a reservation attempt on the project's daily budget came to.</summary>
internal enum HealingLlmBudgetVerdict
{
    /// <summary>The estimate was reserved: both caps still leave room for it.</summary>
    Reserved = 0,

    /// <summary>The period has no room for the estimate (a cap is spent, or the estimate does not fit what is left).</summary>
    Exceeded = 1,

    /// <summary>The project has no daily row, so nothing measures its spend.</summary>
    NotProvisioned = 2,

    /// <summary>The statement failed; the call must not be placed on an unmeasured budget.</summary>
    Unreadable = 3,
}

/// <summary>
/// A reservation held against one daily budget row. It carries the row's own period start, so a settlement that runs
/// after midnight UTC still moves the row it reserved on.
/// </summary>
/// <param name="Tokens">The tokens held.</param>
/// <param name="Cost">The cost held.</param>
/// <param name="PeriodStart">The row's <c>period_start</c>, as ISO text.</param>
internal sealed record HealingLlmReservation(int Tokens, decimal Cost, string PeriodStart);

/// <summary>
/// The budget counters of <c>healing_llm_budget</c> as a two-phase ledger: reserve an estimate before a provider call,
/// settle it to the real usage (or release it) afterwards. Each method is one statement on whichever tenant transaction
/// the caller opened, and the caller commits it before the provider is called, so the hold is visible to every
/// concurrent call and the provider call never holds a database transaction open.
/// </summary>
/// <remarks>
/// The reservation is the single guarded <c>UPDATE ... RETURNING</c>: the row lock serialises concurrent holders and the
/// caps are re-checked against the value the lock returns, so no interleaving of N callers can push the counters past a
/// cap. A hard process kill between reserve and settle leaves the estimate counted until the period resets, which can
/// only over-count — the fail-closed direction.
/// </remarks>
internal sealed class HealingLlmBudgetLedger(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>Holds <paramref name="tokens"/> and <paramref name="cost"/> on today's row, in UTC, when both fit.</summary>
    /// <param name="projectId">The project the call is costed against.</param>
    /// <param name="tokens">The estimated tokens of the call.</param>
    /// <param name="cost">The estimated cost of the call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict, and the reservation when it is <see cref="HealingLlmBudgetVerdict.Reserved"/>.</returns>
    internal async Task<(HealingLlmBudgetVerdict Verdict, HealingLlmReservation? Reservation)> ReserveAsync(
        Guid projectId,
        int tokens,
        decimal cost,
        CancellationToken cancellationToken)
    {
        var key = new { org_id = scopes.Require().OrgId, project_id = projectId };

        try
        {
            var periodStart = await db.ScalarAsync<string?>(
                    HealingLlmBudgetSql.Reserve,
                    new { key.org_id, key.project_id, tokens, cost },
                    cancellationToken)
                .ConfigureAwait(false);

            if (periodStart is not null)
            {
                return (HealingLlmBudgetVerdict.Reserved, new HealingLlmReservation(tokens, cost, periodStart));
            }

            // No row moved: either the project has no row today or the row has no room. One more read tells them apart.
            var provisioned = await db.ScalarAsync<bool>(HealingLlmBudgetSql.RowExists, key, cancellationToken)
                .ConfigureAwait(false);

            return (provisioned ? HealingLlmBudgetVerdict.Exceeded : HealingLlmBudgetVerdict.NotProvisioned, null);
        }
        catch (DbException)
        {
            // Fail closed: a budget nobody could update is not a budget with room, and a call placed on it is unmeasured.
            return (HealingLlmBudgetVerdict.Unreadable, null);
        }
    }

    /// <summary>
    /// Replaces <paramref name="reservation"/> with what the call really spent. A release is a settlement of zero, and a
    /// spend above the estimate is recorded in full: the provider was already paid, so the counter must say so.
    /// </summary>
    /// <param name="projectId">The project the reservation was made for.</param>
    /// <param name="reservation">The hold taken before the call.</param>
    /// <param name="tokens">The tokens the call spent (zero to release).</param>
    /// <param name="cost">The cost the call incurred (zero to release).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task SettleAsync(
        Guid projectId,
        HealingLlmReservation reservation,
        int tokens,
        decimal cost,
        CancellationToken cancellationToken)
    {
        _ = await db.ExecuteAsync(
                HealingLlmBudgetSql.Settle,
                new
                {
                    org_id = scopes.Require().OrgId,
                    project_id = projectId,
                    period_start = reservation.PeriodStart,
                    reserved_tokens = reservation.Tokens,
                    reserved_cost = reservation.Cost,
                    actual_tokens = tokens,
                    actual_cost = cost,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>The statements of <see cref="HealingLlmBudgetLedger"/>.</summary>
internal static class HealingLlmBudgetSql
{
    /// <summary>
    /// The atomic hold. A cap already spent admits nothing (a cap of zero too), and an estimate that does not fit the
    /// remainder is refused as a whole. The period start is the database's own UTC date, so two hosts cannot disagree
    /// about which day a call belongs to.
    /// </summary>
    internal const string Reserve = """
        update public.healing_llm_budget
           set used_cost = used_cost + @cost::numeric,
               used_tokens = used_tokens + @tokens::integer
         where org_id = @org_id::uuid
           and project_id = @project_id::uuid
           and period = 'daily'
           and period_start = (now() at time zone 'utc')::date
           and used_cost < period_cap_cost
           and used_tokens < period_cap_tokens
           and used_cost + @cost::numeric <= period_cap_cost
           and used_tokens + @tokens::integer <= period_cap_tokens
        returning period_start::text
        """;

    /// <summary>Whether today's row exists, to tell a missing row from a spent one after a refused hold.</summary>
    internal const string RowExists = """
        select exists (
          select 1
            from public.healing_llm_budget
           where org_id = @org_id::uuid
             and project_id = @project_id::uuid
             and period = 'daily'
             and period_start = (now() at time zone 'utc')::date)
        """;

    /// <summary>Swaps the hold for the real usage on the row the hold was taken on; counters never go below zero.</summary>
    internal const string Settle = """
        update public.healing_llm_budget
           set used_cost = greatest(used_cost - @reserved_cost::numeric + @actual_cost::numeric, 0),
               used_tokens = greatest(used_tokens - @reserved_tokens::integer + @actual_tokens::integer, 0)
         where org_id = @org_id::uuid
           and project_id = @project_id::uuid
           and period = 'daily'
           and period_start = @period_start::date
        """;
}
