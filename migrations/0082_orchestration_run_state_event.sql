-- 0082_orchestration_run_state_event.sql
--
-- WHY: the run's state history had no durable source. `audit_log`'s `run.state_changed` rows carry no from/to pair,
-- the outbox is an un-tenanted queue that is pruned, and `run` itself only holds the current state, so a screen or a
-- support question could not answer which states a run passed through. This file is that record; it is written by the
-- database, not by the four C# writers, so a transition cannot commit without its history row.
--
-- SHAPE: one append-only table, one writer.
--   run_state_event  one row per state the run has held. `seq` is the run's own sequence (1-based, gapless), and
--                    `(org_id, run_id, seq)` is the primary key, so a duplicate sequence is impossible rather than
--                    merely unlikely. `from_state` is NULL exactly on the creation row — the marker that the run's
--                    history is known at all — and every later row names the state it left. The surrogate `id` and
--                    `unique (org_id, id)` exist because every tenant table declares that composite key as the target
--                    of a composite tenant foreign key (00 §5.4.6, the 0033/0039 precedent).
--   the writer       one trigger function on `public.run`: `AFTER INSERT` records the creation row (`seq` 1,
--                    `from_state` NULL, `to_state` = the inserted state, which the create path writes as `queued` and
--                    the seeded `created` row writes as `created`), and `AFTER UPDATE OF state` records one row per
--                    real state change. A same-state update (the T82 replay/no-op) writes nothing.
--
-- ORDER OF WRITES: `seq` is `coalesce(max(seq), 0) + 1` for the run. That is safe without a lock of its own because
-- every writer of one run's state serialises on the run row itself: the UPDATE that fires this trigger holds that
-- row's lock until commit, so the second updater reads the first one's committed sequence. A concurrent INSERT of the
-- same run cannot exist (it is a primary-key collision), so the creation row is allocated once.
--
-- CHECKS: the state vocabulary is closed in the database, the same list `run_state_check` (0031) enumerates, so a
-- bypassing writer cannot store a token no screen understands. `seq >= 1`, and a transition row's `from_state` differs
-- from its `to_state` — a no-op never reaches the table, so a row that claims one is a writer bug, not history.
--
-- MARKER / NO BACKFILL: rows are not backfilled. A run created before this table has no `seq` 1 row with a NULL
-- `from_state`, so readers must answer "unknown" for it, not "no transition yet" (`[]`). A pre-migration run that
-- transitions afterwards gets its first row at `seq` 1 with a non-NULL `from_state`, so the marker stays absent and
-- the history stays unknown — the trigger cannot invent the state a run was created in.
--
-- RLS: ENABLE + FORCE, with the tenant policy `to evidgate_app` over `evidgate.current_org_id()` (never the raw
-- setting, so a missing or blank setting yields zero rows) and the owner backfill policy FORCE requires
-- (0004/0072/0059 precedent). The project half of a read is the statement's own predicate — a run id resolved inside
-- the caller's project — because a policy over `current_project_id()` would refuse a project-wide read; the table
-- carries no `project_id` for the same reason `release_run` carries none.
--
-- GRANTS: `evidgate_app` holds SELECT and nothing else — no INSERT, UPDATE, DELETE or TRUNCATE — so the only path that
-- writes a row is the trigger, which runs as the table owner (`security definer`, below) and is what makes the app's
-- run INSERT/UPDATE able to append history without holding a write grant here. `evidgate_operator` keeps its default
-- SELECT (no policy, so it reads zero rows until a grant-gated operator policy lands — 0028/0069/0072 posture).
-- `evidgate_worker_sched` holds nothing: no scheduled job reads or writes run history.
--
-- GUARDS: append-only for every role, not only the app one. An `UPDATE` or a `DELETE` raises 23514 (`run_state_event_
-- immutable`), which is the backstop for the owner and for any future grant; an erasure that must remove a run's
-- history disables the guard first, exactly as the other append-only tables' erasure handover records. The composite
-- foreign key to `run` has no `ON DELETE`: a run with history is erased through that erasure path, never by a cascade
-- that would silently delete the record of what happened.
--
-- LOCKS: `set local lock_timeout = '5s'` bounds the DDL's wait, so a busy database fails the migration for a retry
-- instead of stalling run writes. Everything here is a catalogue write on a new relation plus one trigger on `run`,
-- which takes a share-versus-exclusive lock only for the trigger creation.
--
-- ORDER: additive — a new relation plus a trigger nothing read before — so apply it before the API that serves the
-- history. No down migration: dropping the table would delete every run's recorded history, which no rollback
-- restores, and dropping the triggers would leave committed rows that no longer grow.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`, `drop policy/trigger if
-- exists` before every policy and trigger, `create or replace function`, and the repeatable `revoke`/`grant`.

set local lock_timeout = '5s';

create table if not exists public.run_state_event (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  seq integer not null,
  from_state text,
  to_state text not null,
  occurred_at timestamptz not null default clock_timestamp(),
  constraint run_state_event_pkey primary key (org_id, run_id, seq),
  constraint run_state_event_org_id_key unique (org_id, id),
  constraint run_state_event_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint run_state_event_seq_check check (seq >= 1),
  -- The closed 00 §8.1 vocabulary, the same list `run_state_check` (0031) holds. A new state is a new migration.
  constraint run_state_event_to_state_check check (to_state in
    ('created', 'queued', 'running', 'collecting', 'complete', 'incomplete', 'canceled')),
  constraint run_state_event_from_state_check check (from_state is null or from_state in
    ('created', 'queued', 'running', 'collecting', 'complete', 'incomplete', 'canceled')),
  -- A no-op transition writes no row, so a stored row always names a change.
  constraint run_state_event_changed_check check (from_state is null or from_state <> to_state)
);

-- The primary key's `(org_id, run_id)` prefix serves both the per-run read in `seq` order and the referencing side of
-- the run foreign key, so no second index is added.

alter table public.run_state_event enable row level security;
alter table public.run_state_event force row level security;

drop policy if exists run_state_event_app_tenant on public.run_state_event;
create policy run_state_event_app_tenant on public.run_state_event
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy: the operator's default SELECT reads zero rows until the
-- grant-gated operator policy lands (0028/0069/0072 posture).
drop policy if exists run_state_event_owner_backfill on public.run_state_event;
create policy run_state_event_owner_backfill on public.run_state_event
  for all to evidgate_owner using (true) with check (true);

-- The revoke precedes the grant because 0001's default privileges would otherwise leave full DML in place.
revoke insert, update, delete, truncate on public.run_state_event from evidgate_app;
grant select on public.run_state_event to evidgate_app;
grant select on public.run_state_event to evidgate_operator;
revoke all on public.run_state_event from evidgate_worker_sched;

-- lint: audit-deferred shared-run-state-history evidgate.orchestration_run_state_event_append
--
-- WHY NO PER-CALL AUDIT ROW: the row this trigger appends IS the durable record of the act. The transition that fires
-- it already writes its own `run.state_changed` audit row and outbox event in the caller's transaction, so a second
-- row here would duplicate the same transition in another chain, and a definer cannot see the caller's request id.
--
-- WHY `security definer`: the app role holds SELECT only on the table, so an app-role run INSERT/UPDATE would be
-- refused by the ACL when the trigger body ran as its invoker. The function runs as the migration owner, which the
-- owner backfill policy admits under FORCE RLS; `search_path = ''` keeps every name below schema-qualified.
create or replace function evidgate.orchestration_run_state_event_append() returns trigger
  language plpgsql
  security definer
  set search_path = ''
  as $$
  begin
    -- A replay of the state a run already holds is not a transition (T82): nothing is appended, so `seq` stays gapless
    -- and the history has no zero-length moves.
    if tg_op = 'UPDATE' and old.state is not distinct from new.state then
      return null;
    end if;

    -- The aggregate form keeps the allocation and the insert one statement: over zero rows it still returns one row
    -- with `max(seq)` NULL, which `coalesce` turns into the creation row's sequence 1.
    insert into public.run_state_event (id, org_id, run_id, seq, from_state, to_state)
    select uuidv7(),
           new.org_id,
           new.id,
           coalesce(max(e.seq), 0) + 1,
           case when tg_op = 'INSERT' then null else old.state end,
           new.state
      from public.run_state_event e
     where e.org_id = new.org_id
       and e.run_id = new.id;

    return null;
  end;
  $$;

revoke all on function evidgate.orchestration_run_state_event_append() from public;

-- A trigger function has no EXECUTE grant to make: triggers are invoked by the system, and `public` is revoked so no
-- role can call it directly and append history to a run it does not own.

drop trigger if exists run_state_event_created on public.run;
create trigger run_state_event_created
  after insert on public.run
  for each row execute function evidgate.orchestration_run_state_event_append();

drop trigger if exists run_state_event_transition on public.run;
create trigger run_state_event_transition
  after update of state on public.run
  for each row execute function evidgate.orchestration_run_state_event_append();

-- Append-only for every role: the grants keep the app role out, and this is the backstop for the owner and for any
-- future grant. An erasure that must remove history disables this trigger first (the other append-only tables record
-- the same handover).
create or replace function evidgate.orchestration_run_state_event_immutable() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    raise exception 'run_state_event_immutable: a run state event is append-only (%)', tg_op
      using errcode = '23514';
  end;
  $$;

revoke all on function evidgate.orchestration_run_state_event_immutable() from public;

drop trigger if exists run_state_event_immutable on public.run_state_event;
create trigger run_state_event_immutable
  before update or delete on public.run_state_event
  for each row execute function evidgate.orchestration_run_state_event_immutable();
