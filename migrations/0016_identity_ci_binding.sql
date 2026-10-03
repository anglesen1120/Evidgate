-- 0016_identity_ci_binding.sql
--
-- Owner: 02.4.1-T2.1. `ci_binding` (feature plan 02.4.1 §4, 00 §8.9): which repository of
-- which CI provider may mint `ci_producer` tokens for which project, and from which refs
-- and workflow files. Trust follows the immutable `repository_id`, never the repository
-- name or the `sub` claim (DECISIONS F3, D4).
--
-- Proof of repository control (TL T70): a binding is created `pending_verification` with a
-- one-time random challenge, stored only as HMAC-SHA256(pepper, challenge). It becomes
-- `active` when an OIDC exchange from that repository presents the challenge (the repo
-- variable EVIDGATE_BINDING_CHALLENGE needs repo write access to set). The partial unique
-- index on (provider, repository_id) WHERE status = 'active' means at most one org holds
-- the active binding of a repository platform-wide, so the resolver returns <= 1 row.
--
-- Tenant table of 00 §5.4.1: org_id, unique (org_id, id), composite FK to the project,
-- FORCE ROW LEVEL SECURITY with the standard policy, the shared version trigger of 0004.

create table if not exists public.ci_binding (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  provider text not null,
  repository_id bigint not null,
  repository_full_name text not null,
  allowed_ref_patterns text[] not null,
  allowed_workflow_refs text[] not null,
  status text not null default 'pending_verification',
  binding_challenge_hash bytea,
  challenge_pepper_version integer,
  challenge_issued_at timestamptz,
  activated_at timestamptz,
  created_by uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint ci_binding_pkey primary key (id),
  constraint ci_binding_org_id_key unique (org_id, id),
  constraint ci_binding_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint ci_binding_provider_check check (provider in ('github_actions')),
  constraint ci_binding_status_check check (status in ('pending_verification', 'active', 'disabled')),
  constraint ci_binding_repository_id_check check (repository_id > 0),
  constraint ci_binding_name_length_check check (char_length(repository_full_name) between 3 and 200),
  constraint ci_binding_ref_patterns_check check (
    cardinality(allowed_ref_patterns) between 1 and 32),
  constraint ci_binding_workflow_refs_check check (
    cardinality(allowed_workflow_refs) between 1 and 32),
  constraint ci_binding_hash_length_check check (
    binding_challenge_hash is null or octet_length(binding_challenge_hash) = 32)
);

-- One binding per (project, repository): a duplicate create is 409 binding_exists.
create unique index if not exists ci_binding_project_repository_key
  on public.ci_binding (org_id, project_id, provider, repository_id);

-- T70: at most one org holds the ACTIVE binding of a repository, platform-wide.
create unique index if not exists ci_binding_active_repository_key
  on public.ci_binding (provider, repository_id)
  where status = 'active';

-- The resolver's lookup, and the project's binding list.
create index if not exists ci_binding_repository_idx
  on public.ci_binding (provider, repository_id);

create index if not exists ci_binding_org_project_created_idx
  on public.ci_binding (org_id, project_id, created_at, id);

drop trigger if exists ci_binding_set_updated_at_and_version on public.ci_binding;
create trigger ci_binding_set_updated_at_and_version
  before update on public.ci_binding
  for each row
  execute function public.set_updated_at_and_version();

alter table public.ci_binding enable row level security;
alter table public.ci_binding force row level security;

drop policy if exists ci_binding_app_tenant on public.ci_binding;
create policy ci_binding_app_tenant on public.ci_binding
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists ci_binding_owner_backfill on public.ci_binding;
create policy ci_binding_owner_backfill on public.ci_binding
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- Disable, not delete (§6): the app role reads, creates and edits bindings.
grant select, insert, update on public.ci_binding to evidgate_app;
revoke delete on public.ci_binding from evidgate_app;
grant select on public.ci_binding to evidgate_operator;
revoke all on public.ci_binding from evidgate_worker_sched;

-- lint: audit-deferred 02.7.1-T2.2 evidgate_resolve.resolve_ci_binding
--
-- The pre-tenant resolver of `POST /v1/ci/token` (00 §5.4.8, TL T1, T70). It returns the
-- tenant tuple and the binding id/status, never a column of the table. Priority: the single
-- ACTIVE binding of the repository; otherwise the PENDING binding whose challenge hash is one
-- of `p_challenge_hashes` (the HMAC of the presented challenge under each live pepper); otherwise
-- the oldest DISABLED one (so the endpoint can say `binding_disabled`). No row = unbound.
-- The third parameter is additive to the plan's two-argument signature: several orgs may hold
-- a pending binding for one repository, and only the challenge says which one is meant.
create or replace function evidgate_resolve.resolve_ci_binding(
  p_provider text,
  p_repository_id bigint,
  p_challenge_hashes bytea[] default null)
returns table (org_id uuid, project_id uuid, binding_id uuid, status text)
language sql
stable
security definer
set search_path = ''
as $$
  with hit as (
    select b.org_id, b.project_id, b.id as binding_id, b.status
      from public.ci_binding b
     where b.provider = p_provider
       and b.repository_id = p_repository_id
       and (b.status in ('active', 'disabled')
            or (b.status = 'pending_verification'
                and p_challenge_hashes is not null
                and b.binding_challenge_hash = any (p_challenge_hashes)))
     order by case b.status when 'active' then 0 when 'pending_verification' then 1 else 2 end,
              b.created_at, b.id
     limit 1
  )
  select h.org_id, h.project_id, h.binding_id, h.status from hit h
  union all
  -- A binding is waiting for its challenge but none was proven: say so without naming a tenant.
  select null::uuid, null::uuid, null::uuid, 'pending_unproven'::text
   where not exists (select 1 from hit)
     and exists (select 1 from public.ci_binding p
                  where p.provider = p_provider
                    and p.repository_id = p_repository_id
                    and p.status = 'pending_verification')
$$;

revoke all on function evidgate_resolve.resolve_ci_binding(text, bigint, bytea[]) from public;
grant execute on function evidgate_resolve.resolve_ci_binding(text, bigint, bytea[]) to evidgate_app;
