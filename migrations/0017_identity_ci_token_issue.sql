-- 0017_identity_ci_token_issue.sql
--
-- Owner: 02.4.1-T3.1. `ci_token_issue`: one row per `ci_producer` token minted from a GitHub
-- OIDC exchange. `unique (jti)` is the replay arbiter (FR-02-6): the same OIDC JWT presented
-- twice hits the constraint and the second exchange is 401 `token_replayed`. The T88 claims
-- (provider, repository_id, run_id, run_attempt) are recorded verbatim; 05.2.1 copies them into
-- `run.ci_run_ref`. Append-only replay cache: rows are purged once the OIDC token's lifetime
-- plus 10 minutes has passed (T7), by the daily `ci_token_issue.purge` task.

create table if not exists public.ci_token_issue (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  binding_id uuid not null,
  provider text not null,
  jti text not null,
  repository_id bigint not null,
  repository_full_name text not null,
  ref text not null,
  sha text not null,
  run_id bigint not null,
  run_attempt integer not null,
  job_workflow_ref text not null,
  expires_at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint ci_token_issue_pkey primary key (id),
  constraint ci_token_issue_org_id_key unique (org_id, id),
  constraint ci_token_issue_jti_key unique (jti),
  constraint ci_token_issue_binding_fk foreign key (org_id, binding_id) references public.ci_binding (org_id, id),
  constraint ci_token_issue_jti_length_check check (char_length(jti) between 1 and 256)
);

create index if not exists ci_token_issue_expires_idx on public.ci_token_issue (expires_at);

drop trigger if exists ci_token_issue_set_updated_at_and_version on public.ci_token_issue;
create trigger ci_token_issue_set_updated_at_and_version
  before update on public.ci_token_issue
  for each row
  execute function public.set_updated_at_and_version();

alter table public.ci_token_issue enable row level security;
alter table public.ci_token_issue force row level security;

drop policy if exists ci_token_issue_app_tenant on public.ci_token_issue;
create policy ci_token_issue_app_tenant on public.ci_token_issue
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists ci_token_issue_owner_backfill on public.ci_token_issue;
create policy ci_token_issue_owner_backfill on public.ci_token_issue
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- Insert-only for the app role: a minted-token record is never edited or deleted by a request.
grant select, insert on public.ci_token_issue to evidgate_app;
revoke update, delete on public.ci_token_issue from evidgate_app;
grant select on public.ci_token_issue to evidgate_operator;
revoke all on public.ci_token_issue from evidgate_worker_sched;

-- The T7 purge: bounded batches of rows whose replay window has closed, run by the sweep role.
create or replace function evidgate.purge_expired_ci_token_issue(p_limit integer) returns integer
  language sql
  security definer
  set search_path = ''
  as $$
    with batch as (
      select t.id
        from public.ci_token_issue t
       where t.expires_at + interval '10 minutes' < now()
       order by t.expires_at
       limit least(greatest(p_limit, 0), 10000)
         for update skip locked
    ), removed as (
      delete from public.ci_token_issue t
       using batch b
       where t.id = b.id
      returning t.id
    )
    select count(*)::integer from removed
  $$;

revoke all on function evidgate.purge_expired_ci_token_issue(integer) from public;
grant execute on function evidgate.purge_expired_ci_token_issue(integer) to evidgate_worker_sched;
