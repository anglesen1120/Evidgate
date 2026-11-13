-- Installation ownership is stored without credentials. Deleted rows retain their identity;
-- reconnecting creates a new row and only one nondeleted installation may belong to an org.
-- OAuth state holds a digest only, with defaulted convention columns alongside its natural key.
set local lock_timeout = '5s';

create table if not exists public.github_installation (
  id uuid not null,
  org_id uuid not null,
  installation_id bigint not null,
  account_login text,
  account_id bigint,
  permissions jsonb not null,
  state text not null default 'active',
  suspended_at timestamptz,
  installed_by uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint github_installation_pkey primary key (id),
  constraint github_installation_org_id_key unique (org_id, id),
  constraint github_installation_installation_id_key unique (installation_id),
  constraint github_installation_org_fk foreign key (org_id) references public.organization (id),
  constraint github_installation_installed_by_fk foreign key (installed_by) references public."user" (id),
  constraint github_installation_installation_id_check check (installation_id > 0),
  constraint github_installation_account_id_check check (account_id is null or account_id > 0),
  constraint github_installation_account_login_check check (account_login is null or char_length(account_login) between 1 and 100),
  constraint github_installation_permissions_check
    check (jsonb_typeof(permissions) = 'object' and octet_length(permissions::text) <= 8192),
  constraint github_installation_state_check check (state in ('active', 'suspended', 'deleted')),
  constraint github_installation_version_check check (version >= 1)
);

create unique index if not exists github_installation_active_org_key
  on public.github_installation (org_id) where state <> 'deleted';
create index if not exists github_installation_installed_by_idx
  on public.github_installation (installed_by);

create table if not exists public.github_install_state (
  state_hash bytea not null,
  id uuid not null default uuidv7(),
  org_id uuid not null,
  user_id uuid not null,
  expires_at timestamptz not null,
  used_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint github_install_state_pkey primary key (state_hash),
  constraint github_install_state_org_id_key unique (org_id, id),
  constraint github_install_state_org_fk foreign key (org_id) references public.organization (id),
  constraint github_install_state_user_fk foreign key (user_id) references public."user" (id),
  constraint github_install_state_hash_check check (octet_length(state_hash) = 32),
  constraint github_install_state_version_check check (version >= 1)
);

create index if not exists github_install_state_expires_at_idx
  on public.github_install_state (expires_at, state_hash);
create index if not exists github_install_state_user_idx on public.github_install_state (user_id);

create or replace function public.github_installation_guard() returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if (to_jsonb(new) - array['state', 'suspended_at', 'permissions', 'account_login', 'updated_at', 'version'])
     is distinct from
     (to_jsonb(old) - array['state', 'suspended_at', 'permissions', 'account_login', 'updated_at', 'version']) then
    raise exception 'github_installation identity is immutable' using errcode = '23514';
  end if;
  if old.state = 'deleted' then
    raise exception 'github_installation is deleted' using errcode = '23514';
  end if;
  if new.state is distinct from old.state
     and not ((old.state = 'active' and new.state in ('suspended', 'deleted'))
              or (old.state = 'suspended' and new.state in ('active', 'deleted'))) then
    raise exception 'github_installation transition is refused' using errcode = '23514';
  end if;
  -- `deleted` keeps the last suspension for the record; a live state must agree with it, so a
  -- reader can trust `suspended_at` without re-reading `state`.
  if (new.state = 'suspended' and new.suspended_at is null)
     or (new.state = 'active' and new.suspended_at is not null) then
    raise exception 'github_installation suspended_at disagrees with state' using errcode = '23514';
  end if;
  return new;
end
$$;

-- Consuming a nonce is the only permitted edit: its tenant, actor, digest and deadline never move.
create or replace function public.github_install_state_guard() returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if (to_jsonb(new) - array['used_at', 'updated_at', 'version'])
     is distinct from (to_jsonb(old) - array['used_at', 'updated_at', 'version'])
     or old.used_at is not null or new.used_at is null then
    raise exception 'github_install_state may be consumed only once' using errcode = '23514';
  end if;
  return new;
