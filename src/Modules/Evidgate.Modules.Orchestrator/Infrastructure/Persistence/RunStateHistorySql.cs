namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The run state history columns of the run detail read: two correlated subqueries that belong to the one statement
/// selecting the run row, so the run's state and its history come from a single snapshot — read separately, a cancel
/// committing in between would serve the old state beside a history that ends canceled. The table has no writer this
/// module calls: the trigger on <c>public.run</c> appends every row, so this is the only side here.
/// </summary>
internal static class RunStateHistorySql
{
    /// <summary>
    /// The history of the run row that the surrounding statement aliases <c>r</c>, as two columns. The marker row —
    /// sequence 1, no from state — is what makes a run's history knowable at all: a run with none was created before
    /// the table existed, so its answer is not "no transition" but "unknown", and that marker is never served because
    /// only rows naming a from state are aggregated.
    /// <para>
    /// The order is the row's own sequence, not <c>occurred_at</c>, so a clock that steps backwards cannot reorder a
    /// run's history. <c>64</c> is the contract's bound, and it is headroom, never a truncation a real run reaches: the
    /// forward ranks are fixed (<c>created</c> 0, <c>queued</c> 1, <c>running</c> 2, <c>collecting</c> 3) and a terminal
    /// state has no outgoing edge, so the longest declared path is four transitions
    /// (<c>created → queued → running → collecting → complete | incomplete</c>), while a run is inserted already
    /// <c>queued</c>, which leaves at most three served.
    /// </para>
    /// </summary>
    internal const string HistoryColumns = """
        (select exists (
                    select 1
                      from public.run_state_event m
                     where m.org_id = r.org_id
                       and m.run_id = r.id
                       and m.seq = 1
                       and m.from_state is null)) as state_history_known,
               (select coalesce(
                           jsonb_agg(
                               jsonb_build_object('from_state', h.from_state, 'to_state', h.to_state,
                                                  'occurred_at', h.occurred_at)
                               order by h.seq),
                           '[]'::jsonb)::text
                  from (select e.from_state, e.to_state, e.occurred_at, e.seq
                          from public.run_state_event e
                         where e.org_id = r.org_id
                           and e.run_id = r.id
                           and e.from_state is not null
                         order by e.seq
                         limit 64) h) as state_history
        """;
}
