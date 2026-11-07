namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The run state history read: one statement for the marker and the transitions, on the ambient tenant scope, so RLS —
/// not a `where` clause alone — hides another org's rows. The table has no writer this module calls: the trigger on
/// <c>public.run</c> appends every row, so the read is the only side here.
/// </summary>
internal static class RunStateHistorySql
{
    /// <summary>
    /// The run's recorded transitions in the order the trigger assigned, with the marker carried alongside them. The
    /// marker row — sequence 1, no from state — is what makes a run's history knowable at all: a run with none was
    /// created before the table existed, so its answers are not "no transition" but "unknown", and the marker row it
    /// would have written is not a transition and is never served. The left join is what keeps that distinction in one
    /// round trip: with no marker the statement still answers one row, with <c>known</c> false.
    /// <para>
    /// The order is the row's own sequence, not <c>occurred_at</c>, so a clock that steps backwards cannot reorder a
    /// run's history. <c>64</c> is the contract's bound, and it is headroom, never a truncation a real run reaches: the
    /// forward ranks are fixed (<c>created</c> 0, <c>queued</c> 1, <c>running</c> 2, <c>collecting</c> 3) and a terminal
    /// state has no outgoing edge, so the longest declared path is four transitions
    /// (<c>created → queued → running → collecting → complete | incomplete</c>), while a run is inserted already
    /// <c>queued</c>, which leaves at most three served.
    /// </para>
    /// </summary>
    internal const string HistoryForRun = """
        select marker.known, e.from_state, e.to_state, e.occurred_at
          from (select exists (
                           select 1
                             from public.run_state_event m
                            where m.org_id = @org_id
                              and m.run_id = @run_id
                              and m.seq = 1
                              and m.from_state is null) as known) marker
          left join public.run_state_event e
            on marker.known
           and e.org_id = @org_id
           and e.run_id = @run_id
           and e.from_state is not null
         order by e.seq
         limit 64
        """;
}