end
$$;

revoke all on function public.github_installation_guard() from public;
revoke all on function public.github_install_state_guard() from public;

drop trigger if exists github_installation_guard on public.github_installation;
create trigger github_installation_guard before update on public.github_installation
  for each row execute function public.github_installation_guard();
drop trigger if exists github_installation_set_updated_at_and_version on public.github_installation;
create trigger github_installation_set_updated_at_and_version before update on public.github_installation
  for each row execute function public.set_updated_at_and_version();
drop trigger if exists github_install_state_guard on public.github_install_state;
create trigger github_install_state_guard before update on public.github_install_state
  for each row execute function public.github_install_state_guard();
drop trigger if exists github_install_state_set_updated_at_and_version on public.github_install_state;
create trigger github_install_state_set_updated_at_and_version before update on public.github_install_state
  for each row execute function public.set_updated_at_and_version();

alter table public.github_installation enable row level security;
alter table public.github_installation force row level security;
drop policy if exists github_installation_app_tenant on public.github_installation;
create policy github_installation_app_tenant on public.github_installation for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists github_installation_owner_backfill on public.github_installation;
create policy github_installation_owner_backfill on public.github_installation for all to evidgate_owner
  using (true) with check (true);

alter table public.github_install_state enable row level security;
alter table public.github_install_state force row level security;
drop policy if exists github_install_state_app_tenant on public.github_install_state;
create policy github_install_state_app_tenant on public.github_install_state for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists github_install_state_owner_backfill on public.github_install_state;
create policy github_install_state_owner_backfill on public.github_install_state for all to evidgate_owner
  using (true) with check (true);

-- Default privileges otherwise leave full DML available; deletion is logical and nonce expiry is owner work.
revoke all on public.github_installation, public.github_install_state
  from evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant select, insert on public.github_installation, public.github_install_state to evidgate_app;
grant update (state, suspended_at, permissions, account_login) on public.github_installation to evidgate_app;
grant update (used_at) on public.github_install_state to evidgate_app;

-- The webhook's pre-tenant lookup appends one platform-scope row per call (TL T80): it runs before any
-- tenant is known, so `audit_log` (org NOT NULL, chained per org) cannot hold it, and the row names no
-- tenant, installation row or payload. `evidgate.record_sweep_audit` is not reused here because it stamps
-- the sweep role as the actor, which would misattribute an app-side read.
create or replace function evidgate_resolve.installation_by_id(p_installation_id bigint)
returns table (org_id uuid, status text)
language plpgsql
volatile
security definer
set search_path = ''
as $$
begin
  insert into public.platform_sweep_audit (actor, action, removed, batch_limit, budget_exhausted)
  values ('evidgate_app', 'github_installation.resolve', 0, 1, false);
  return query
    select i.org_id, i.state from public.github_installation i
     where i.installation_id = p_installation_id;
end
$$;

revoke all on function evidgate_resolve.installation_by_id(bigint)
  from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant execute on function evidgate_resolve.installation_by_id(bigint) to evidgate_app;

create or replace function evidgate.purge_expired_github_install_state(p_limit integer)
returns integer
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_limit integer := least(greatest(coalesce(p_limit, 0), 0), 10000);
  v_removed integer;
begin
  with batch as (
    select s.state_hash from public.github_install_state s
     where s.expires_at <= now()
     order by s.expires_at, s.state_hash
     limit v_limit for update skip locked
  ), removed as (
    delete from public.github_install_state s using batch b
     where s.state_hash = b.state_hash returning s.state_hash
  )
  select count(*)::integer into v_removed from removed;
  insert into public.platform_sweep_audit (actor, action, removed, batch_limit, budget_exhausted)
  values ('evidgate_worker_sched', 'github_install_state.purge', v_removed, greatest(v_limit, 1), false);
  return v_removed;
end
$$;

revoke all on function evidgate.purge_expired_github_install_state(integer)
  from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant execute on function evidgate.purge_expired_github_install_state(integer) to evidgate_worker_sched;
