-- 0083_identity_api_token.sql
--
-- WHY: automation that only needs to read a project (a dashboard, a script, a reporting job) had no
-- credential of its own. A user session is a browser cookie, an ingest token is minted only for
-- `POST /v1/ci/token`, and handing either to a script gives it far more than the project read it needs.
-- This table is the third principal: a project-scoped, always-expiring bearer token that carries an
-- explicit permission subset and no administrative permission.
--
-- SHAPE: one row per token. The public form is `evg_<12-char prefix>_<base64url secret>`; only the
-- prefix, `HMAC-SHA256(pepper_vN, secret)` and `pepper_version` are stored (the ingest-token approach,
-- TL T5), so a database dump holds nothing usable. The prefix is the resolver's lookup key and is
-- unique platform-wide, because the resolver runs before any tenant scope exists.
--
-- STATES are derived, not stored, except revocation: `revoked_at` set -> revoked; `expires_at` passed ->
-- expired; otherwise active. There is no un-revoke and no expiry extension: a replacement is a new row.
-- `expires_at` is NOT NULL — a token that never expires is the standing credential this story refuses.
--
-- TENANCY: `unique (org_id, id)` is the composite-FK target (00 §5.4.6). Both references are composite
-- tenant keys: `(org_id, project_id) -> project (org_id, id)` and `(org_id, created_by) ->
-- membership (org_id, id)`. `created_by` names the creator's MEMBERSHIP, not the platform user: the
-- membership row is the tenant-scoped actor (a member of another org can never be recorded as the
-- creator), and it is the only reference form a composite key can carry, since membership's
-- `(org_id, user_id)` key is a partial unique index and therefore not an FK target.
--
-- PERMISSIONS: `text[]`, non-empty and bounded. The catalogue membership and the deny set are
-- contracts/permissions.json data that the create command and the evaluator enforce at create and at
-- use time; SQL cannot read that file, so a duplicated list here would drift from it. The array shape
-- (one dimension, non-empty, no empty element) is checked.
--
-- RLS: ENABLE + FORCE, with the tenant policy `to evidgate_app` over `evidgate.current_org_id()` (never
-- the raw setting, so a missing or blank setting yields zero rows) and the owner backfill policy FORCE
-- requires (0004/0049/0082 precedent). The table is a normal tenant table, so it is NOT an entry in
-- contracts/rls-exceptions.txt.
--
-- GRANTS: `evidgate_app` holds SELECT, INSERT and a COLUMN-scoped UPDATE on `revoked_at`/`last_used_at`
-- only — revocation and the throttled usage stamp are the only writes the API performs — and no DELETE
-- (revocation never deletes; retention deletes expired rows as the owner). The revoke precedes the grant
-- because 0001's default privileges would otherwise leave full DML in place (0046/0080 precedent).
-- `evidgate_operator` keeps its default SELECT with no operator policy, so it reads zero rows until a
-- grant-gated operator policy lands (0028/0069/0072 posture); `evidgate_worker_sched` holds nothing.
--
-- GUARDS: `api_token_guard_write` refuses, for every role, any change to the credential identity
-- (prefix, HMAC, pepper version, permissions, expiry, project and creator) and any second write to
-- `revoked_at` — an un-revoke or a re-revoke is a state-machine violation, not an edit (the
-- `waiver_guard`/`run_identity_immutable` shape). `name` stays mutable for display. The shared
-- `set_updated_at_and_version` trigger is the schema convention; `last_used_at` bookkeeping bumps the
-- version, which is intentional — no reader pins the version of this table (the revoke route carries no
-- If-Match), so there is no keep-version-on-touch trigger to maintain.
--
-- RESOLVER: `evidgate_resolve.resolve_api_token(prefix)` is the one pre-tenant path: owner-owned,
-- SECURITY DEFINER, `search_path = ''`, EXECUTE for `evidgate_app` only, and it returns only the
-- documented tuple — the HMAC is verified afterwards, inside the resolved tenant scope.
--
-- ORDER: additive for every reader, so migrate before the API that serves tokens. No down migration:
-- dropping the table would delete live credentials, and revoking the permission rows would 403 a route
-- that is already deployed.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`,
-- `drop policy/trigger if exists` before every policy and trigger, `create or replace function`,
-- `on conflict do nothing` seeds and the repeatable `revoke`/`grant`.

set local lock_timeout = '5s';

create table if not exists public.api_token (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  name text not null,
  prefix text not null,
  token_hmac bytea not null,
  pepper_version integer not null,
  permissions text[] not null,
  expires_at timestamptz not null,
  revoked_at timestamptz,
  last_used_at timestamptz,
  created_by uuid not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint api_token_pkey primary key (id),
  constraint api_token_org_id_key unique (org_id, id),
  constraint api_token_prefix_key unique (prefix),
  constraint api_token_project_fk
    foreign key (org_id, project_id) references public.project (org_id, id),
  constraint api_token_created_by_fk
    foreign key (org_id, created_by) references public.membership (org_id, id),
  constraint api_token_prefix_check check (prefix ~ '^[A-Za-z0-9]{12}$'),
  constraint api_token_hmac_length_check check (octet_length(token_hmac) = 32),
  constraint api_token_pepper_version_check check (pepper_version >= 1),
  constraint api_token_name_length_check check (char_length(name) between 1 and 120),
  constraint api_token_permissions_check check (
    case
      when array_ndims(permissions) is distinct from 1 then false
      else cardinality(permissions) between 1 and 64
           and array_position(permissions, '') is null
    end),
  constraint api_token_expiry_check check (expires_at > created_at),
  constraint api_token_revoked_after_created_check check (revoked_at is null or revoked_at >= created_at)
);

-- The token list pages `(created_at, id)` per project, and the project half is the predicate the list
-- always carries, so the key leads with it (00 §5.7 P4).
create index if not exists api_token_org_project_created_idx
  on public.api_token (org_id, project_id, created_at, id);

drop trigger if exists api_token_set_updated_at_and_version on public.api_token;
create trigger api_token_set_updated_at_and_version
  before update on public.api_token
  for each row
  execute function public.set_updated_at_and_version();

-- WHY: the row has two writers — the create/revoke commands and the throttled `last_used_at` stamp — and
-- only three columns are legitimately mutable. The credential identity and the grants it carries are
-- written once; a later widening (an extended expiry, an added permission) would be an escalation that
-- no command validates. A second `revoked_at` write would break the `active -> revoked` machine.
create or replace function public.api_token_guard_write() returns trigger
  language plpgsql
  set search_path = ''
as $$
begin
  if new.org_id is distinct from old.org_id
     or new.project_id is distinct from old.project_id
     or new.created_by is distinct from old.created_by
     or new.prefix is distinct from old.prefix
     or new.token_hmac is distinct from old.token_hmac
     or new.pepper_version is distinct from old.pepper_version
     or new.permissions is distinct from old.permissions
     or new.expires_at is distinct from old.expires_at then
    raise exception 'api_token % carries its credential and grant for life; create a replacement instead', old.id
      using errcode = '23514';
  end if;

  if old.revoked_at is not null and new.revoked_at is distinct from old.revoked_at then
    raise exception 'api_token % is already revoked; a revocation is never rewritten', old.id
      using errcode = '23514';
  end if;

  return new;
end
$$;

revoke all on function public.api_token_guard_write() from public;
grant execute on function public.api_token_guard_write() to evidgate_app;

drop trigger if exists api_token_guard_write on public.api_token;
create trigger api_token_guard_write
  before update on public.api_token
  for each row
  execute function public.api_token_guard_write();

alter table public.api_token enable row level security;
alter table public.api_token force row level security;

drop policy if exists api_token_app_tenant on public.api_token;
create policy api_token_app_tenant on public.api_token
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists api_token_owner_backfill on public.api_token;
create policy api_token_owner_backfill on public.api_token
  for all
  to evidgate_owner
  using (true)
  with check (true);

revoke delete on public.api_token from evidgate_app;
revoke update on public.api_token from evidgate_app;
grant select, insert on public.api_token to evidgate_app;
grant update (revoked_at, last_used_at) on public.api_token to evidgate_app;
grant select on public.api_token to evidgate_operator;
revoke all on public.api_token from evidgate_worker_sched;

-- The two catalogue strings of the token route. `api_token:write` is sensitive because it returns the
-- plaintext exactly once; `api_token:read` returns metadata only (never the secret or its HMAC). The
-- resource/action pair must equal the code, and the rows must stay byte-identical to
-- contracts/permissions.json, which `SeedMatrixTests.RolesMatchOverview` diffs against the seeded
-- catalogue.
do $seed$
declare
  permissions jsonb := '[
    {"code": "api_token:read", "resource": "api_token", "action": "read",
     "description": "List project API-token metadata (id, name, prefix, permissions, status, last_used_at) — never the secret or its HMAC (02.4.4).",
     "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "api_token:write", "resource": "api_token", "action": "write",
     "description": "Create or revoke a project API token; the plaintext secret is returned exactly once (02.4.4).",
     "sensitive": true, "reserved": false, "tenant_assignable": true}
  ]'::jsonb;

  grants jsonb := '[
    {"role_code": "tenant_admin", "permission_code": "api_token:read"},
    {"role_code": "tenant_admin", "permission_code": "api_token:write"}
  ]'::jsonb;
begin
  insert into public.permission (code, resource, action, description, sensitive, reserved, tenant_assignable)
  select p.code, p.resource, p.action, p.description, p.sensitive, p.reserved, p.tenant_assignable
    from jsonb_to_recordset(permissions)
      as p(code text, resource text, action text, description text,
           sensitive boolean, reserved boolean, tenant_assignable boolean)
  on conflict (code) do nothing;

  -- The built-in roles are 0010's rows (`org_id is null`); an org-scoped custom role with the same code
  -- never matches here, and a tenant that narrows a built-in role keeps its choice on a re-run.
  insert into public.role_permission (id, org_id, role_id, permission_code)
  select uuidv7(), null, r.id, granted.permission_code
    from jsonb_to_recordset(grants) as granted(role_code text, permission_code text)
    join public.role r on r.org_id is null and r.code = granted.role_code
  on conflict (role_id, permission_code) do nothing;
end
$seed$;

-- lint: audit-deferred bearer-handler evidgate_resolve.resolve_api_token
--
-- The pre-tenant resolver of an API token: the prefix is the only thing the caller knows. It returns the
-- tenant tuple and a derived status so the bearer handler picks the 401 code without reading the table;
-- the HMAC is verified afterwards, inside the resolved tenant scope. No row = unknown prefix.
--
-- No per-call audit row is written here on purpose: this function runs before any credential is proven,
-- so a row per call would let an unauthenticated prefix probe write to the audit chain of an org whose
-- existence it only guessed, and the accepted-use record is the throttled `last_used_at` stamp plus the
-- handler's `api_token.use_denied` rows (the ingest-token resolver records the same deferral).
create or replace function evidgate_resolve.resolve_api_token(p_prefix text)
returns table (org_id uuid, project_id uuid, token_id uuid, status text)
language sql
stable
security definer
set search_path = ''
as $$
  select t.org_id, t.project_id, t.id,
         case
           when t.revoked_at is not null then 'revoked'
           when t.expires_at <= now() then 'expired'
           else 'active'
         end
    from public.api_token t
   where t.prefix = p_prefix
$$;

revoke all on function evidgate_resolve.resolve_api_token(text) from public;
grant execute on function evidgate_resolve.resolve_api_token(text) to evidgate_app;
