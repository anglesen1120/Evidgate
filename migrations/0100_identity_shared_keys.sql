-- The Data Protection key ring and the login replay set move out of each process and into PostgreSQL, so every Api
-- replica protects and unprotects the login, link and antiforgery cookies with the same keys and refuses a replayed
-- login `state` that another replica already consumed (00 §5.7 S8). Until now both lived in process memory: with N
-- replicas a cookie minted by one failed on the others, and a restart lost the ring.
--
-- Two platform tables (no org_id: neither holds customer business data; the keys protect pre-tenant cookies and the
-- replay set is keyed by a login `state`), listed in contracts/rls-exceptions.txt as `platform`:
--   public.data_protection_key  one row per ring element, `friendly_name` unique; `key_xml` holds the element as the
--                               application stored it (AES-256-GCM envelope when a key-encryption key is configured).
--   public.login_state_replay   sha-256 of `provider:state` -> `expires_at`; a live row means "already used".
--
-- Access is the session precedent (0041/0048): FORCE row level security with the owner-only `*_owner_backfill`
-- policy, every privilege revoked from the runtime roles, and three invoker-less definers in `evidgate_resolve` that
-- only `evidgate_app` may execute. The app role can therefore neither read the ring nor edit the replay set directly.
--   evidgate_resolve.data_protection_key_list()                       -> every ring element
--   evidgate_resolve.data_protection_key_store(name, xml)             -> insert-if-absent (two replicas may mint the
--                                                                        same element name; one wins, both read it back)
--   evidgate_resolve.login_state_replay_consume(hash, now, expires)   -> true on first use; false while a live row exists.
--                                                                        An expired row of the same hash is replaced, and each call
--                                                                        deletes up to 100 expired rows first: that is the
--                                                                        cleanup path, so the table is bounded by the live
--                                                                        window without a scheduled sweep.
--
-- lint: audit-deferred 02.3.1-T1.4 evidgate_resolve.data_protection_key_list, evidgate_resolve.data_protection_key_store, evidgate_resolve.login_state_replay_consume
--
-- Rollback: drop the three functions and the two tables. The ring is regenerated on the next start (existing login
-- cookies fail once) and the replay set is empty (a state used in the last few minutes could be replayed once).
--
-- Deploy: migration first, then the application; an older application keeps its in-process ring and set.

create table if not exists public.data_protection_key (
  id            uuid primary key default gen_random_uuid(),
  friendly_name text not null,
  key_xml       text not null,
  created_at    timestamptz not null default now(),
  constraint data_protection_key_friendly_name_key unique (friendly_name),
  constraint data_protection_key_friendly_name_check check (char_length(friendly_name) between 1 and 256),
  constraint data_protection_key_key_xml_check check (char_length(key_xml) between 1 and 65536)
);

create table if not exists public.login_state_replay (
  state_hash bytea primary key,
  expires_at timestamptz not null,
  constraint login_state_replay_state_hash_check check (octet_length(state_hash) = 32)
);

create index if not exists login_state_replay_expires_at_idx on public.login_state_replay (expires_at);

alter table public.data_protection_key enable row level security;
alter table public.data_protection_key force row level security;
alter table public.login_state_replay enable row level security;
alter table public.login_state_replay force row level security;

drop policy if exists data_protection_key_owner_backfill on public.data_protection_key;
create policy data_protection_key_owner_backfill on public.data_protection_key
  for all
  to evidgate_owner
  using (true)
  with check (true);

drop policy if exists login_state_replay_owner_backfill on public.login_state_replay;
create policy login_state_replay_owner_backfill on public.login_state_replay
  for all
  to evidgate_owner
  using (true)
  with check (true);

revoke all on public.data_protection_key from evidgate_app, evidgate_operator, evidgate_worker_sched;
revoke all on public.login_state_replay from evidgate_app, evidgate_operator, evidgate_worker_sched;

create or replace function evidgate_resolve.data_protection_key_list()
returns table (friendly_name text, key_xml text)
language sql
stable
security definer
set search_path = ''
as $$
  select k.friendly_name, k.key_xml
    from public.data_protection_key k
   order by k.created_at, k.id;
$$;

create or replace function evidgate_resolve.data_protection_key_store(
  p_friendly_name text,
  p_key_xml text)
returns void
language plpgsql
volatile
security definer
set search_path = ''
as $$
begin
  if p_friendly_name is null or p_key_xml is null then
    raise exception 'data_protection_key_store: name and xml are required' using errcode = '22004';
  end if;

  insert into public.data_protection_key (friendly_name, key_xml)
  values (p_friendly_name, p_key_xml)
  on conflict (friendly_name) do nothing;
end
$$;

create or replace function evidgate_resolve.login_state_replay_consume(
  p_state_hash bytea,
  p_now timestamptz,
  p_expires_at timestamptz)
returns boolean
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_written integer;
begin
  if p_state_hash is null or p_now is null or p_expires_at is null
     or p_expires_at <= p_now or p_expires_at > p_now + interval '1 day' then
    raise exception 'login_state_replay_consume: invalid arguments' using errcode = '22023';
  end if;

  delete from public.login_state_replay r
   where r.state_hash in (
           select e.state_hash
             from public.login_state_replay e
            where e.expires_at <= p_now
              and e.state_hash <> p_state_hash
            order by e.expires_at
            limit 100
              for update skip locked);

  delete from public.login_state_replay r
   where r.state_hash = p_state_hash
     and r.expires_at <= p_now;

  insert into public.login_state_replay (state_hash, expires_at)
  values (p_state_hash, p_expires_at)
  on conflict (state_hash) do nothing;

  get diagnostics v_written = row_count;

  return v_written = 1;
end
$$;

revoke all on function evidgate_resolve.data_protection_key_list() from public;
revoke all on function evidgate_resolve.data_protection_key_store(text, text) from public;
revoke all on function evidgate_resolve.login_state_replay_consume(bytea, timestamptz, timestamptz) from public;
grant execute on function evidgate_resolve.data_protection_key_list() to evidgate_app;
grant execute on function evidgate_resolve.data_protection_key_store(text, text) to evidgate_app;
grant execute on function evidgate_resolve.login_state_replay_consume(bytea, timestamptz, timestamptz) to evidgate_app;
