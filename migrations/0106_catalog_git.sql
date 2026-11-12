-- The commit-registration tables: one repository binding per project, the webhook delivery ledger, and the
-- sync queue. A binding names the installation, the repository and a secret-store *reference* — never the
-- secret — and `sync_state` follows installation events only. A repository is bound once across the whole
-- installation, so the tenant-less resolver has exactly one binding to answer with. Deliveries are deduped
-- by the global (provider, delivery_id) key, because GitHub re-delivers the same id against the same
-- repository. A sync row is idempotent by (binding, commit) and moves queued -> running -> done | failed,
-- with a failed row replayed back to queued by the dead-letter route; a done row never changes again.
--
-- GRANTS: `evidgate_app` holds SELECT, INSERT and a column-scoped UPDATE only. The binding's identity, the
-- delivery's payload identity and a sync's commit are written once; the mutable columns are the ones the
-- webhook, the binding command and the sync handler own. `evidgate_worker` and `evidgate_worker_sched` hold
-- nothing: the queue machinery never reads tenant catalog data, and the handlers run inside a tenant
-- transaction as `evidgate_app` (0104's split). `evidgate_operator` keeps its 0001 SELECT grant and has no
-- policy, exactly like the other tenant tables, so a tenant read of these rows stays the app role's path.

set local lock_timeout = '5s';

create table if not exists public.git_repo_binding (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  provider text not null default 'github',
  installation_id text not null,
  repository_id text not null,
  default_branch text not null default 'main',
  head_commit text,
  last_synced_commit text,
  sync_state text not null default 'active',
  webhook_secret_ref text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint git_repo_binding_pkey primary key (id),
  constraint git_repo_binding_org_id_key unique (org_id, id),
  -- The target of the sync row's three-column FK: a sync names a binding of its own project, never a sibling's.
  constraint git_repo_binding_project_id_key unique (org_id, project_id, id),
  constraint git_repo_binding_repository_key unique (org_id, project_id, provider, repository_id),
  -- Global on purpose: the tenant-less resolver matches installation + repository and must find one binding.
  constraint git_repo_binding_global_repository_key unique (provider, installation_id, repository_id),
  constraint git_repo_binding_project_fk
    foreign key (org_id, project_id) references public.project (org_id, id),
  constraint git_repo_binding_provider_check check (provider in ('github')),
  constraint git_repo_binding_installation_id_check check (char_length(installation_id) between 1 and 64),
  constraint git_repo_binding_repository_id_check check (char_length(repository_id) between 1 and 64),
  constraint git_repo_binding_default_branch_check check (char_length(default_branch) between 1 and 255),
  constraint git_repo_binding_head_commit_check check (head_commit is null or head_commit ~ '^[0-9a-f]{7,64}$'),
  constraint git_repo_binding_last_synced_commit_check
    check (last_synced_commit is null or last_synced_commit ~ '^[0-9a-f]{7,64}$'),
  constraint git_repo_binding_sync_state_check check (sync_state in ('active', 'disabled')),
  -- A reference to the secret store (11.3.1's `secret:<name>` form): the value itself must never land here.
  constraint git_repo_binding_webhook_secret_ref_check
    check (webhook_secret_ref ~ '^secret:[A-Za-z0-9_.-]{1,64}$'),
  constraint git_repo_binding_version_check check (version >= 1)
);

create table if not exists public.webhook_delivery (
  id uuid not null,
  org_id uuid not null,
  provider text not null,
  delivery_id text not null,
  event text not null,
  payload_hash text not null,
  state text not null default 'accepted',
  created_at timestamptz not null default now(),
  constraint webhook_delivery_pkey primary key (id),
  constraint webhook_delivery_org_id_key unique (org_id, id),
  -- Global on purpose: the dedupe must catch a delivery even when it is re-issued against the same repo.
  constraint webhook_delivery_identity_key unique (provider, delivery_id),
  constraint webhook_delivery_provider_check check (provider in ('github')),
  constraint webhook_delivery_delivery_id_check check (char_length(delivery_id) between 1 and 200),
  constraint webhook_delivery_event_check check (char_length(event) between 1 and 100),
  constraint webhook_delivery_payload_hash_check check (payload_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint webhook_delivery_state_check check (state in ('accepted', 'duplicate', 'processed'))
);

create table if not exists public.catalog_sync (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  commit_sha text not null,
  binding_id uuid not null,
  state text not null default 'queued',
  counts jsonb,
  error text,
  started_at timestamptz,
  finished_at timestamptz,
  created_at timestamptz not null default now(),
  constraint catalog_sync_pkey primary key (id),
  constraint catalog_sync_org_id_key unique (org_id, id),
  constraint catalog_sync_commit_key unique (org_id, binding_id, commit_sha),
  constraint catalog_sync_project_fk
    foreign key (org_id, project_id) references public.project (org_id, id),
  constraint catalog_sync_binding_fk
    foreign key (org_id, project_id, binding_id) references public.git_repo_binding (org_id, project_id, id),
  constraint catalog_sync_state_check check (state in ('queued', 'running', 'done', 'failed')),
  constraint catalog_sync_commit_sha_check check (commit_sha ~ '^[0-9a-f]{7,64}$'),
  constraint catalog_sync_counts_check
    check (counts is null or (jsonb_typeof(counts) = 'object' and octet_length(counts::text) <= 8192)),
  constraint catalog_sync_error_check check (error is null or char_length(error) <= 4096)
);

-- The sync list (newest first), and one index per child side: `catalog_sync_list` covers the project FK and
-- `catalog_sync_binding` covers the (org_id, project_id, binding_id) FK, whose leading pair is not a prefix of
-- the list. The parent-side lookups are served by `project_pkey`/`project_org_id_key` and the binding's keys.
create index if not exists catalog_sync_list
  on public.catalog_sync (org_id, project_id, created_at desc, id);

create index if not exists catalog_sync_binding
  on public.catalog_sync (org_id, project_id, binding_id);

-- WHY: the binding's repository identity is written once by the binding command, and only the four event
-- columns move afterwards. A later rewrite of `repository_id` or `webhook_secret_ref` would silently point
-- the installation at another repo or another secret with nothing re-verifying it (S3/S4).
create or replace function public.git_repo_binding_guard() returns trigger
  language plpgsql
  set search_path = ''
as $$
begin
  if (to_jsonb(new) - array['sync_state', 'head_commit', 'last_synced_commit', 'default_branch', 'updated_at', 'version'])
     is distinct from
     (to_jsonb(old) - array['sync_state', 'head_commit', 'last_synced_commit', 'default_branch', 'updated_at', 'version']) then
    raise exception 'git_repo_binding % carries its identity for life', old.id
      using errcode = '23514';
  end if;

  if new.sync_state is distinct from old.sync_state
     and not ((old.sync_state = 'active' and new.sync_state = 'disabled')
              or (old.sync_state = 'disabled' and new.sync_state = 'active')) then
    raise exception 'git_repo_binding % cannot move from % to %', old.id, old.sync_state, new.sync_state
      using errcode = '23514';
  end if;

  return new;
end
$$;

-- WHY: a delivery row is the record of one received payload. Only the informational state moves, forward,
-- once; a rewritten payload hash would let two different bodies share one delivery identity.
create or replace function public.webhook_delivery_guard() returns trigger
  language plpgsql
  set search_path = ''
as $$
begin
  if (to_jsonb(new) - 'state') is distinct from (to_jsonb(old) - 'state') then
    raise exception 'webhook_delivery % is a received payload; only its state moves', old.id
      using errcode = '23514';
  end if;

  if new.state is distinct from old.state
     and not (old.state = 'accepted' and new.state in ('duplicate', 'processed')) then
    raise exception 'webhook_delivery % cannot move from % to %', old.id, old.state, new.state
      using errcode = '23514';
  end if;

  return new;
end
$$;

-- WHY: the sync row is the queue entry; `done` is final (a re-run creates nothing — the commit key answers)
-- and `failed -> queued` is the dead-letter replay. The applied content (`counts`) is free to change while
-- the row is not done, so the guard freezes the identity and the terminal row only.
create or replace function public.catalog_sync_guard() returns trigger
  language plpgsql
  set search_path = ''
as $$
begin
  if old.state = 'done' and new is distinct from old then
    raise exception 'catalog_sync % is done and never changes', old.id
      using errcode = '23514';
  end if;

  if (to_jsonb(new) - array['state', 'counts', 'error', 'started_at', 'finished_at'])
     is distinct from
     (to_jsonb(old) - array['state', 'counts', 'error', 'started_at', 'finished_at']) then
    raise exception 'catalog_sync % carries its commit and binding for life', old.id
      using errcode = '23514';
  end if;

  if new.state is distinct from old.state
     and not ((old.state = 'queued' and new.state = 'running')
              or (old.state = 'running' and new.state in ('done', 'failed'))
              or (old.state = 'failed' and new.state = 'queued')) then
    raise exception 'catalog_sync % cannot move from % to %', old.id, old.state, new.state
      using errcode = '23514';
  end if;

  return new;
end
$$;

revoke all on function public.git_repo_binding_guard() from public;
revoke all on function public.webhook_delivery_guard() from public;
revoke all on function public.catalog_sync_guard() from public;

drop trigger if exists git_repo_binding_guard on public.git_repo_binding;
create trigger git_repo_binding_guard
  before update on public.git_repo_binding
  for each row
  execute function public.git_repo_binding_guard();

drop trigger if exists git_repo_binding_set_updated_at_and_version on public.git_repo_binding;
create trigger git_repo_binding_set_updated_at_and_version
  before update on public.git_repo_binding
  for each row
  execute function public.set_updated_at_and_version();

drop trigger if exists webhook_delivery_guard on public.webhook_delivery;
create trigger webhook_delivery_guard
  before update on public.webhook_delivery
  for each row
  execute function public.webhook_delivery_guard();

drop trigger if exists catalog_sync_guard on public.catalog_sync;
create trigger catalog_sync_guard
  before update on public.catalog_sync
  for each row
  execute function public.catalog_sync_guard();

alter table public.git_repo_binding enable row level security;
alter table public.git_repo_binding force row level security;
alter table public.webhook_delivery enable row level security;
alter table public.webhook_delivery force row level security;
alter table public.catalog_sync enable row level security;
alter table public.catalog_sync force row level security;

drop policy if exists git_repo_binding_app_tenant on public.git_repo_binding;
create policy git_repo_binding_app_tenant on public.git_repo_binding
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists git_repo_binding_owner_backfill on public.git_repo_binding;
create policy git_repo_binding_owner_backfill on public.git_repo_binding
  for all
  to evidgate_owner
  using (true)
  with check (true);

drop policy if exists webhook_delivery_app_tenant on public.webhook_delivery;
create policy webhook_delivery_app_tenant on public.webhook_delivery
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists webhook_delivery_owner_backfill on public.webhook_delivery;
create policy webhook_delivery_owner_backfill on public.webhook_delivery
  for all
  to evidgate_owner
  using (true)
  with check (true);

drop policy if exists catalog_sync_app_tenant on public.catalog_sync;
create policy catalog_sync_app_tenant on public.catalog_sync
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists catalog_sync_owner_backfill on public.catalog_sync;
create policy catalog_sync_owner_backfill on public.catalog_sync
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- The 0001 default privileges hand every new table DML (app) and SELECT (operator); the grants below are
-- the decision for these three. `revoke` first makes the file re-runnable and removes the queue roles' reach.
revoke all on public.git_repo_binding from public, evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator;
revoke all on public.webhook_delivery from public, evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator;
revoke all on public.catalog_sync from public, evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator;

grant select, insert on public.git_repo_binding to evidgate_app;
grant update (sync_state, head_commit, last_synced_commit, default_branch, updated_at, version)
  on public.git_repo_binding to evidgate_app;
grant select on public.git_repo_binding to evidgate_operator;

grant select, insert on public.webhook_delivery to evidgate_app;
grant update (state) on public.webhook_delivery to evidgate_app;
grant select on public.webhook_delivery to evidgate_operator;

grant select, insert on public.catalog_sync to evidgate_app;
grant update (state, counts, error, started_at, finished_at) on public.catalog_sync to evidgate_app;
grant select on public.catalog_sync to evidgate_operator;
