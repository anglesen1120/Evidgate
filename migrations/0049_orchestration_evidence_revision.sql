-- 0049_orchestration_evidence_revision.sql
--
-- Owner: 05.3.2-T5.1 + T5.2. `evidence_revision`: one immutable row per frozen evidence set of a run
-- (feature plan 05.3.2 §2 AC4/AC5/AC8/AC9, §4, §10 T5; 00 §8.5 completeness codes; rulings T3, T14,
-- T81; contract C6). Late evidence never rewrites a row: it appends the next `revision_no` (AC8/AC9).
-- This file is the table, its privileges, the read index, the pointer FK and its guards. The
-- reconciler that INSERTs rows (T2/T3/T4) is out of scope here.
--
-- Append-only (C6, AC9): `evidgate_app` holds SELECT + INSERT and no UPDATE/DELETE, so a revision cannot
-- be rewritten; the rejection is the ACL's (42501), before any trigger or policy sees the row
-- (0028/0034/0046 precedent). 0001's default privileges grant the app role full DML on every table the
-- owner creates in `public`, hence the explicit `revoke` before the minimal `grant`.
--
-- `completeness` is the 00 §8.5 field and `missing[]` its reason codes (`setup_error` per T14). The
-- code list is the plan §4 comment's closed list, enforced per element with `<@`; a multi-dimensional
-- array is refused (`array_ndims(missing) is null or 1`: an empty array has no dimensions, a one-level
-- array has one) and duplicates are tolerated. The check is a `CASE`, not a conjunction, because
-- `array_position` raises 0A000 on a multi-dimensional array and CHECK term order is not guaranteed, so
-- the dimension test must run and short-circuit first. One cross-column rule keeps missing evidence from
-- looking complete: `completeness = 'complete'` requires `missing = '{}'` (AC4). The converse (an
-- incomplete row naming at least one code) is the reconciler's rule, not asserted here (the plan does
-- not state it as a data invariant): an incomplete row may legitimately carry an empty `missing[]`, and
-- `outcome` is not tied to completeness (this file constrains its value set, never its pairing), so
-- readers MUST key on `completeness` alone and never infer it from `missing` or `outcome`.
--
-- `revision_no` allocation [DECISION] (panel S115, PANEL-CHOICE option A): the plan's `unique (org_id,
-- run_id, revision_no)` only forbids duplicates. This file additionally makes the sequence gap-free
-- under concurrent inserts: a BEFORE INSERT trigger takes the run row `FOR NO KEY UPDATE` (the lock the
-- 05.3.1 state machine and the reconciler already take, so inserts of one run serialise there) and
-- refuses any `revision_no` other than `max + 1` (1 for the first). A violation raises 23514.
--
-- Lock order [DECISION D-S115-Q1, proposed; ratified by the coordinator]: every writer of a run's
-- evidence takes the `run` row FIRST (`FOR NO KEY UPDATE`, or the `update … set
-- current_evidence_revision_id` that implies it), THEN inserts the `evidence_revision` row, then moves
-- the pointer, all in one transaction. The insert trigger re-takes the run lock `FOR NO KEY UPDATE`, a
-- no-op only for a holder already in that mode: a caller holding `FOR SHARE` alone is UPGRADED, and two
-- such callers can deadlock (40P01). So the T2/T3/T4 reconciler and `LateEvidenceRevisionTask` MUST take
-- `FOR NO KEY UPDATE` on the run before any other read of it; `FinalizeShardCommand` (through
-- `OrchestratorSql.ShareLockRunForFinalize`) and `RunEventScopeReader` take `FOR SHARE` today and MUST
-- NOT be chained with an evidence insert in one transaction. Taking the run first is what keeps an
-- inserter from deadlocking against 05.3.1's state transition, which also locks `run` first. The
-- reconciler must not lock an evidence_revision row before the run.
--
-- Pointer (T5.2): 0036 added `run.current_evidence_revision_id uuid null` with no FK and no app-role
-- UPDATE grant. This file (a) adds the composite FK `(org_id, current_evidence_revision_id) ->
-- evidence_revision (org_id, id)` that 0036 deferred to here (T3: a cross-tenant pointer is
-- structurally impossible; NULL stays legal, MATCH SIMPLE), (b) grants the app role `update
-- (current_evidence_revision_id)` alone on `run` so the reconciler/LateEvidenceRevisionTask can move
-- it (plan §7), and (c) guards the move with `guard_run_evidence_pointer()`: the new value must be the
-- LATEST revision of THAT run (the FK alone would accept another run's revision of the same org), and a
-- set pointer can be cleared to NULL only by `evidgate_owner`, for the erasure sweep (the run <->
-- evidence_revision FK cycle has no ON DELETE action, so the owner MUST clear the pointer, then delete
-- the revisions, then delete the runs; the app role cannot clear a set pointer). A fresh run has no
-- revisions, so `run_evidence_pointer_insert_guard` refuses an INSERT that already carries one.
--
-- Freshness is guaranteed WHEN THE POINTER MOVES, not by construction. The guard reads the revisions
-- visible to the moving transaction, so a writer that commits the insert and the pointer move in
-- separate transactions leaves the pointer one revision behind in between (indefinitely if the second
-- never runs). One transaction closes that window: the insert and the move commit or roll back
-- together, so a crash leaves neither a revision without its pointer move nor a stale pointer behind.
-- `GetRun`'s existing read of the column needs no query change. Both guards run with invoker rights (no
-- SECURITY DEFINER): the reads they make go through the caller's RLS scope, which is the same org by
-- the FK.
--
-- Index: `evidence_revision_run_idx (org_id, run_id, revision_no desc)` is the plan §4 current-revision
-- lookup (the unique constraint's index is ascending; this one serves `order by revision_no desc limit
-- 1` and the max() reads of the triggers without a backward scan of a wider key). The referencing side
-- of the pointer FK gets a partial index on `run` so an owner erasure/rewrite of one revision does not
-- scan `run` (0024/0028 precedent).
--
-- Row security: one `for all to evidgate_app` tenant policy plus an owner backfill policy, like
-- 0028/0046 (a policy with no `to` clause applies to PUBLIC and would let `evidgate_operator`, which
-- holds the SELECT grant, read any org). No operator policy until 02.8.1 (TL T76, D1).
--
-- Idempotent like 0025/0027/0028/0046: `if not exists` / `drop … if exists`, the FK added behind a
-- catalog check, so a re-run is a no-op. One file runs in one transaction (migrations/README.md).

create table if not exists public.evidence_revision (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  revision_no integer not null,
  completeness text not null,
  missing text[] not null default '{}',
  outcome text,
  artifact_set_hash text,
  event_watermark bigint not null,
  manifest_hash text not null,
  created_at timestamptz not null default now(),
  constraint evidence_revision_pkey primary key (id),
  -- T3 / 00 §5.4.6: the composite-FK target of `run.current_evidence_revision_id` below.
  constraint evidence_revision_org_id_key unique (org_id, id),
  -- 00 §8.5 / AC9: one row per (run, revision); the next one is `revision_no + 1`, never a rewrite.
  constraint evidence_revision_run_revision_key unique (org_id, run_id, revision_no),
  constraint evidence_revision_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint evidence_revision_revision_no_check check (revision_no >= 1),
  constraint evidence_revision_completeness_check check (completeness in ('complete', 'incomplete')),
  -- 00 §8.5 reason codes incl. `setup_error` (T14); the plan §4 list. `<@` also rejects a NULL element.
  -- One level only: an empty `{}` has no dimensions, a flat array has one; a nested array is refused.
  -- A CASE, because `array_position` raises 0A000 on a multi-dimensional array and CHECK term order is
  -- not guaranteed: the dimension test must run (and short-circuit) first.
  constraint evidence_revision_missing_check check (
    case
      when array_ndims(missing) is null or array_ndims(missing) = 1 then
        missing <@ array['missing_shard', 'count_mismatch', 'artifact_missing', 'build_mismatch',
                         'unknown_status', 'setup_error', 'stale_epoch']::text[]
        and array_position(missing, null) is null
      else false
    end),
  -- AC4: missing evidence never yields a `complete` row.
  constraint evidence_revision_complete_has_no_missing_check check (completeness <> 'complete' or missing = '{}'),
  constraint evidence_revision_outcome_check check (outcome is null or outcome in ('all_passed', 'has_failures', 'no_results')),
  constraint evidence_revision_watermark_check check (event_watermark >= 0),
  constraint evidence_revision_manifest_hash_check check (char_length(manifest_hash) between 1 and 200),
  constraint evidence_revision_artifact_set_hash_check check (artifact_set_hash is null or char_length(artifact_set_hash) between 1 and 200)
);

create index if not exists evidence_revision_run_idx
  on public.evidence_revision (org_id, run_id, revision_no desc);

-- Gap-free `revision_no` (see header). Invoker rights; `run` is readable and lockable by the app role
-- (SELECT plus the column UPDATE grants of 0034/0036 are what `FOR NO KEY UPDATE` requires).
create or replace function public.guard_evidence_revision_insert() returns trigger
language plpgsql as $$
declare
  expected integer;
begin
  perform 1 from public.run where org_id = new.org_id and id = new.run_id for no key update;
  select coalesce(max(revision_no), 0) + 1 into expected
    from public.evidence_revision where org_id = new.org_id and run_id = new.run_id;
  if new.revision_no <> expected then
    raise exception 'evidence_revision.revision_no must be % for run % (got %)', expected, new.run_id, new.revision_no
      using errcode = '23514';
  end if;
  return new;
end;
$$;

drop trigger if exists evidence_revision_guard_insert on public.evidence_revision;
create trigger evidence_revision_guard_insert
  before insert on public.evidence_revision
  for each row execute function public.guard_evidence_revision_insert();

alter table public.evidence_revision enable row level security;
alter table public.evidence_revision force row level security;

drop policy if exists evidence_revision_tenant on public.evidence_revision;
create policy evidence_revision_tenant on public.evidence_revision
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy (TL T76, D1): see the header.

-- `force row level security` applies to the table owner too: the migration role needs its own policy
-- (0004/0005/0028/0046 precedent); explicit and permissive on purpose, BYPASSRLS is rejected (F5).
drop policy if exists evidence_revision_owner_backfill on public.evidence_revision;
create policy evidence_revision_owner_backfill on public.evidence_revision
  for all to evidgate_owner using (true) with check (true);

-- AC9 / C6: append-only in SQL.
revoke update, delete on public.evidence_revision from evidgate_app;
grant select, insert on public.evidence_revision to evidgate_app;
grant select on public.evidence_revision to evidgate_operator;
revoke all on public.evidence_revision from evidgate_worker_sched;

-- T5.2: the pointer FK 0036 deferred. Added behind a catalog check so a re-run is a no-op.
do $$
begin
  if not exists (
    select 1 from pg_constraint
     where conname = 'run_current_evidence_revision_fk' and conrelid = 'public.run'::regclass) then
    alter table public.run
      add constraint run_current_evidence_revision_fk
      foreign key (org_id, current_evidence_revision_id)
      references public.evidence_revision (org_id, id);
  end if;
end;
$$;

create index if not exists run_current_evidence_revision_idx
  on public.run (org_id, current_evidence_revision_id) where current_evidence_revision_id is not null;

-- The pointer moves only to the latest revision of the same run. Clearing it is the owner's erasure
-- step alone (the FK cycle run <-> evidence_revision has no ON DELETE action), never the app role's.
create or replace function public.guard_run_evidence_pointer() returns trigger
language plpgsql as $$
begin
  if new.current_evidence_revision_id is null then
    -- `current_user` is the role the statement runs as (not `session_user`), so the erasure sweep as
    -- `evidgate_owner` passes while `evidgate_app` stays refused.
    if current_user = 'evidgate_owner' then
      return new;
    end if;
    raise exception 'run.current_evidence_revision_id cannot be cleared once set (run %)', new.id
      using errcode = '23514';
  end if;
  if not exists (
    select 1
      from public.evidence_revision er
     where er.org_id = new.org_id
       and er.id = new.current_evidence_revision_id
       and er.run_id = new.id
       and er.revision_no = (
         select max(l.revision_no) from public.evidence_revision l
          where l.org_id = new.org_id and l.run_id = new.id)) then
    raise exception 'run.current_evidence_revision_id must be the latest evidence_revision of run %', new.id
      using errcode = '23514';
  end if;
  return new;
end;
$$;

drop trigger if exists run_evidence_pointer_guard on public.run;
create trigger run_evidence_pointer_guard
  before update on public.run
  for each row
  when (new.current_evidence_revision_id is distinct from old.current_evidence_revision_id)
  execute function public.guard_run_evidence_pointer();

-- A fresh run has no revisions, so its pointer starts NULL. A backfill or a future INSERT grant must not
-- be able to hand a new run someone else's revision (the composite FK would accept it: same org, any id).
-- BEFORE triggers run before the FK's AFTER ROW check, so a pointer that resolves to a revision visible in
-- the caller's org is refused here (23514). A pointer that resolves to nothing visible (a missing id or
-- another org's revision, hidden by RLS) is left for the composite FK, which answers 23503: the FK stays
-- the structural backstop for exactly the cross-org case, and the scan-driven composite-FK suite can
-- observe it. Together the two refuse every non-NULL pointer on insert.
create or replace function public.guard_run_evidence_pointer_insert() returns trigger
language plpgsql as $$
begin
  if exists (
    select 1 from public.evidence_revision er
     where er.org_id = new.org_id and er.id = new.current_evidence_revision_id) then
    raise exception 'run.current_evidence_revision_id must be null on insert (run %)', new.id
      using errcode = '23514';
  end if;
  return new;
end;
$$;

drop trigger if exists run_evidence_pointer_insert_guard on public.run;
create trigger run_evidence_pointer_insert_guard
  before insert on public.run
  for each row
  when (new.current_evidence_revision_id is not null)
  execute function public.guard_run_evidence_pointer_insert();

-- EXECUTE (0001 revokes the built-in PUBLIC EXECUTE on functions evidgate_owner creates): the app role
-- fires all three triggers. No grant to operator or worker roles.
grant execute on function public.guard_evidence_revision_insert() to evidgate_app;
grant execute on function public.guard_run_evidence_pointer() to evidgate_app;
grant execute on function public.guard_run_evidence_pointer_insert() to evidgate_app;

-- The one new run column the app role may write besides `state` (0036) and `updated_at` (0034).
grant update (current_evidence_revision_id) on public.run to evidgate_app;
