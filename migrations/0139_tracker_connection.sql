-- 0139_tracker_connection.sql
-- Why: a project that keeps its requirements in Jira Cloud or Linear needs a stored, read-only connection to that
--   tracker, a cache of the items it reads, and a short-lived record of a consent that is still in flight.
-- What: public.tracker_connection (one live connection per project and provider, tokens kept only as ciphertext),
--   public.tracker_item (the read-through cache of tracker items), public.tracker_oauth_state (the single-use,
--   expiring consent state) and requirement.tracker_item_id (the link 0132 deliberately left out), with their
--   composite tenant keys, guards, FORCE RLS policies and minimal grants.
-- Order: after 0138; the migrator refuses a gap, so 0137 and 0138 must land first.
-- Lock: three new tables; requirement gets one nullable column and one composite key (the column is all NULL, so
--   the key check scans nothing that matters). lock_timeout is 5 s.
-- Scope of the app role: it may create a connection and move its state, tokens, scopes and upstream id, but it
--   holds no UPDATE on create_issue_enabled (the write opt-in has no writer in this task) and no DELETE anywhere:
--   a disconnect is the `disconnected` state with both token columns cleared. A disconnected row is terminal.
-- Purge order for the owner erasure path: requirement rows lose tracker_item_id (or are deleted) first, then
--   tracker_item, then tracker_connection and tracker_oauth_state, then the project.
set local lock_timeout = '5s';

create table if not exists public.tracker_connection (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  provider text not null,
  state text not null default 'active',
  -- Nullable like requirement_version.author_membership_id: the writer names the acting member, and a row
  -- written by the owner path has none, which MATCH SIMPLE leaves unenforced.
  created_by_membership_id uuid,
  -- Ciphertext produced by the application's data protector; the plaintext token never reaches this table.
  access_token_cipher text,
  refresh_token_cipher text,
  token_expires_at timestamptz,
  -- Space-delimited OAuth scope string as the provider granted it, split by the reader.
  granted_scopes text not null default '',
  create_issue_enabled boolean not null default false,
  -- The provider's own tenant handle (Jira cloud id, Linear organization id); set once by the first sync.
  upstream_resource_id text,
  disconnected_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint tracker_connection_pkey primary key (id),
  constraint tracker_connection_org_id_key unique (org_id, id),
  -- The target of the item key: an item provably belongs to this connection and its project.
  constraint tracker_connection_project_id_key unique (org_id, project_id, id),
  constraint tracker_connection_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  constraint tracker_connection_membership_fk foreign key (org_id, created_by_membership_id)
    references public.membership (org_id, id),
  constraint tracker_connection_provider_check check (provider in ('jira_cloud', 'linear')),
  constraint tracker_connection_state_check check (state in ('active', 'needs_reauth', 'disconnected')),
  constraint tracker_connection_access_cipher_check
    check (access_token_cipher is null or char_length(access_token_cipher) between 1 and 8192),
  constraint tracker_connection_refresh_cipher_check
    check (refresh_token_cipher is null or char_length(refresh_token_cipher) between 1 and 8192),
  constraint tracker_connection_scopes_check check (char_length(granted_scopes) <= 2000),
  constraint tracker_connection_upstream_check
    check (upstream_resource_id is null or char_length(upstream_resource_id) between 1 and 200),
  -- A disconnected row holds no credential at all; a live row always holds an access token.
  constraint tracker_connection_disconnected_clean_check
    check (state <> 'disconnected' or (access_token_cipher is null and refresh_token_cipher is null)),
  constraint tracker_connection_active_token_check
    check (state <> 'active' or access_token_cipher is not null),
  constraint tracker_connection_disconnected_at_check
    check ((state = 'disconnected') = (disconnected_at is not null))
);

-- At most one live connection per project and provider; a disconnected row does not block a new one.
create unique index if not exists tracker_connection_live_uq
  on public.tracker_connection (org_id, project_id, provider)
  where state <> 'disconnected';

-- The lookup the composite project key needs when a project row is purged, and the project's connection list.
create index if not exists tracker_connection_project_idx
  on public.tracker_connection (org_id, project_id, created_at, id);

-- Membership lookup of the composite key when a membership row is purged.
create index if not exists tracker_connection_member_idx
  on public.tracker_connection (org_id, created_by_membership_id)
  where created_by_membership_id is not null;

