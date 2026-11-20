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
        select id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref, last_sequence, closed_at,
               declared_last_sequence, declared_count
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
         order by shard_index
        """;

    /// <summary>
    /// One slot by its producer id, with the run's CI reference (T88) and the declared values (T68): the
    /// single indexed lookup of <see cref="IProducerRegistry.BindOrCheckAsync"/> (P3) and the read
    /// <see cref="IProducerRegistry.CloseSlotAsync"/> decides on. The run join is what makes the T88
    /// comparison part of the same statement, so the enforcement costs no extra round trip; it joins on
    /// the slot's own <c>(org_id, run_id)</c>, so RLS scopes the run exactly like the slot row, and
    /// <c>ci_run_ref</c> is written once at run create and never updated, so the join needs no lock.
    /// <para>
    /// <c>run_has_ci_ref</c> is the run's own <c>ci_run_ref is not null</c>, kept beside the composed
    /// reference (L1): the composition is a <c>-&gt;&gt;</c> concatenation, so a member that were JSON null
    /// would make the whole expression NULL — indistinguishable from a run that carries no reference at
    /// all. The flag separates the two and lets the caller fail closed on a run that has one it could not
    /// compose.
    /// </para>
    /// </summary>
    internal const string ReadByProducer = """
        select s.id, s.shard_index, s.producer_id, s.bound_token_jti, s.bound_ci_run_ref,
               s.last_sequence, s.closed_at, s.declared_last_sequence, s.declared_count,
               (r.ci_run_ref is not null) as run_has_ci_ref,
               (r.ci_run_ref->>'repository_id') || '/' || (r.ci_run_ref->>'run_id') || '/' || (r.ci_run_ref->>'run_attempt') as run_ci_run_ref
          from public.producer_slot s
          join public.run r on r.org_id = s.org_id and r.id = s.run_id
         where s.org_id = @org_id
           and s.run_id = @run_id
           and s.producer_id = @producer_id
        """;

    /// <summary>
    /// The first-write bind (T9): a conditional UPDATE that writes only an unbound slot, so the
    /// statement itself is the race arbiter - a second first writer matches no row and returns none.
    /// </summary>
    internal const string BindIfUnbound = """
        update public.producer_slot
           set bound_token_jti = @jti,
               bound_ci_run_ref = @ci_run_ref
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
           and bound_token_jti is null
        returning id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref, last_sequence, closed_at
        """;

    /// <summary>
    /// The one-shot close (T68, AC9): the whole identity guard sits in the <c>WHERE</c> - an unclosed
    /// slot bound to exactly this jti and CI reference - so the statement itself is the race arbiter and
    /// a second finalize matches no row. <c>closed_at</c> and the two declared values are written
    /// together exactly once; <c>is not distinct from</c> is the null-safe equality the binding needs
    /// (<c>bound_ci_run_ref</c> is null for a non-CI producer), which the read path expresses in C#.
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
           and bound_token_jti = @jti
           and bound_ci_run_ref is not distinct from @ci_run_ref
        returning id, shard_index, producer_id, closed_at, declared_last_sequence, declared_count
        """;
}
