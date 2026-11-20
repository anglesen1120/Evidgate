-- 0114_run_completed_at.sql
--
-- WHY: completed runs admit late evidence against their original completion instant, not their collection deadline.
-- No backfill: existing complete rows keep completed_at NULL = closed (fail closed).
-- Only collecting -> complete stamps now(); appended evidence never moves the terminal state or restarts its window.
--
-- SHAPE: a nullable column, a plain validated state CHECK, and invoker guards for every role, owner included.
-- No app column grant is added: UPDATE of completed_at remains denied. The existing table-level INSERT grant
-- also covers new columns, so the insert guard refuses a caller-supplied completion instant independently of role.
-- The UPDATE OF guard runs only for explicitly targeted columns; the state trigger's assignment still stamps it.
--
-- LOCKS / ORDER: the validated CHECK scans public.run under ACCESS EXCLUSIVE; lock_timeout bounds only DDL waits.
-- Pilot-size table assumed small [INFERENCE: scan duration unmeasured]. Apply before deploying completed_at readers.
-- All changes and the journal entry share the migrator's one transaction. Guarded DDL permits replay.
-- ROLLBACK: deploy the previous readers first, restore run_guard_transition from 0099, then drop this file's
-- two triggers/functions, CHECK and column. Dropping the column loses completion instants, never evidence revisions.

set local lock_timeout = '5s';

alter table public.run add column if not exists completed_at timestamptz;

alter table public.run drop constraint if exists run_completed_at_state_check;
alter table public.run add constraint run_completed_at_state_check
  check (completed_at is null or state = 'complete');

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
      if old.state = 'collecting' and new.state = 'complete' then
        new.completed_at := now();
      end if;
      return new;
    end if;

    raise exception 'run_illegal_transition: % -> %', old.state, new.state
      using errcode = '23514';
  end;
  $$;

revoke all on function evidgate.run_guard_transition() from public;
alter function evidgate.run_guard_transition() owner to evidgate_owner;

create or replace function evidgate.run_completed_at_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if new.completed_at is distinct from old.completed_at then
      raise exception 'run_completed_at_immutable: the completion instant never changes'
        using errcode = '23514';
    end if;
    return new;
  end;
  $$;

revoke all on function evidgate.run_completed_at_guard() from public;
alter function evidgate.run_completed_at_guard() owner to evidgate_owner;

drop trigger if exists run_completed_at_guard_trg on public.run;
create trigger run_completed_at_guard_trg
  before update of completed_at on public.run
  for each row execute function evidgate.run_completed_at_guard();

create or replace function evidgate.run_completed_at_insert_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if new.completed_at is not null then
      raise exception 'run_completed_at_insert_refused: completion is stamped by the state transition'
        using errcode = '23514';
    end if;
    return new;
  end;
  $$;

revoke all on function evidgate.run_completed_at_insert_guard() from public;
alter function evidgate.run_completed_at_insert_guard() owner to evidgate_owner;

drop trigger if exists run_completed_at_insert_guard_trg on public.run;
create trigger run_completed_at_insert_guard_trg
  before insert on public.run
  for each row execute function evidgate.run_completed_at_insert_guard();
