set local lock_timeout = '5s';

create table if not exists public.draft (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  author_membership_id uuid not null,
  kind text not null,
  base_commit text not null,
  status text not null default 'draft',
  version integer not null default 1,
  content jsonb not null,
  provenance jsonb,
  -- These targets land with the AI and dry-run stores; no target table exists yet.
  ai_run_id uuid,
  dry_run_id uuid,
  allow_unverified boolean not null default false,
  unverified_reason text,
  pr_branch text,
  pr_commit_sha text,
  pr_url text,
  pr_number integer,
  linked_case_id uuid,
  linked_revision_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint draft_pkey primary key (id),
  constraint draft_org_id_key unique (org_id, id),
  constraint draft_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint draft_author_fk foreign key (org_id, author_membership_id) references public.membership (org_id, id),
  constraint draft_linked_case_fk foreign key (org_id, linked_case_id) references public.test_case (org_id, id),
  constraint draft_linked_revision_fk foreign key (org_id, linked_revision_id) references public.test_revision (org_id, id),
  constraint draft_kind_check check (kind in ('template', 'ai', 'regression')),
  constraint draft_base_commit_check check (base_commit ~ '^[0-9a-f]{7,64}$'),
  constraint draft_status_check check (status in ('draft', 'dry_running', 'dry_passed', 'dry_failed', 'pr_open', 'merged', 'closed')),
  constraint draft_version_check check (version >= 1),
  constraint draft_content_check check (jsonb_typeof(content) = 'object' and octet_length(content::text) <= 262144),
  constraint draft_provenance_check check (provenance is null or (jsonb_typeof(provenance) = 'object' and octet_length(provenance::text) <= 4096)),
  constraint draft_unverified_reason_check check (unverified_reason is null or char_length(unverified_reason) <= 4096),
  constraint draft_pr_branch_check check (pr_branch is null or char_length(pr_branch) between 1 and 255),
  constraint draft_pr_commit_sha_check check (pr_commit_sha is null or pr_commit_sha ~ '^[0-9a-f]{7,64}$'),
  constraint draft_pr_url_check check (pr_url is null or char_length(pr_url) between 1 and 2048),
  constraint draft_pr_number_check check (pr_number is null or pr_number >= 1)
);

create index if not exists draft_project_list on public.draft (org_id, project_id, created_at, id);
create index if not exists draft_status on public.draft (org_id, project_id, status);
create index if not exists draft_author on public.draft (org_id, author_membership_id);
create index if not exists draft_linked_case on public.draft (org_id, linked_case_id);
create index if not exists draft_linked_revision on public.draft (org_id, linked_revision_id);

create or replace function public.draft_guard() returns trigger
  language plpgsql
  set search_path = ''
as $$
begin
  if (new.id, new.org_id, new.project_id, new.author_membership_id, new.kind, new.base_commit, new.created_at)
     is distinct from
     (old.id, old.org_id, old.project_id, old.author_membership_id, old.kind, old.base_commit, old.created_at) then
    raise exception 'draft identity is immutable' using errcode = '23514';
  end if;

  if new.status is distinct from old.status and not (
    (old.status = 'draft' and new.status = 'dry_running') or
    (old.status = 'dry_running' and new.status in ('dry_passed', 'dry_failed')) or
    (old.status = 'dry_passed' and new.status = 'pr_open') or
    (old.status = 'dry_failed' and new.status in ('dry_running', 'pr_open')) or
    (old.status = 'pr_open' and new.status in ('draft', 'merged', 'closed'))
  ) then
    raise exception 'draft cannot move from % to %', old.status, new.status using errcode = '23514';
  end if;

  if new.content is distinct from old.content and old.status <> 'draft' then
    raise exception 'draft content is editable only in draft state' using errcode = '23514';
  end if;
  return new;
end
$$;

revoke all on function public.draft_guard() from public;
drop trigger if exists draft_guard on public.draft;
create trigger draft_guard before update on public.draft
  for each row execute function public.draft_guard();
drop trigger if exists draft_set_updated_at_and_version on public.draft;
create trigger draft_set_updated_at_and_version before update on public.draft
  for each row execute function public.set_updated_at_and_version();

alter table public.draft enable row level security;
alter table public.draft force row level security;
drop policy if exists draft_app_tenant on public.draft;
create policy draft_app_tenant on public.draft for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists draft_owner_backfill on public.draft;
create policy draft_owner_backfill on public.draft for all to evidgate_owner
  using (true) with check (true);

-- Draft bodies are tenant-only authoring material, including for operator sessions.
revoke all on public.draft from public, evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator, evidgate_operator;
grant select, insert on public.draft to evidgate_app;
grant update (content, version, updated_at, status, dry_run_id, allow_unverified, unverified_reason,
              pr_branch, pr_commit_sha, pr_url, pr_number, linked_case_id, linked_revision_id)
  on public.draft to evidgate_app;
