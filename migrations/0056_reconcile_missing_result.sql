-- 0056_reconcile_missing_result.sql
--
-- WHY: `evidence_revision.missing[]` carries the 00 §8.5 reason codes of an incomplete revision, and
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
-- never the hold: once the statement is in, the file runs to completion within the migrator's command
-- timeout (`LOCK_TIMEOUT`/`MigratorOptions.CommandTimeoutVariable` governs that). Exit 3 (timeout, file
-- rolled back and unjournalled) means "apply again while the table is quiet", not a defect. Expect one
-- exclusive window roughly as long as a sequential scan plus a rewrite-free constraint check of the
-- table's row count.
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
