-- The 00 §8.4 artifact state machine, enforced in the database. Until now only the C# aggregate refused an illegal
-- `artifact.state` move, so a stale or racing writer holding the column grant could still move a `rejected` row back
-- to `ready`. This BEFORE UPDATE trigger is the second, independent layer: it holds for every role, owner included.
--
-- The allow-list (anything else raises 23514 `artifact_illegal_transition`, leaving the row unchanged):
--   pending_upload -> uploaded | ready | rejected
--   uploaded       -> ready | rejected
--   ready          -> rejected (a parse verdict on stored bytes) | pending_delete (retention, secret rejection)
--   pending_delete -> deleted
--   rejected, deleted: terminal.
-- Writing the state a row already holds is not a move and always passes.
--
-- An invoker function with an empty search_path, like `artifact_scan_guard` (0066): it only raises, so it needs no
-- definer grant and cannot be used to widen anything. Its name `artifact_guard_transition_trg` sorts before
-- `artifact_set_updated_at_and_version`, so a refused update never bumps `version` or `updated_at`.
--
-- Rollback: drop the trigger and the function; no data is transformed, the guard only ever refuses writes.

create or replace function evidgate.artifact_guard_transition() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if new.state is not distinct from old.state then
      return new;
    end if;

    if (old.state, new.state) in (
        ('pending_upload', 'uploaded'),
        ('pending_upload', 'ready'),
        ('pending_upload', 'rejected'),
        ('uploaded', 'ready'),
        ('uploaded', 'rejected'),
        ('ready', 'rejected'),
        ('ready', 'pending_delete'),
        ('pending_delete', 'deleted')) then
      return new;
    end if;

    raise exception 'artifact_illegal_transition: % -> %', old.state, new.state
      using errcode = '23514';
  end;
  $$;

revoke all on function evidgate.artifact_guard_transition() from public;

-- Stated so a re-run over a function some other role once created ends owner-owned too.
alter function evidgate.artifact_guard_transition() owner to evidgate_owner;

drop trigger if exists artifact_guard_transition_trg on public.artifact;
create trigger artifact_guard_transition_trg
  before update of state on public.artifact
  for each row
  when (old.state is distinct from new.state)
  execute function evidgate.artifact_guard_transition();
