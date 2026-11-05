-- 0070_orchestration_case_attempt_after_cancel.sql
--
-- WHY: an attempt written for a run that is already `canceled` has to be distinguishable from one written before the
-- cancel. `event` carries `after_cancel` (0034), but `case_attempt` (0032) had no such flag, so the stored rows of a
-- canceled run read the same whether the evidence arrived before the cancel or after it — and 00 §8.1's
-- after-cancel race rule asks for the mark on the rows it stamps, not only on the event stream.
--
-- SHAPE: one boolean, NOT NULL with a `false` default, so every attempt written before this file reads as
-- "not after the cancel" without a backfill. The writer derives it at insert, in the same statement that inserts the
-- row: `CaseAttemptSql.InsertAttempts` already joins `public.run` for the scope's project visibility, and its caller
-- (the ingest batch handler) has taken that row `FOR SHARE` in the same transaction, so the value is serialised with
-- the cancel's `FOR NO KEY UPDATE` (the lock `IRunStateTransition` takes) and can never miss a committed cancel.
--
-- GRANTS: nothing to add. 0032 revokes `update, delete` and grants table-level `select, insert`, which covers a
-- column added later, so `evidgate_app` can write and read `after_cancel` and still cannot rewrite it: the value is
-- a fact of the insert, not a state the application moves.
--
-- GUARD: no trigger. The guard convention exists for columns an UPDATE may move; an attempt row is create-time-only
-- by grant, so there is no transition to police and a trigger would only restate the ACL.
--
-- LOCKS: `set local lock_timeout` bounds the DDL's wait, so a busy table fails the migration for a retry instead of
-- stalling ingest writes. The statement itself is a catalogue write: on PostgreSQL 11+ a non-volatile `default` is
-- recorded in `pg_attribute` and no existing row is rewritten, so the table's size does not matter.
--
-- ORDER: additive for every reader and writer — a row inserted before the column exists still reads `false` — so
-- apply this before the ingest worker that writes the flag. No down migration: dropping the column would erase
-- which attempts arrived after a cancel, which no rollback restores.
--
-- Idempotent in the shape the README's file contract allows: `add column if not exists`, so a re-run is a no-op.

set local lock_timeout = '5s';

alter table public.case_attempt
  add column if not exists after_cancel boolean not null default false;
