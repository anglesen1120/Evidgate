-- 0129_policy_release_decision.sql
--
-- WHY: a release is a set of digest-pinned components, and "may this release ship" is the aggregate of the decisions
-- of the runs bound to those components. The aggregate has to outlive the task that computed it and has to be
-- readable without recomputing, so it is stored: one row per distinct set of inputs. A re-aggregation of the same
-- inputs hashes to the same `inputs_hash` and collides with the stored row; a changed input set (a new bound run, a
-- new verdict) is a new row, and the newest row by `created_at` is the release's current decision. Nothing is
-- rewritten, like `evaluation`.
--
-- WHAT: the table, its keys and CHECKs, the "latest decision of this release" index, the immutability trigger, row
-- level security and the role grants. `component_evaluations` is a jsonb array of per-component facts that is only
-- ever read whole (never filtered by a predicate), so it stays a document. `completeness` and `decision` use the
-- evaluation vocabulary; `PASS` is only stored over `complete`, enforced here so a handler bug cannot store a pass
-- over a missing component.
--
-- REFERENCES: composite on `org_id` to `release` and `policy_revision`, so a row cannot name another tenant's rows
-- even if a bug let the policy pass. No cascade: a release with a stored decision is never erased by a cascade.
--
-- ORDER: additive, migrate before the worker that writes rows. No down migration: dropping the table would delete
-- every stored release decision.
--
-- LOCKS: `create table` and `create index` on a new empty table; `lock_timeout` 5 s for the `release` and
-- `policy_revision` share locks the foreign keys take.

set local lock_timeout = '5s';

create table if not exists public.release_decision (
  id uuid not null,
  org_id uuid not null,
  release_id uuid not null,
  policy_revision_id uuid not null,
  component_evaluations jsonb not null,
  completeness text not null,
  decision text not null,
  reasons text[] not null default '{}',
  inputs_hash text not null,
  created_at timestamptz not null default now(),
  constraint release_decision_pkey primary key (id),
  constraint release_decision_org_id_key unique (org_id, id),
  -- The dedupe key: the same inputs under the same policy revision are one stored row.
  constraint release_decision_inputs_key unique (org_id, release_id, policy_revision_id, inputs_hash),
  constraint release_decision_release_fk foreign key (org_id, release_id)
    references public.release (org_id, id),
  constraint release_decision_policy_revision_fk foreign key (org_id, policy_revision_id)
    references public.policy_revision (org_id, id),
  constraint release_decision_completeness_check check (completeness in ('complete', 'incomplete')),
  constraint release_decision_decision_check check (decision in ('PASS', 'FAIL', 'INCONCLUSIVE')),
  constraint release_decision_pass_requires_complete_check
    check (decision <> 'PASS' or completeness = 'complete'),
  constraint release_decision_inputs_hash_check check (inputs_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint release_decision_components_check
    check (jsonb_typeof(component_evaluations) = 'array' and jsonb_array_length(component_evaluations) <= 200),
  constraint release_decision_reasons_check check (
    case
      when array_ndims(reasons) is null or array_ndims(reasons) = 1 then
        reasons <@ array['component_digest_mismatch', 'component_run_unbound', 'component_not_evaluated',
                         'component_failed', 'component_inconclusive', 'mixed_policy_revisions',
                         'aggregation_bound_exceeded']::text[]
        and array_position(reasons, null) is null
      else false
    end)
);

-- "The latest decision of this release" streams one row from this index: no sort, no scan of the history.
create index if not exists release_decision_release_created_idx
  on public.release_decision (org_id, release_id, created_at desc, id desc);

-- The aggregation reads one release's bindings in (created_at, id) order under a cap; the binding key ends in
-- (run_id, component_key) and the run index leads with run_id, so neither can serve that order without a sort.
create index if not exists release_run_release_created_idx
  on public.release_run (org_id, release_id, created_at, id);

-- A stored aggregate is evidence: nothing about it may change after the INSERT, for any role. The grants below stop
-- `evidgate_app` first; the trigger is the backstop for the owner and for any future grant.
create or replace function evidgate.release_decision_immutable() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'release_decision_immutable: a stored release decision is never deleted (DELETE)'
        using errcode = '23514';
    end if;

    raise exception 'release_decision_immutable: a stored release decision is never rewritten (UPDATE)'
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.release_decision_immutable() from public;

drop trigger if exists release_decision_immutable on public.release_decision;
create trigger release_decision_immutable
  before update or delete on public.release_decision
  for each row execute function evidgate.release_decision_immutable();

alter table public.release_decision enable row level security;
alter table public.release_decision force row level security;

drop policy if exists release_decision_app_tenant on public.release_decision;
create policy release_decision_app_tenant on public.release_decision
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- `force row level security` binds the owner too, so the migration role needs its own permissive policy (0059).
drop policy if exists release_decision_owner_backfill on public.release_decision;
create policy release_decision_owner_backfill on public.release_decision
  for all to evidgate_owner using (true) with check (true);

-- 0001's default privileges hand `evidgate_app` full DML on every new table, so the revoke comes first. The app role
-- writes a row once and reads it; the operator keeps read only; the scheduler holds nothing.
revoke insert, update, delete on public.release_decision from evidgate_app;
grant select, insert on public.release_decision to evidgate_app;
grant select on public.release_decision to evidgate_operator;
revoke all on public.release_decision from evidgate_worker_sched;
