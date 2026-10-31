namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The SQL of one reconcile attempt (<c>ReconcileRunTask</c>): exactly four statements,
/// each over the ambient tenant transaction (00 §5.5 C3) and each filtering <c>org_id</c> explicitly as well as
/// relying on row level security, exactly like <see cref="OrchestratorSql"/>. Read-only by construction — none of
/// them writes, none reads a payload column, and none issues a statement per event, per producer or per planned
/// execution (00 §5.7 P3).
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
    /// <para>
    /// <c>deadline_passed</c> is the collection deadline evaluated by the database, in the same statement and under the
    /// same lock, so no application clock decides it and the flag cannot disagree with the row it was read from. It is
    /// <c>coalesce(r.collection_deadline_at &lt;= now(), false)</c>: <c>0031</c> declares the column <c>NOT NULL</c>, so
    /// the coalesce never fires today — it keeps a NULL (were the column ever relaxed) meaning "not passed", i.e. the
    /// attempt waits for every planned shard instead of forcing a decision on an unset deadline (fail open on waiting,
    /// never on completeness). A passed deadline is what 05.3.2-T3.1 needs to force the verdict.
    /// </para>
    /// </summary>
    internal const string LockRun = """
        select r.state as state,
               coalesce(r.collection_deadline_at <= now(), false) as deadline_passed
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
    /// The observed evidence, in <b>one</b> statement and with no raw copy of the run's rows: the per-producer counts and
    /// sequence bounds are aggregated straight from <c>public.event</c> joined to the run's slots (both branches served
    /// by <c>event_run_producer_sequence_key</c>, whose leading columns are <c>org_id, run_id</c>), and the unfinished
    /// attempts are aggregated from the run's <c>test.started</c> rows alone. Every stored event counts as
    /// acknowledged, whatever its <c>late</c>/<c>after_cancel</c>/<c>stale</c> flag; no payload column is read and no
    /// <c>org_id</c> predicate is left implicit.
    /// <para>
    /// The aggregate starts from the run's slots, so every slot answers exactly one row (a producer that stored no event
    /// answers zeros and NULL min/max, which the comparer reads like an absent row). <c>count_up_to_declared</c> is 0
    /// for an open slot because its declaration is NULL; <c>count_beyond_declared</c> is every event for an open slot,
    /// matching <see cref="Application.Runs.ObservedShard"/>.
    /// </para>
    /// <para>
    /// <c>unfinished_started_count</c> counts the distinct <c>(execution_key, attempt_no)</c> attempts that have a
    /// <c>test.started</c> row and no <c>test.finished</c> row — a status that was never reported (COORDINATOR-DECISION
    /// S119, case b of <c>unknown_status</c>). The finish is looked up across <b>all</b> producers of the org and run, so
    /// a finish another producer reported clears the attempt (the status is the run's, not the shard's), while the count
    /// is attributed to the producer of the started row. Only <c>type = 'test.started'</c> rows are candidates: an
    /// unrelated type never becomes an unfinished attempt.
    /// </para>
    /// <para>
    /// A flag count rides along in the same pass and does not narrow the acknowledged total above: <c>blocked_count</c>
    /// counts the <c>test.finished</c> events whose normalized status is <c>blocked</c> — a failed setup, which marks its
    /// dependants blocked (00 §8.3). The comparer turns a non-zero count on a closed shard into <c>setup_error</c>, so a
    /// blocked event can never leave the evidence looking complete (D-S119-Q2 risk, 05.3.2-T2.3). <c>event.stale</c> is
    /// not counted: nothing writes it, because no producer's current epoch is stored anywhere to compare against, and a
    /// count that can only ever be zero would claim a check this module does not make.
    /// </para>
    /// </summary>
    internal const string ObservedEvents = """
        with grouped as (
            select s.producer_id as producer_id,
                   count(e.sequence) filter (where e.sequence <= s.declared_last_sequence) as count_up_to_declared,
                   count(e.sequence) filter (where s.declared_last_sequence is null or e.sequence > s.declared_last_sequence)
                       as count_beyond_declared,
                   min(e.sequence) as min_sequence,
                   max(e.sequence) as max_sequence,
                   count(e.sequence) filter (where e.type = 'test.finished' and e.normalized_status is null)
                       as unknown_status_count,
                   count(e.sequence) filter (where e.type = 'test.finished' and e.normalized_status = 'blocked')
                       as blocked_count
              from public.producer_slot s
              left join public.event e
                     on e.org_id = @org_id and e.run_id = @run_id and e.producer_id = s.producer_id
             where s.org_id = @org_id and s.run_id = @run_id
             group by s.producer_id, s.declared_last_sequence
        ),
        unfinished as (
            select st.producer_id as producer_id,
                   count(distinct (st.execution_key, st.attempt_no)) as unfinished_started_count
              from public.event st
             where st.org_id = @org_id and st.run_id = @run_id and st.type = 'test.started'
               and not exists (
                   select 1
                     from public.event f
                    where f.org_id = @org_id and f.run_id = @run_id
                      and f.type = 'test.finished'
                      and f.execution_key = st.execution_key
                      and f.attempt_no = st.attempt_no)
             group by st.producer_id
        )
        select g.producer_id as producer_id,
               g.count_up_to_declared as count_up_to_declared,
               g.count_beyond_declared as count_beyond_declared,
               g.min_sequence as min_sequence,
               g.max_sequence as max_sequence,
               g.unknown_status_count as unknown_status_count,
               coalesce(u.unfinished_started_count, 0) as unfinished_started_count,
               g.blocked_count as blocked_count
          from grouped g
          left join unfinished u on u.producer_id = g.producer_id
        """;

    /// <summary>
    /// How many uncovered executions one reconcile attempt materialises. The read runs under the run lock on every
    /// attempt that decides and a run can plan tens of thousands of executions, so it stops here; each row still carries
    /// the statement's own total, which the comparer reports as one counted reason for the rest, so the cap bounds the
    /// work without understating the gap.
    /// </summary>
    internal const int UncoveredExecutionLimit = 100;

    /// <summary>
    /// The run's planned executions that never reported a status: a <c>planned_execution</c> row whose
    /// <c>execution_key</c> has no <c>case_attempt</c>. Every <c>case_attempt</c> row carries one of 0032's six terminal
    /// <c>normalized_status</c> values (all six are verdicts, none is in flight), so the row's existence is the
    /// terminality test and no status vocabulary is copied into this module's SQL. A started attempt that has not
    /// finished writes no row at all — the aggregate above still counts its <c>test.started</c> as unfinished — so it
    /// answers here as missing, while a <c>blocked</c> dependant row with no timestamps still answers as present. One
    /// statement however many executions: the outer scan is the <c>planned_execution_run_execution_key</c> prefix
    /// <c>(org_id, run_id)</c> and each probe is the <c>case_attempt_run_execution_attempt_key</c> prefix
    /// <c>(org_id, run_id, execution_key)</c>. Both tables are FORCE RLS with an app-role tenant policy and the
    /// predicates carry <c>org_id</c> explicitly, so neither side can answer for another tenant.
    /// <para>
    /// At most <see cref="UncoveredExecutionLimit"/> rows come back, and <c>uncovered_total</c> is the number the whole
    /// predicate matches: a window count is evaluated before <c>limit</c>, so every returned row states the true gap even
    /// when it was cut short. The <c>order by</c> is what makes that cut deterministic — two attempts over the same rows
    /// read the same subset of execution keys, so the reasons and the counted remainder cannot move between attempts —
    /// while the comparer still imposes its own total order on the reasons, since one shard's keys are not what orders
    /// them. A resolver-written <c>blocked</c> dependant row counts as covered — it reports a status — while its shard's
    /// <c>setup_error</c> comes from the failing producer's own event; nothing calls <c>IBlockedAttemptResolver</c> today,
    /// and whoever wires it in must also count those rows toward <c>setup_error</c>.
    /// </para>
    /// </summary>
    internal static readonly string ListUncoveredExecutions = $"""
        select pe.execution_key as execution_key,
               pe.shard_id as shard_id,
               count(*) over () as uncovered_total
          from public.planned_execution pe
         where pe.org_id = @org_id and pe.run_id = @run_id
           and not exists (
               select 1
                 from public.case_attempt ca
                where ca.org_id = @org_id and ca.run_id = @run_id
                  and ca.execution_key = pe.execution_key)
         order by pe.execution_key
         limit {UncoveredExecutionLimit}
        """;
}
