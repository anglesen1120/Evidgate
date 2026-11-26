-- 0130_policy_deploy_target.sql
--
-- `deploy_target`: a named, ordered release destination of one project (`staging`, `prod`)
-- that the promotion gate will evaluate a release against. A target may pin one policy revision of its
-- own project; without a pin the project's active revision applies at check time.
--
-- Soft delete: a target is never removed. `DELETE` on the API moves `active -> disabled`, `PATCH` may move it back.
-- The app role holds no DELETE grant and the guard trigger refuses any DELETE, so later rows that reference a target
-- can always reach it. The unique name index spans both states, so a disabled target keeps its name until it is renamed.
--
-- Pin integrity: `policy_revision` only had `unique (org_id, id)`. The composite key `(org_id, project_id, id)` added
-- below lets the pin's FK name the target's own project, so a revision of another project (or tenant) cannot be
-- pinned even by a bug in the API. The FK is MATCH SIMPLE: a null pin is not checked.
--
-- Column grants (0030 precedent): the app role may INSERT only the request columns and UPDATE only the four mutable
-- ones; `state` therefore starts at its default `active`, and `version`/`updated_at` belong to the 0004 trigger.
--
-- Idempotent like 0025/0030: re-running is a no-op.

-- Not dropped first: on a re-run the FK below already depends on this key.
-- The unique index build holds ACCESS EXCLUSIVE on policy_revision until this migration commits; the table is
-- append-only and small, so the pause is short, but deploy it like any other blocking migration.
do $$
begin
  if not exists (select 1 from pg_constraint where conname = 'policy_revision_org_project_id_key' and conrelid = 'public.policy_revision'::regclass) then
    alter table public.policy_revision
      add constraint policy_revision_org_project_id_key unique (org_id, project_id, id);
  end if;
end
$$;

create table if not exists public.deploy_target (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  name text not null,
  position integer not null default 0,
  policy_revision_id uuid,
  state text not null default 'active',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint deploy_target_pkey primary key (id),
  constraint deploy_target_org_id_key unique (org_id, id),
  constraint deploy_target_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint deploy_target_policy_revision_fk foreign key (org_id, project_id, policy_revision_id)
    references public.policy_revision (org_id, project_id, id),
  constraint deploy_target_name_check check (name ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$'),
  constraint deploy_target_position_check check (position between 0 and 10000),
  constraint deploy_target_state_check check (state in ('active', 'disabled')),
  constraint deploy_target_version_check check (version >= 1)
);

-- One name per project, compared case-insensitively and across both states: `Prod` and `prod` are one target.
create unique index if not exists deploy_target_name_key
  on public.deploy_target (org_id, project_id, lower(name));

-- The list walks one project in display order.
create index if not exists deploy_target_project_position_idx
  on public.deploy_target (org_id, project_id, position, id);

drop trigger if exists deploy_target_set_updated_at_and_version on public.deploy_target;
create trigger deploy_target_set_updated_at_and_version
  before update on public.deploy_target
  for each row execute function public.set_updated_at_and_version();

create or replace function evidgate.deploy_target_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'deploy_target_immutable: a deploy target is disabled, never deleted'
        using errcode = '23514';
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.created_at is distinct from old.created_at then
      raise exception 'deploy_target_immutable: a deploy target identity and project never change (UPDATE)'
        using errcode = '23514';
    end if;

    if new.state is distinct from old.state
       and not ((old.state = 'active' and new.state = 'disabled')
                or (old.state = 'disabled' and new.state = 'active')) then
      raise exception 'deploy_target_state_invalid: % -> %', old.state, new.state
        using errcode = '23514';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.deploy_target_guard() from public;

drop trigger if exists deploy_target_guard on public.deploy_target;
create trigger deploy_target_guard
  before update or delete on public.deploy_target
  for each row execute function evidgate.deploy_target_guard();

alter table public.deploy_target enable row level security;
alter table public.deploy_target force row level security;

drop policy if exists deploy_target_app_tenant on public.deploy_target;
create policy deploy_target_app_tenant on public.deploy_target
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists deploy_target_owner_backfill on public.deploy_target;
create policy deploy_target_owner_backfill on public.deploy_target
  for all to evidgate_owner using (true) with check (true);

revoke insert, update on public.deploy_target from evidgate_app;
grant select on public.deploy_target to evidgate_app;
grant insert (id, org_id, project_id, name, position, policy_revision_id) on public.deploy_target to evidgate_app;
grant update (name, position, policy_revision_id, state) on public.deploy_target to evidgate_app;
revoke delete on public.deploy_target from evidgate_app;
-- No TO evidgate_operator policy on purpose: with FORCE RLS the operator reads no tenant rows (same posture as 0030).
grant select on public.deploy_target to evidgate_operator;
revoke all on public.deploy_target from evidgate_worker_sched;
