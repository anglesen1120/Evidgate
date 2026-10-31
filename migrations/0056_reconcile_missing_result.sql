-- 0056_reconcile_missing_result.sql
--
-- The file has two parts, both on the reconcile outcome. Part 1 (below, through the constraint
-- validation) widens the `missing[]` vocabulary with `missing_result`. Part 2 (at the end) adds the
-- partial index the overdue scan seeks through and `evidgate.reconcile_overdue_runs`, the read-only
-- projection the collection-deadline sweep scans with: the verdict of part 1 is what a run past its
-- deadline is decided with, and part 2 is what makes an attempt happen for a run whose shard never
-- finalizes. Each part carries its own WHY/WHAT/GRANTS block.
--
-- WHY: `evidence_revision.missing[]` carries the reason codes of an incomplete revision, and
-- 0049's CHECK pinned that vocabulary to the seven codes the event comparison and the artifact freeze
-- can name. The reconciler now also decides completeness from the run's PLAN: a planned execution with
-- no recorded `case_attempt` never produced a status, so the run is incomplete whatever the producers
-- declared, and the code that says so is `missing_result`. Without the literal in the CHECK, that
-- revision insert would be refused 23514 and the attempt would be retried until the queue dead-lettered
-- it — the fail-closed verdict would become a stuck run. The code is the eighth and last member of the
-- closed list; nothing else about the vocabulary changes.
--
-- WHAT: `evidence_revision_missing_check` is dropped and re-created over the 0049 list with
-- `missing_result` appended. The predicate is copied verbatim from 0049 — the `<@` membership test over
-- one-level arrays, the `array_position(missing, null)` term that rejects a null element, and the `CASE`
-- that runs the dimension test first (a CHECK's term order is not guaranteed, and `array_position`
-- raises 0A000 on a multi-dimensional array). Only the array literal grows. The new constraint is added
-- `not valid` and then validated: the row scan is its own statement, so a scan failure names the phase
-- that failed, and the shape matches the three-step form 0031 used for its deferred foreign keys. It
-- does NOT make the scan cheaper in lock terms (see LOCKS): one transaction per script means the
-- ACCESS EXCLUSIVE taken above is still held while `validate constraint` scans. Every row that
-- satisfied the old list satisfies the new one
-- (the change only ADDS a member), so the validation cannot fail on existing history; if it ever does,
-- the file rolls back and the database keeps the 0049 constraint rather than an unvalidated one.
--
-- ORDER: migrate before app. The new literal is additive for readers (an old application never writes
-- `missing_result`) and the widened CHECK accepts every code the old application knows, so the old app
-- keeps working against the migrated database while the new one is rolled out. An application that
-- writes `missing_result` before this file has run fails the insert, which is why the file goes first.
--
-- LOCKS: `drop constraint` / `add constraint` are ALTER TABLE-class changes that need ACCESS EXCLUSIVE
-- on `evidence_revision`, a table evidence inserts write. The migrator applies one transaction per
-- script (`WithTransactionPerScript` in Src/Evidgate.Migrator/MigratorRunner.cs), so that lock is held
-- from the drop until the file commits: the `add constraint ... not valid` catalog write AND the whole
-- `validate constraint` scan run under it, and every other reader or writer of the table waits for the
-- scan, not just for the catalog write. The `lock_timeout` below bounds the wait to ACQUIRE the lock,
-- never the hold: once a statement is in, the file runs to completion within the migrator's per-statement
-- command timeout, `EVIDGATE_MIGRATOR__COMMANDTIMEOUTSECONDS` (`MigratorOptions.CommandTimeoutVariable`,
-- unset = no timeout). Exit 3 (timeout, file rolled back and unjournalled) means "apply again while the
-- table is quiet", not a defect. Expect one exclusive window roughly as long as a sequential scan plus a
-- rewrite-free constraint check of the table's row count.
--
-- Forward-only: the statements are `drop constraint if exists` + `add constraint` + `validate`, so a
-- re-run is a no-op in substance, and there is no down migration (rolling the code back leaves rows
-- naming `missing_result`, which a narrower CHECK would refuse).

set local lock_timeout = '5s';

alter table public.evidence_revision
  drop constraint if exists evidence_revision_missing_check;

alter table public.evidence_revision
  add constraint evidence_revision_missing_check check (
    case
      when array_ndims(missing) is null or array_ndims(missing) = 1 then
        missing <@ array['missing_shard', 'count_mismatch', 'artifact_missing', 'build_mismatch',
                         'unknown_status', 'setup_error', 'stale_epoch', 'missing_result']::text[]
        and array_position(missing, null) is null
      else false
    end) not valid;

alter table public.evidence_revision validate constraint evidence_revision_missing_check;

