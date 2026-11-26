using Evidgate.Modules.Ingestion.Contracts;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>The statements of the producer registry; every one runs in the caller's tenant transaction.</summary>
internal static class ProducerSlotSql
{
    /// <summary>
    /// The per-shard insert of one run (AC2, P2): the ids arrive as a <c>uuid[]</c> and the shard
    /// index is the array's 1-based ordinal, so every slot is written in one statement.
    /// <c>on conflict do nothing</c> absorbs a concurrent registration on either run-scoped unique
    /// (<c>producer_slot_run_producer_key</c>/<c>producer_slot_run_shard_key</c>) instead of raising a
    /// 23505 that would abort the caller's transaction; the caller re-reads and decides.
    /// </summary>
    internal const string InsertSlots = """
        insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id)
        select u.id, @org_id, @run_id, u.ord, 'shard-' || u.ord
          from unnest(@ids::uuid[]) with ordinality as u(id, ord)
        on conflict do nothing
        """;

    /// <summary>The slots of one run, in shard order (P4, served by the <c>producer_slot_run</c> index).</summary>
    internal const string ReadRunSlots = """
        select id, shard_index, producer_id, bound_token_jti, last_sequence, closed_at,
               declared_last_sequence, declared_count
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
         order by shard_index
        """;

    /// <summary>
    /// One slot by its producer id, with the declared values (T68): the single indexed lookup of
    /// <see cref="IProducerRegistry.BindOrCheckAsync"/> (P3) and the read
    /// <see cref="IProducerRegistry.CloseSlotAsync"/> decides on.
    /// </summary>
    internal const string ReadByProducer = """
        select id, shard_index, producer_id, bound_token_jti,
               last_sequence, closed_at, declared_last_sequence, declared_count
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
        """;

    /// <summary>
    /// The first-write bind (T9): a conditional UPDATE that writes only an unbound slot, so the
    /// statement itself is the race arbiter - a second first writer matches no row and returns none.
    /// The owner is the authenticated credential id, kept in the historical <c>bound_token_jti</c> column.
    /// </summary>
    internal const string BindIfUnbound = """
        update public.producer_slot
           set bound_token_jti = @owner_id
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
           and bound_token_jti is null
        returning id, shard_index, producer_id, bound_token_jti, last_sequence, closed_at
        """;

    /// <summary>
    /// The one-shot close (T68, AC9): the whole identity guard sits in the <c>WHERE</c> - an unclosed
    /// slot bound to exactly this owner - so the statement itself is the race arbiter and a second
    /// finalize matches no row. <c>closed_at</c> and the two declared values are written together once.
    /// </summary>
    internal const string CloseIfOpen = """
        update public.producer_slot
           set closed_at = now(),
               declared_last_sequence = @declared_last_sequence,
               declared_count = @declared_count
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
           and closed_at is null
           and bound_token_jti = @owner_id
        returning id, shard_index, producer_id, closed_at, declared_last_sequence, declared_count
        """;
}
