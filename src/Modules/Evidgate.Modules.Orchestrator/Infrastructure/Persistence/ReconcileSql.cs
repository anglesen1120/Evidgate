namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The SQL of one reconcile attempt (05.3.2-T2.1/T2.2, plan §5 <c>ReconcileRunTask</c>): exactly three statements,
/// each over the ambient tenant transaction (00 §5.5 C3) and each filtering <c>org_id</c> explicitly as well as
/// relying on row level security, exactly like <see cref="OrchestratorSql"/>. Read-only by construction — none of
/// them writes, none reads a payload column, and none issues a statement per event or per producer (00 §5.7 P3).
/// </summary>
internal static class ReconcileSql
{
    /// <summary>
    /// The run row, locked, and the state this attempt decides on. The lock is <c>FOR NO KEY UPDATE OF r</c> and is
    /// taken <b>first</b>, before any slot or event is read: the module's lock order is run → slot
    /// (<c>ProducerRegistry.BindOrCheckAsync</c>, inherited by <see cref="OrchestratorSql.LockRunForFinalize"/>), and a
    /// reconcile may transition the run (<c>collecting → complete|incomplete</c>, 05.3.2-T2.4) in the same transaction.
    /// <para>
    /// Never <c>FOR SHARE</c> (plan Notes, 05.3.2-T5 S115, LOCK ORDER): T2.4 inserts an <c>evidence_revision</c> row in
    /// this transaction, and <c>0049</c>'s insert trigger re-takes the same run row <c>FOR NO KEY UPDATE</c>. A shared
    /// lock taken first would wait to upgrade while the trigger waits behind it — the <c>40P01</c> upgrade deadlock
    /// 04.1.1-T4 already recorded.
    /// </para>
    /// <para>
    /// <c>OF r</c> names only <c>run</c>, and the clause needs an <c>UPDATE</c> column grant on it: PostgreSQL requires
    /// that of every table a locking clause names, which is why <c>0034</c> grants <c>update (updated_at)</c> on
    /// <c>public.run</c> (the same grant <see cref="OrchestratorSql.LockRunForFinalize"/> and
    /// <see cref="RunEventScopeReader"/> rely on). An invisible run answers no row, which the reconciler reports as
    /// <c>RunNotFound</c>.
    /// </para>
    /// </summary>
    internal const string LockRun = """
        select r.state as state
          from public.run r
         where r.org_id = @org_id and r.id = @run_id
           for no key update of r
        """;

    /// <summary>
    /// The run's producer slots — the shard plan, one row per slot (4.1.1-T1's <c>producer_slot_run</c> index). They are
    /// read unlocked on purpose: the run lock above serialises this attempt against a finalize, which needs the run row
    /// as well (under <c>COLLECTING</c> a finalize takes <c>FOR SHARE</c>, which conflicts with this
    /// <c>FOR NO KEY UPDATE</c>), so a slot cannot close between the two reads. <c>closed_at is not null</c> is the
    /// closed flag; the declared pair is NULL while the slot is open (<c>0035</c>'s all-or-nothing check) and stays the
    /// first close's values afterwards (T68).
    /// </summary>
    internal const string ListSlots = """
        select s.producer_id as producer_id,
               s.shard_index as shard_index,
               (s.closed_at is not null) as closed,
               s.declared_last_sequence as declared_last_sequence,
               s.declared_count as declared_count
          from public.producer_slot s
         where s.org_id = @org_id and s.run_id = @run_id
         order by s.shard_index, s.producer_id
        """;

    /// <summary>
    /// The observed evidence, in <b>one</b> statement: the run's <c>event</c> rows are read once (the
    /// <c>event_run_producer_sequence_key</c> index covers <c>org_id, run_id</c> as its leading columns) and then
    /// aggregated two ways over that one materialised set — counts per producer, and per-<c>(execution_key, attempt_no)</c>
    /// attempt flags for the <c>unknown_status</c> evidence. Every stored event counts as acknowledged, whatever its
    /// <c>late</c>/<c>after_cancel</c>/<c>stale</c> flag; no payload column is read.
    /// <para>
    /// The aggregate starts from the run's slots and joins the events to them, so every slot answers exactly one row
    /// (a producer that stored no event answers zeros and NULL min/max, which the comparer treats like an absent row).
    /// <c>count_up_to_declared</c> is 0 for an open slot because its declaration is NULL; <c>count_beyond_declared</c> is
    /// every event for an open slot, matching <see cref="Application.Runs.ObservedShard"/>. <c>unfinished_started_count</c>
    /// counts the distinct attempts that have a <c>test.started</c> and no <c>test.finished</c> event — a status that was
    /// never reported (COORDINATOR-DECISION S119, case b of <c>unknown_status</c>).
    /// </para>
    /// </summary>
    internal const string ObservedEvents = """
        with run_events as materialized (
            select e.producer_id as producer_id,
                   e.sequence as sequence,
                   e.type as type,
                   e.normalized_status as normalized_status,
                   e.execution_key as execution_key,
                   e.attempt_no as attempt_no
              from public.event e
             where e.org_id = @org_id and e.run_id = @run_id
        ),
        attempts as (
            select r.producer_id as producer_id,
                   r.execution_key as execution_key,
                   r.attempt_no as attempt_no,
                   bool_or(r.type = 'test.started') as started,
                   bool_or(r.type = 'test.finished') as finished
              from run_events r
             group by r.producer_id, r.execution_key, r.attempt_no
        ),
        unfinished as (
            select a.producer_id as producer_id,
                   count(*) as unfinished_started
              from attempts a
             where a.started and not a.finished
             group by a.producer_id
        )
        select s.producer_id as producer_id,
               count(e.sequence) as total_count,
               count(e.sequence) filter (where e.sequence <= s.declared_last_sequence) as count_up_to_declared,
               count(e.sequence) filter (where s.declared_last_sequence is null or e.sequence > s.declared_last_sequence)
                   as count_beyond_declared,
               min(e.sequence) as min_sequence,
               max(e.sequence) as max_sequence,
               count(*) filter (where e.type = 'test.finished' and e.normalized_status is null) as unknown_status_count,
               coalesce(u.unfinished_started, 0) as unfinished_started_count
          from public.producer_slot s
          left join run_events e on e.producer_id = s.producer_id
          left join unfinished u on u.producer_id = s.producer_id
         where s.org_id = @org_id and s.run_id = @run_id
         group by s.producer_id, s.declared_last_sequence, u.unfinished_started
        """;
}