-- ---------------------------------------------------------------------------------------------------
-- The scan's index on `public.run`.
-- ---------------------------------------------------------------------------------------------------
-- WHY: the scan seeks and orders on `(org_id, id)` inside one state, and `public.run` keeps every finished
-- run forever. A partial index over the collecting rows alone keeps that seek proportional to the runs a
-- sweep can actually name — one entry per in-flight run, none for history.
--
-- LOCKS: `create index` (not `concurrently`, which cannot run inside the migrator's per-script transaction)
-- takes a SHARE lock on `public.run`: readers continue while it builds, inserts and updates of the table wait
-- behind it, and `lock_timeout` above bounds the wait to acquire the lock. `if not exists` makes a re-run a
-- no-op, index and all.
--
-- GRANTS: none. The sweep role reaches this index only through the definer function below, and holds no
-- privilege on the table itself.

create index if not exists run_collecting_idx
  on public.run (org_id, id)
  where state = 'collecting';

-- ---------------------------------------------------------------------------------------------------
-- The collection-deadline scan (`evidgate.reconcile_overdue_runs`).
-- ---------------------------------------------------------------------------------------------------
-- WHY: a reconcile attempt is queued in the transaction that closes a producer slot, and the attempt
-- itself decides the run's outcome against `collection_deadline_at` (0031) on the database clock. A run
-- whose last shard never finalizes therefore gets no attempt at all: it stays `collecting` past its
-- deadline with no outbox row, and nothing in the platform looks at it again. The missing piece is a
-- periodic scan, and the platform's sweeps run as `evidgate_worker_sched`, which 0031 deliberately gives
-- no privilege on `public.run` and which has no system GUC to read around row level security — so the
-- scan cannot be a plain SELECT. `evidgate.audit_org_ids` (0015) answers the same shape for the audit
-- walk: the sweep role reaches one owner function that projects exactly the identifiers it needs, and
-- every write it then performs runs in the tenant's own scope as the app role. This function is that
-- projection for runs.
--
-- WHAT: a read-only, bounded, keyset-paged projection of `(org_id, run_id, collection_deadline_at)` for
-- the runs of any tenant that are past their collection deadline and still collectable — `state =
-- 'collecting'`, and deliberately nothing else. `running` is excluded on purpose: the attempt the scan
-- queues returns `NotCollectable` for every state but `collecting` (it checks the state before it reads
-- the deadline), and the only `running -> collecting` edge belongs to the finalize that closes a slot —
-- so a queued reconcile for a `running` run could only be a no-op, while its key (one per run) would
-- mark the deadline attempt as spent. What the sweep exists for is the run whose shard never finalizes,
-- which is stuck in `collecting`. Nothing else is exposed: no business column, no event, no plan. The
-- deadline is compared by the database (`now()` inside the body), so a caller cannot widen the window,
-- and the caller's clock cannot disagree with the clock the attempt it triggers will use. The cursor
-- `(org_id, id) > (p_after_org, p_after_run)` is ordered on the primary key's tenant prefix and then the
-- id, so a page boundary can neither repeat nor skip a run whatever else commits meanwhile. The limit is
-- clamped here as well, so a caller cannot ask for the whole table. It holds no lock, writes nothing, and
-- is `stable` — one snapshot per call, which is all a scan of this table needs.
--
-- GRANTS: `security definer` on `evidgate_owner` (the posture of every sweep helper: the owner is the
-- only role with a policy-free view, and its body is what the migration role audits), `public` revoked,
-- EXECUTE granted to `evidgate_worker_sched` alone. `evidgate_app` and `evidgate_operator` cannot call
-- it: their path to a run is the tenant routes, not the platform's.
--
-- ORDER: migrate before app. Nothing existing calls the function — it is inert until the Worker that
-- scans is rolled out — and the old application keeps working against the migrated database.
--
-- LOCKS: `create or replace function` plus the two privilege statements are catalog writes on a function
-- no other session has executed yet; the `lock_timeout` above bounds them. No table lock and no rewrite,
-- unlike the first half of this file.
--
-- Forward-only: `create or replace` and repeatable `revoke`/`grant` statements, no down migration.

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
     where r.state = 'collecting'
       and r.collection_deadline_at <= now()
       and (r.org_id, r.id) > (coalesce(p_after_org, '00000000-0000-0000-0000-000000000000'::uuid),
                               coalesce(p_after_run, '00000000-0000-0000-0000-000000000000'::uuid))
     order by r.org_id, r.id
     limit least(greatest(coalesce(p_limit, 100), 1), 1000)
  $$;

revoke all on function evidgate.reconcile_overdue_runs(uuid, uuid, integer) from public;

grant execute on function evidgate.reconcile_overdue_runs(uuid, uuid, integer) to evidgate_worker_sched;
