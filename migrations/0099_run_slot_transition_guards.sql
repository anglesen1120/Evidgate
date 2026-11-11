-- The 00 §8.1 run state machine and the producer-slot invariants, enforced in the database. Until now only the C#
-- aggregate (`RunStateMachine`) refused an illegal `run.state` move, and nothing refused a reopened slot or a
-- regressing sequence, so a stale or racing writer holding the column grant (0036 grants `state`, 0034 grants
-- `last_sequence`, 0035 grants `closed_at`) could move a `complete` run back to `running`. These BEFORE UPDATE
-- triggers are the second, independent layer: they hold for every role, owner included, like 0095 does for `artifact`.
--
-- run.state allow-list (anything else raises 23514 `run_illegal_transition`, leaving the row unchanged):
--   created    -> queued | canceled
--   queued     -> running | incomplete | canceled
--   running    -> collecting | incomplete | canceled
--   collecting -> complete | incomplete | canceled
--   complete, incomplete, canceled: terminal.
-- Writing the state a row already holds is not a move and always passes (the trigger is not even fired for it).
--
-- producer_slot refusals (23514, row unchanged):
--   * `producer_slot_slot_reopened`: `closed_at` was set and the update clears or changes it, or changes the declared
--     values (`declared_last_sequence`, `declared_count`) written with it. A close is written once; the reconciler
--     compares observed evidence against those declared values, so they freeze with the close.
--   * `producer_slot_sequence_regressed`: `last_sequence` decreases. It is the observed high-water mark; every writer
--     uses `greatest(...)`, so a lower value can only be a stale overwrite.
-- A closed slot may still raise `last_sequence` (late events), so no other column is frozen.
-- INSERT is outside both guards, as in 0095: a pre-closed slot is written deliberately by the raw-report worker.
--
-- Invoker functions with an empty search_path, like `artifact_guard_transition` (0095): they only raise, need no
-- definer grant and cannot be used to widen anything. Trigger names sort before the shared
-- `*_set_updated_at_and_version` triggers, so a refused update never bumps `version` or `updated_at`.
--
-- Rollback: drop the two triggers and the two functions; no data is transformed, the guards only ever refuse writes.
--
-- Deploy: any order, one transaction. The runner applies one file in one transaction (`WithTransactionPerScript`), so
-- each function and its trigger land together or not at all.

set local lock_timeout = '5s';

create or replace function evidgate.run_guard_transition() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if (old.state, new.state) in (
        ('created', 'queued'),
        ('created', 'canceled'),
        ('queued', 'running'),
        ('queued', 'incomplete'),
        ('queued', 'canceled'),
        ('running', 'collecting'),
        ('running', 'incomplete'),
        ('running', 'canceled'),
        ('collecting', 'complete'),
        ('collecting', 'incomplete'),
        ('collecting', 'canceled')) then
      return new;
    end if;

    raise exception 'run_illegal_transition: % -> %', old.state, new.state
      using errcode = '23514';
  end;
  $$;

revoke all on function evidgate.run_guard_transition() from public;

-- Stated so a re-run over a function some other role once created ends owner-owned too.
alter function evidgate.run_guard_transition() owner to evidgate_owner;

drop trigger if exists run_guard_transition_trg on public.run;
create trigger run_guard_transition_trg
  before update of state on public.run
  for each row
  when (old.state is distinct from new.state)
  execute function evidgate.run_guard_transition();

create or replace function evidgate.producer_slot_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if old.closed_at is not null and (
        new.closed_at is distinct from old.closed_at
        or new.declared_last_sequence is distinct from old.declared_last_sequence
        or new.declared_count is distinct from old.declared_count) then
      raise exception 'producer_slot_slot_reopened: a close is written once'
        using errcode = '23514';
    end if;

    if new.last_sequence < old.last_sequence then
      raise exception 'producer_slot_sequence_regressed: % -> %', old.last_sequence, new.last_sequence
        using errcode = '23514';
    end if;

    return new;
  end;
  $$;

revoke all on function evidgate.producer_slot_guard() from public;

alter function evidgate.producer_slot_guard() owner to evidgate_owner;

drop trigger if exists producer_slot_guard_trg on public.producer_slot;
create trigger producer_slot_guard_trg
  before update of closed_at, last_sequence, declared_last_sequence, declared_count on public.producer_slot
  for each row
  execute function evidgate.producer_slot_guard();