create table if not exists public.tracker_item (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  connection_id uuid not null,
  provider_id text not null,
  provider_key text not null,
  kind text not null,
  -- Display only: never fetched and never used to identify the item.
  url text,
  content_hash text not null,
  last_seen_at timestamptz not null default now(),
  removed_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint tracker_item_pkey primary key (id),
  constraint tracker_item_org_id_key unique (org_id, id),
  -- The target of requirement.tracker_item_id: the item provably belongs to the requirement's project.
  constraint tracker_item_project_id_key unique (org_id, project_id, id),
  -- One row per upstream item per connection; its prefix is also the lookup the connection key needs.
  constraint tracker_item_provider_key unique (org_id, project_id, connection_id, provider_id),
  constraint tracker_item_connection_fk foreign key (org_id, project_id, connection_id)
    references public.tracker_connection (org_id, project_id, id),
  constraint tracker_item_provider_id_check check (char_length(provider_id) between 1 and 200),
  constraint tracker_item_provider_key_check check (char_length(provider_key) between 1 and 128),
  constraint tracker_item_kind_check check (kind in ('story', 'epic', 'bug', 'other')),
  constraint tracker_item_url_check
    check (url is null or (char_length(url) <= 2048 and url ~ '^https://')),
  constraint tracker_item_content_hash_check check (content_hash ~ '^sha256:[0-9a-f]{64}$')
);

drop trigger if exists tracker_connection_set_updated_at_and_version on public.tracker_connection;
create trigger tracker_connection_set_updated_at_and_version
  before update on public.tracker_connection
  for each row execute function public.set_updated_at_and_version();

drop trigger if exists tracker_item_set_updated_at_and_version on public.tracker_item;
create trigger tracker_item_set_updated_at_and_version
  before update on public.tracker_item
  for each row execute function public.set_updated_at_and_version();

-- The project's item list in provider key order.
create index if not exists tracker_item_project_key_idx
  on public.tracker_item (org_id, project_id, provider_key, id)
  where removed_at is null;

-- The link from a requirement to the tracker item it was imported from, pinned to the same project.
alter table public.requirement add column if not exists tracker_item_id uuid;

alter table public.requirement drop constraint if exists requirement_tracker_item_fk;
alter table public.requirement add constraint requirement_tracker_item_fk
  foreign key (org_id, project_id, tracker_item_id)
  references public.tracker_item (org_id, project_id, id);

-- The lookup the composite item key needs when an item row is purged.
create index if not exists requirement_tracker_item_idx
  on public.requirement (org_id, project_id, tracker_item_id)
  where tracker_item_id is not null;

create table if not exists public.tracker_oauth_state (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  user_id uuid not null,
  provider text not null,
  intent text not null default 'read',
  -- SHA-256 of the opaque state value; the value itself is never stored.
  state_hash bytea not null,
  -- The PKCE verifier as ciphertext; null for a provider that does not use PKCE.
  pkce_verifier_cipher text,
  redirect_uri text not null,
  expires_at timestamptz not null,
  consumed_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint tracker_oauth_state_pkey primary key (id),
  constraint tracker_oauth_state_org_id_key unique (org_id, id),
  constraint tracker_oauth_state_hash_key unique (state_hash),
  constraint tracker_oauth_state_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  constraint tracker_oauth_state_user_fk foreign key (user_id)
    references public."user" (id),
  constraint tracker_oauth_state_provider_check check (provider in ('jira_cloud', 'linear')),
  constraint tracker_oauth_state_intent_check check (intent = 'read'),
  constraint tracker_oauth_state_hash_check check (octet_length(state_hash) = 32),
  constraint tracker_oauth_state_pkce_check
    check (pkce_verifier_cipher is null or char_length(pkce_verifier_cipher) between 1 and 2048),
  constraint tracker_oauth_state_redirect_check check (char_length(redirect_uri) between 1 and 2048),
  constraint tracker_oauth_state_expiry_check check (expires_at > created_at)
);

drop trigger if exists tracker_oauth_state_set_updated_at_and_version on public.tracker_oauth_state;
create trigger tracker_oauth_state_set_updated_at_and_version
  before update on public.tracker_oauth_state
  for each row execute function public.set_updated_at_and_version();

-- Lookups the composite project key and the user key need when those rows are purged; the expiry index
-- serves the sweep that removes spent and expired states.
create index if not exists tracker_oauth_state_project_idx
  on public.tracker_oauth_state (org_id, project_id);
create index if not exists tracker_oauth_state_user_idx
  on public.tracker_oauth_state (user_id);
create index if not exists tracker_oauth_state_expires_idx
  on public.tracker_oauth_state (expires_at);

