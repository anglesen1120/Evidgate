namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The run's producer-slot facts as one column of the statement that selects the run row (aliased <c>r</c>): one item
/// per stored <c>producer_slot</c> row, with the slot's own state and the counts of the <c>event</c> rows recorded
/// against it. Beside the run row rather than behind a second read, so the counts and the run's state share one
/// snapshot. The binding values themselves — the token's <c>jti</c> and the CI run ref — are never selected: the
/// column reports only whether the slot is bound at all.
/// </summary>
internal static class ProducerSlotFactsSql
{
    /// <summary>
    /// The most slots one run detail serves. <c>producer_slot.shard_index</c> is unique per run and checked
    /// <c>1..256</c> (0029), so a run can never register more than this and the cap is headroom, never a truncation a
    /// real run reaches; it is spelled out so the read has a stated bound like the release list does.
    /// </summary>
    internal const int MaxProducerSlots = 256;

    /// <summary>
    /// One item per slot in <c>shard_index</c> order — the order <c>declared_producers</c> reports, so a client can
    /// zip the two lists. The event counts come from one aggregate over the run's events grouped by producer, not a
    /// subquery per slot, so the statement count stays flat in the number of shards; the left join keeps a slot that
    /// recorded no event at all with counts of zero instead of dropping it. A slot that is neither bound nor finalized
    /// reports <c>unbound</c>/<c>open</c>, and <c>expected</c> is null until the slot closes and declares its count.
    /// A count that disagrees with <c>expected</c> is served as stored: the mismatch is the client's to derive.
    /// </summary>
    internal const string SlotFactsColumn = """
        (select coalesce(
                    jsonb_agg(
                        jsonb_build_object('producer_id', s.producer_id,
                                           'identity_state', s.identity_state,
                                           'finalize_status', s.finalize_status,
                                           'last_sequence', s.last_sequence,
                                           'count', s.event_count,
                                           'expected', s.declared_count,
                                           'stale_epoch_events', s.stale_events,
                                           'after_cancel_events', s.after_cancel_events)
                        order by s.shard_index),
                    '[]'::jsonb)::text
           from (select p.producer_id, p.shard_index, p.last_sequence, p.declared_count,
                        case when p.bound_token_jti is not null or p.bound_ci_run_ref is not null
                             then 'bound' else 'unbound' end as identity_state,
                        case when p.closed_at is not null then 'finalized' else 'open' end as finalize_status,
                        coalesce(e.event_count, 0) as event_count,
                        coalesce(e.stale_events, 0) as stale_events,
                        coalesce(e.after_cancel_events, 0) as after_cancel_events
                   from public.producer_slot p
                   left join (select ev.producer_id,
                                     count(*) as event_count,
                                     count(*) filter (where ev.stale) as stale_events,
                                     count(*) filter (where ev.after_cancel) as after_cancel_events
                                from public.event ev
                               where ev.org_id = r.org_id and ev.run_id = r.id
                               group by ev.producer_id) e
                     on e.producer_id = p.producer_id
                  where p.org_id = r.org_id and p.run_id = r.id
                  order by p.shard_index
                  limit @producer_slot_limit) s) as producer_slots
        """;
}
