-- 0058_reconcile_overdue_running.sql
--
-- WHY: 0056 shipped the collection-deadline scan over `collecting` runs alone, and the note it carried said
-- why: a queued attempt for any other state answered `NotCollectable`, so queuing one would only spend the
-- run's single deadline key. That held because the reconciler gated on the state before it read the deadline.
-- The consequence is a stall nothing else can break: a run is enqueued for reconcile only when a finalize
-- closes a producer slot, so a run whose producers never started (`queued`) or died before their first
-- finalize (`running`) is never looked at again. It stays open past `collection_deadline_at` forever, with no
-- outbox row, no verdict and no reader — the same class of stall 0056 closed for `collecting`, one state
-- earlier. The deadline is the run's promise to the caller: past it the evidence is whatever arrived, and
-- missing evidence never reads as a pass, so the run is closed `incomplete` with the reasons that say what
-- never arrived.
--
-- WHAT: part 1 replaces the partial index the scan seeks through with one over the wider state set. The
-- scan orders and keysets on `(org_id, id)` and `public.run` keeps every finished run forever, so the index
-- stays partial: one entry per in-flight run, none for history. `state in ('queued', 'running', 'collecting')`
-- is the exact predicate of the widened scan, and it covers the old index's predicate, so `run_collecting_idx`
-- is dropped in the same file: keeping both would write a second entry per collecting run for every insert and
-- state update, and no query needs a collecting-only variant (a narrower `state = 'collecting'` predicate
-- implies the wider one, so the planner reads the new index for it). Part 2 replaces
-- `evidgate.reconcile_overdue_runs` with the same signature, return shape, definer posture, search_path,
-- database clock, keyset cursor and clamped limit over the wider predicate. The three states are exactly the
-- ones with a declared edge to `incomplete` (`RunStateMachine.DeclaredEdges`: queued, running and collecting
-- each carry one). `created` is excluded: it has not started, no shard plan is committed for it, and
-- `created -> incomplete` is not an edge, so closing one at a deadline would invent a transition the machine
-- does not declare. The three terminal states have no outgoing edge at all, so their verdict stands.
--
-- GRANTS: unchanged. The function keeps `security definer` on `evidgate_owner` (the only role with a
-- policy-free view; `scripts/check-rls.py` audits the body), `public` revoked and EXECUTE for
-- `evidgate_worker_sched` alone; `create or replace` keeps the owner and the `proconfig` search_path, and the
-- two privilege statements are repeated so the file is self-contained. An index is not granted on, and the
-- sweep role reaches it only through the definer function, which holds no table privilege.
--
-- ORDER: migrate before app, in the same deploy. The widened scan is what lets the sweep queue a `queued` or
-- `running` run at all, and only the reconciler shipped with this file can decide one: an application built
-- before it answers `NotCollectable` for such an attempt and completes it without a verdict. That costs one
-- attempt, not the run: the sweep re-arms a spent attempt — done, dead-lettered, or its task purged — up to
-- `RunReconcileContract.DeadlineMaxAttempts` (three), so the run is attempted again after the deployment, and
-- only an exhausted bound leaves it open for an operator to see. Deploy the app immediately after the
-- migration; the old application otherwise keeps working, because it never sees the rows the scan returns
-- today, and every attempt it wastes is retried.
--
-- LOCKS: `create index` (not `concurrently`, which cannot run inside the migrator's per-script transaction)
-- takes a SHARE lock on `public.run`: readers continue while it builds, inserts and updates wait behind it. The
-- same transaction then runs `drop index`, which needs ACCESS EXCLUSIVE and queues NEW readers behind it for as
-- long as it waits — and because the whole script is one transaction, a `lock_timeout` on that drop rolls back
-- the completed index build. Apply this file in a quiet window; exit 3 means "apply again while the table is
-- quiet", not a defect. The new index is created BEFORE the old one is dropped, so no statement runs without a
-- usable partial index; `create or replace function` plus the two privilege statements are catalog writes on a
-- function the sweep calls every ten minutes, and the same bound covers them.
--
-- Forward-only: `create index if not exists`, `drop index if exists`, `create or replace` and repeatable
-- `revoke`/`grant`, so a re-run is a no-op in substance, and there is no down migration. Dropping the wider
-- index or restoring the collecting-only predicate would silently return the platform to the stall this file
-- removes (a run past its deadline that nothing ever decides), not roll back a behaviour.

set local lock_timeout = '5s';

create index if not exists run_overdue_idx
  on public.run (org_id, id)
  where state in ('queued', 'running', 'collecting');

drop index if exists public.run_collecting_idx;

create or replace function evidgate.reconcile_overdue_runs(
  p_after_org uuid,
  p_after_run uuid,
  p_limit    integer)
  returns table (org_id uuid, run_id uuid, collection_deadline_at timestamptz)
  language sql
  stable
  security definer
  set search_path = ''
  as $$
    select r.org_id, r.id, r.collection_deadline_at
      from public.run r
     where r.state in ('queued', 'running', 'collecting')
       and r.collection_deadline_at <= now()
       and (r.org_id, r.id) > (coalesce(p_after_org, '00000000-0000-0000-0000-000000000000'::uuid),
                               coalesce(p_after_run, '00000000-0000-0000-0000-000000000000'::uuid))
     order by r.org_id, r.id
     limit least(greatest(coalesce(p_limit, 100), 1), 1000)
  $$;

revoke all on function evidgate.reconcile_overdue_runs(uuid, uuid, integer) from public;

grant execute on function evidgate.reconcile_overdue_runs(uuid, uuid, integer) to evidgate_worker_sched;