-- A connection never changes its identity, never leaves `disconnected`, never returns to a live state with a
-- token, and keeps the first upstream handle it was given. A new connection starts live and read-only.
create or replace function evidgate.tracker_connection_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'INSERT' then
      if new.state <> 'active' or new.create_issue_enabled or new.disconnected_at is not null then
        raise exception 'tracker_connection_guard: a new connection is active and read-only'
          using errcode = '23514';
      end if;
      return new;
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.provider is distinct from old.provider
       or new.created_by_membership_id is distinct from old.created_by_membership_id
       or new.created_at is distinct from old.created_at then
      raise exception 'tracker_connection_guard: the identity of a connection is immutable'
        using errcode = '23514';
    end if;

    if old.state = 'disconnected' then
      raise exception 'tracker_connection_guard: a disconnected connection is terminal'
        using errcode = '23514';
    end if;

    if old.upstream_resource_id is not null
       and new.upstream_resource_id is distinct from old.upstream_resource_id then
      raise exception 'tracker_connection_guard: the upstream resource id is set once'
        using errcode = '23514';
    end if;
    return new;
  end
  $$;

revoke all on function evidgate.tracker_connection_guard() from public;
grant execute on function evidgate.tracker_connection_guard() to evidgate_app;

drop trigger if exists tracker_connection_guard on public.tracker_connection;
create trigger tracker_connection_guard
  before insert or update on public.tracker_connection
  for each row execute function evidgate.tracker_connection_guard();

-- A consent state is consumed once: only consumed_at may change, and only from NULL to a value.
create or replace function evidgate.tracker_oauth_state_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if (to_jsonb(new) - array['consumed_at', 'updated_at', 'version'])
       is distinct from (to_jsonb(old) - array['consumed_at', 'updated_at', 'version']) then
      raise exception 'tracker_oauth_state_guard: only consumed_at may change'
        using errcode = '23514';
    end if;
    if old.consumed_at is not null or new.consumed_at is null then
      raise exception 'tracker_oauth_state_guard: a consent state is consumed once'
        using errcode = '23514';
    end if;
    return new;
  end
  $$;

revoke all on function evidgate.tracker_oauth_state_guard() from public;
grant execute on function evidgate.tracker_oauth_state_guard() to evidgate_app;

drop trigger if exists tracker_oauth_state_guard on public.tracker_oauth_state;
create trigger tracker_oauth_state_guard
  before update on public.tracker_oauth_state
  for each row execute function evidgate.tracker_oauth_state_guard();

-- Row level security: ENABLE + FORCE, the tenant policy for the app role and the owner policy the erasure
-- path needs (FORCE binds the owner, so without it the purge would delete zero rows without failing).
alter table public.tracker_connection enable row level security;
alter table public.tracker_connection force row level security;

drop policy if exists tracker_connection_tenant on public.tracker_connection;
create policy tracker_connection_tenant on public.tracker_connection
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists tracker_connection_owner_backfill on public.tracker_connection;
create policy tracker_connection_owner_backfill on public.tracker_connection
  for all to evidgate_owner using (true) with check (true);

alter table public.tracker_item enable row level security;
alter table public.tracker_item force row level security;

drop policy if exists tracker_item_tenant on public.tracker_item;
create policy tracker_item_tenant on public.tracker_item
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists tracker_item_owner_backfill on public.tracker_item;
create policy tracker_item_owner_backfill on public.tracker_item
  for all to evidgate_owner using (true) with check (true);

alter table public.tracker_oauth_state enable row level security;
alter table public.tracker_oauth_state force row level security;

drop policy if exists tracker_oauth_state_tenant on public.tracker_oauth_state;
create policy tracker_oauth_state_tenant on public.tracker_oauth_state
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists tracker_oauth_state_owner_backfill on public.tracker_oauth_state;
create policy tracker_oauth_state_owner_backfill on public.tracker_oauth_state
  for all to evidgate_owner using (true) with check (true);

-- Grants: 0001's default privileges give the app role full DML on every new table, so the revoke comes first.
-- create_issue_enabled is deliberately outside the UPDATE list: the write opt-in is a later grant.
revoke update, delete on public.tracker_connection from evidgate_app;
grant select, insert on public.tracker_connection to evidgate_app;
grant update (state, access_token_cipher, refresh_token_cipher, token_expires_at, granted_scopes,
              upstream_resource_id, disconnected_at)
  on public.tracker_connection to evidgate_app;
revoke all on public.tracker_connection from evidgate_operator;
revoke all on public.tracker_connection from evidgate_worker_sched;

revoke update, delete on public.tracker_item from evidgate_app;
grant select, insert on public.tracker_item to evidgate_app;
grant update (provider_key, kind, url, content_hash, last_seen_at, removed_at)
  on public.tracker_item to evidgate_app;
revoke all on public.tracker_item from evidgate_operator;
revoke all on public.tracker_item from evidgate_worker_sched;

revoke update, delete on public.tracker_oauth_state from evidgate_app;
grant select, insert on public.tracker_oauth_state to evidgate_app;
grant update (consumed_at) on public.tracker_oauth_state to evidgate_app;
revoke all on public.tracker_oauth_state from evidgate_operator;
revoke all on public.tracker_oauth_state from evidgate_worker_sched;
