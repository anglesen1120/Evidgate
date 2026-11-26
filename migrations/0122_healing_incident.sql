set local lock_timeout = '5s';

create table if not exists public.incident (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  run_id uuid not null,
  attempt_id uuid not null,
  case_id uuid not null,
  case_revision_id uuid not null,
  execution_key text not null,
  engine text not null,
  state text not null default 'open',
  context_state text not null default 'pending',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint incident_pkey primary key (id),
  constraint incident_org_id_key unique (org_id, id),
  constraint incident_run_execution_key unique (org_id, run_id, execution_key),
  constraint incident_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint incident_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint incident_case_fk foreign key (org_id, case_id) references public.test_case (org_id, id),
  constraint incident_case_revision_fk foreign key (org_id, case_revision_id) references public.test_revision (org_id, id),
  constraint incident_attempt_fk foreign key (org_id, attempt_id) references public.case_attempt (org_id, id),
  constraint incident_execution_key_check check (execution_key ~ '^[0-9a-f]{64}$'),
  constraint incident_engine_check check (char_length(engine) between 1 and 64),
  constraint incident_state_check check (state in ('open', 'triaged', 'closed')),
  constraint incident_context_state_check check (context_state in ('pending', 'complete', 'partial')),
  constraint incident_version_check check (version >= 1)
);

-- The keyset page reads one project's incidents newest first, and a state-filtered page reads the second index;
-- both match the page order so neither sorts in memory.
create index if not exists incident_project_list on public.incident (org_id, project_id, created_at desc, id);
create index if not exists incident_project_state on public.incident (org_id, project_id, state);

create table if not exists public.incident_attempt (
  id uuid not null,
  org_id uuid not null,
  incident_id uuid not null,
  attempt_id uuid not null,
  attempt_no integer not null,
  result text not null,
  created_at timestamptz not null default now(),
  constraint incident_attempt_pkey primary key (id),
  constraint incident_attempt_org_id_key unique (org_id, id),
  constraint incident_attempt_incident_attempt_key unique (org_id, incident_id, attempt_id),
  constraint incident_attempt_incident_fk foreign key (org_id, incident_id) references public.incident (org_id, id),
  constraint incident_attempt_attempt_fk foreign key (org_id, attempt_id) references public.case_attempt (org_id, id),
  constraint incident_attempt_attempt_no_check check (attempt_no >= 1),
  constraint incident_attempt_result_check check (result in ('passed', 'failed', 'skipped', 'timed_out', 'error', 'blocked'))
);

create or replace function public.incident_guard() returns trigger
  language plpgsql
  set search_path = ''
as $$
begin
  -- The update guard cannot see an insert, so a caller that skips the recorder must not file an incident past
  -- its initial state or version. This runs before the CHECKs, so a bad insert answers this message.
  if tg_op = 'INSERT' then
    if new.state <> 'open' or new.context_state <> 'pending' or new.version <> 1 then
      raise exception 'incident must start open and pending at version 1' using errcode = '23514';
    end if;
    return new;
  end if;

  if (new.id, new.org_id, new.project_id, new.run_id, new.attempt_id, new.case_id, new.case_revision_id,
      new.execution_key, new.engine, new.created_at)
     is distinct from
     (old.id, old.org_id, old.project_id, old.run_id, old.attempt_id, old.case_id, old.case_revision_id,
      old.execution_key, old.engine, old.created_at) then
    raise exception 'incident linkage is immutable' using errcode = '23514';
  end if;

  if new.state is distinct from old.state and not (
    (old.state = 'open' and new.state = 'triaged') or
    (old.state = 'triaged' and new.state = 'closed')
  ) then
    raise exception 'incident cannot move from % to %', old.state, new.state using errcode = '23514';
  end if;

  if new.context_state is distinct from old.context_state and not (
    old.context_state = 'pending' and new.context_state in ('complete', 'partial')
  ) then
    raise exception 'incident context cannot move from % to %', old.context_state, new.context_state
      using errcode = '23514';
  end if;
  return new;
end
$$;

revoke all on function public.incident_guard() from public;
drop trigger if exists incident_guard on public.incident;
create trigger incident_guard before insert or update on public.incident
  for each row execute function public.incident_guard();
drop trigger if exists incident_set_updated_at_and_version on public.incident;
create trigger incident_set_updated_at_and_version before update on public.incident
  for each row execute function public.set_updated_at_and_version();

create or replace function public.incident_attempt_append_only() returns trigger
  language plpgsql
  set search_path = ''
as $$
begin
  raise exception 'incident_attempt is append-only: % is refused', tg_op using errcode = '23514';
end
$$;

revoke all on function public.incident_attempt_append_only() from public;
drop trigger if exists incident_attempt_append_only on public.incident_attempt;
create trigger incident_attempt_append_only before update or delete on public.incident_attempt
  for each row execute function public.incident_attempt_append_only();

alter table public.incident enable row level security;
alter table public.incident force row level security;
drop policy if exists incident_app_tenant on public.incident;
create policy incident_app_tenant on public.incident for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists incident_owner_backfill on public.incident;
create policy incident_owner_backfill on public.incident for all to evidgate_owner
  using (true) with check (true);

alter table public.incident_attempt enable row level security;
alter table public.incident_attempt force row level security;
drop policy if exists incident_attempt_app_tenant on public.incident_attempt;
create policy incident_attempt_app_tenant on public.incident_attempt for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists incident_attempt_owner_backfill on public.incident_attempt;
create policy incident_attempt_owner_backfill on public.incident_attempt for all to evidgate_owner
  using (true) with check (true);

-- Incidents carry failure evidence, so no operator or worker holds a standing grant: the recorder and the triage
-- routes run as the application role inside a tenant scope.
revoke all on public.incident, public.incident_attempt
  from public, evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator, evidgate_operator;
grant select, insert on public.incident to evidgate_app;
grant update (state, context_state, version, updated_at) on public.incident to evidgate_app;
grant select, insert on public.incident_attempt to evidgate_app;
