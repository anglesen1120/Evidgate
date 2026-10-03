-- 0018_identity_ingest_token.sql
--
-- Owner: 02.4.2-T1.1. `ingest_token`: the revocable, rotatable credential of a project on a
-- non-GitHub CI. The public form is `egt_<12-char prefix>_<base64url secret>`; only the prefix
-- and HMAC-SHA256(pepper_vN, secret) are stored (TL T5), so a database dump holds nothing
-- usable. The prefix is the resolver's lookup key (unique platform-wide).
--
-- States are derived, not stored, except revocation: `revoked_at` set -> revoked; `expires_at`
-- or `overlap_ends_at` passed -> expired; otherwise active. A rotation sets `rotated_to_id` and
-- `overlap_ends_at` on the old token (24 h, D-A5) and creates the successor; a token is either
-- rotated or revoked, never both.

create table if not exists public.ingest_token (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  name text not null,
  prefix text not null,
  token_hmac bytea not null,
  pepper_version integer not null,
  scopes text[] not null,
  expires_at timestamptz,
  overlap_ends_at timestamptz,
  revoked_at timestamptz,
  rotated_to_id uuid,
  last_used_at timestamptz,
  created_by uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint ingest_token_pkey primary key (id),
  constraint ingest_token_org_id_key unique (org_id, id),
  constraint ingest_token_prefix_key unique (prefix),
  constraint ingest_token_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint ingest_token_rotated_to_fk foreign key (org_id, rotated_to_id) references public.ingest_token (org_id, id),
  constraint ingest_token_prefix_check check (prefix ~ '^[A-Za-z0-9]{12}$'),
  constraint ingest_token_hmac_length_check check (octet_length(token_hmac) = 32),
  constraint ingest_token_name_length_check check (char_length(name) between 1 and 128),
  constraint ingest_token_scopes_check check (
    cardinality(scopes) between 1 and 3 and scopes <@ array['event:write', 'artifact:write', 'run:create']::text[]),
  constraint ingest_token_rotated_or_revoked_check check (revoked_at is null or rotated_to_id is null)
);

create index if not exists ingest_token_org_project_created_idx
  on public.ingest_token (org_id, project_id, created_at, id);

drop trigger if exists ingest_token_set_updated_at_and_version on public.ingest_token;
create trigger ingest_token_set_updated_at_and_version
  before update on public.ingest_token
  for each row
  execute function public.set_updated_at_and_version();

alter table public.ingest_token enable row level security;
alter table public.ingest_token force row level security;

drop policy if exists ingest_token_app_tenant on public.ingest_token;
create policy ingest_token_app_tenant on public.ingest_token
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists ingest_token_owner_backfill on public.ingest_token;
create policy ingest_token_owner_backfill on public.ingest_token
  for all
  to evidgate_owner
  using (true)
  with check (true);

grant select, insert, update on public.ingest_token to evidgate_app;
revoke delete on public.ingest_token from evidgate_app;
grant select on public.ingest_token to evidgate_operator;
revoke all on public.ingest_token from evidgate_worker_sched;

-- lint: audit-deferred 02.7.1-T2.2 evidgate_resolve.resolve_ingest_token
--
-- The pre-tenant resolver of the ingest-token grant: the prefix is the only thing the caller
-- knows. It returns the tenant tuple and a derived status so the endpoint picks the right 401
-- without reading the table; the HMAC is verified afterwards, inside the resolved tenant scope.
-- No row = unknown prefix.
create or replace function evidgate_resolve.resolve_ingest_token(p_prefix text)
returns table (org_id uuid, project_id uuid, binding_id uuid, status text)
language sql
stable
security definer
set search_path = ''
as $$
  select t.org_id, t.project_id, t.id,
         case
           when t.revoked_at is not null then 'revoked'
           when (t.expires_at is not null and t.expires_at <= now())
                or (t.overlap_ends_at is not null and t.overlap_ends_at <= now()) then 'expired'
           else 'active'
         end
    from public.ingest_token t
   where t.prefix = p_prefix
$$;

revoke all on function evidgate_resolve.resolve_ingest_token(text) from public;
grant execute on function evidgate_resolve.resolve_ingest_token(text) to evidgate_app;
