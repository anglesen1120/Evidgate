-- 0004_identity_organization.sql
--
-- Owner: 02.1.1-T1.1. `organization` is the root of the tenant hierarchy and the
-- one business table that carries no `org_id` column: it is on the 00 §5.4.1
-- exception list (TL T4), because `org_id` would have to reference the row
-- itself. Its policy therefore compares the row's own `id` to the tenant
-- setting, and its members' org list is served by
-- `evidgate_resolve.member_orgs(user_id)` (02.6.1-T3), never by a cross-org
-- `select` under RLS.
--
-- The shape copies the template block of 0003_foundation_rls_probe.sql (00 §5.4,
-- §7, DECISIONS F5): `id` app-generated UUIDv7, timestamps, `version` as the
-- optimistic-concurrency token, ENABLE + FORCE row level security with a policy
-- for the tenant role and a permissive one for the migration role, plus grants
-- per role. Two deviations from that copy block, both deliberate and both
-- explained at the statement: the `set_updated_at_and_version` trigger below (the
-- 0003 template cannot carry it because that file is already applied) and the
-- absence of a DELETE grant for `evidgate_app` (M1W3-05).
-- The `TO evidgate_operator` policy is deliberately absent (TL T76):
-- grant-gated operator access is 02.8.1's `evidgate.operator_grant_ok` policy,
-- and until it lands the operator's SELECT grant below reads zero rows.
--
-- Column ownership: the quota/limit columns of 02.1.1-T3 (`max_projects`,
-- `max_seats`) are deliberately NOT created here — they arrive with that task's
-- migration and the `ITenantLimits` contract.
--
-- Policy consequence for the command side (02.1.1-T2): the `evidgate_app` policy
-- requires `id = evidgate.current_org_id()` for INSERT too, so `POST /v1/orgs`
-- must open its transaction with the tenant setting already pointing at the
-- freshly generated org id before it inserts the row. That is the intended
-- fail-closed behaviour (TL T2): an app-role insert with no tenant setting is
-- rejected instead of silently creating an unowned organization.

create table if not exists public.organization (
  id uuid not null,
  name text not null,
  slug text not null,
  status text not null default 'active',
  retention_default_days integer,
  allowed_email_domains text[],
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint organization_pkey primary key (id),
  constraint organization_slug_key unique (slug),
  -- Input limits (00 §5.7 S3): the slug grammar of the feature plan §4, a name
  -- capped at 128 characters and a bounded domain allowlist.
  constraint organization_slug_format_check check (slug ~ '^[a-z0-9][a-z0-9-]{1,62}$'),
  constraint organization_name_length_check check (char_length(name) between 1 and 128),
  constraint organization_status_check check (status in ('active', 'suspended', 'deleted')),
  constraint organization_email_domains_check check (
    allowed_email_domains is null or cardinality(allowed_email_domains) <= 64)
);

-- 00 §7 / 00 §5.5 C4: `version` is the optimistic-concurrency token and the
-- database owns its increment (M1W3-01). One shared trigger function, created
-- here and reused by every later business table (0005 below; the 0003 copy block
-- cannot carry it because that file is already applied and checksum-locked, so
-- this migration is the authoritative copy for new tables):
--   * `updated_at = now()` on every UPDATE, so a write path cannot forget it;
--   * `version = old.version + 1`, so two writers that read the same version
--     cannot both win — the loser's `where version = @original` matches no row.
-- EF Core reads the incremented token back (`ValueGenerated.OnUpdate` in
-- TenantModelConventions), so a second tracked update in the same scope matches.
--
-- `evidgate_app` needs EXECUTE: a trigger function runs with the privileges of
-- the invoking role, and 0001 revokes the built-in PUBLIC grant on functions
-- created by evidgate_owner, so without the grant every app-role UPDATE would
-- fail with 42501.
create or replace function public.set_updated_at_and_version() returns trigger
  language plpgsql
  as $$
  begin
    new.updated_at = now();
    new.version = old.version + 1;
    return new;
  end
  $$;

revoke all on function public.set_updated_at_and_version() from public;
grant execute on function public.set_updated_at_and_version() to evidgate_app;

drop trigger if exists organization_set_updated_at_and_version on public.organization;
create trigger organization_set_updated_at_and_version
  before update on public.organization
  for each row
  execute function public.set_updated_at_and_version();

alter table public.organization enable row level security;
alter table public.organization force row level security;

-- The app role sees exactly its own org: `id` replaces `org_id` (T4).
drop policy if exists organization_app_tenant on public.organization;
create policy organization_app_tenant on public.organization
  for all
  to evidgate_app
  using (id = evidgate.current_org_id())
  with check (id = evidgate.current_org_id());

-- No `TO evidgate_operator` policy on purpose (TL T76, D1): the tenant-table
-- operator policy is the grant-gated `evidgate.operator_grant_ok` form owned by
-- 02.8.1, not an unconditional org-scoped read. With `force row level security`
-- and no policy for the role, the operator's SELECT grant below returns zero
-- rows until a support grant opens the scope — the standing-access contract of
-- 02.8.1, not a hole.

-- The migration role needs its own policy: `force row level security` applies to
-- the table owner too, so an expand/contract backfill or an owner SECURITY
-- DEFINER sweep would otherwise update zero rows without failing. Explicit and
-- permissive on purpose — the alternative would be BYPASSRLS, which DECISIONS F5
-- rejects.
drop policy if exists organization_owner_backfill on public.organization;
create policy organization_owner_backfill on public.organization
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- M1W3-05 / 00 §5.4.3 least privilege: the app role never hard-deletes a
-- registry row — a removed org is the `status = 'deleted'` soft delete of
-- 02.1.1-T2's commands. The `revoke` is required in addition to omitting DELETE
-- from the grant: 0001's `alter default privileges` hands DELETE to
-- `evidgate_app` on every table `evidgate_owner` creates in `public`, and a
-- `grant` cannot take a privilege away. (The 0003 copy block's rule 7 is
-- therefore superseded for registry tables: app = select, insert, update.)
grant select, insert, update on public.organization to evidgate_app;
revoke delete on public.organization from evidgate_app;
grant select on public.organization to evidgate_operator;
revoke all on public.organization from evidgate_worker_sched;
