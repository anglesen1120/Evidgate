-- 0011_identity_membership.sql
--
-- Owner: 02.1.2-T1.1. `membership` and `membership_role` (area plan 02 §4.1,
-- feature plan 02.1.2 §4): the row that makes a user a member of an org, and the
-- rows that give that member her roles.
--
-- Both tables are ordinary tenant tables of 00 §5.4.1: they carry `org_id`, declare
-- `unique (org_id, id)` so a later table can reference them with a composite key
-- (TL T3), run under ENABLE + FORCE ROW LEVEL SECURITY with the standard
-- `org_id = evidgate.current_org_id()` policy (TL T2), and carry the 00 §7 audit
-- columns plus the shared `set_updated_at_and_version` trigger of 0004.
--
-- The role reference of `membership_role` — read this before "fixing" it:
--
--   `membership_role.role_id` deliberately carries NO declarative foreign key.
--   The feature plan §4 asks for a composite `(org_id, role_id) -> role (org_id, id)`,
--   and 0010 declares `role.unique (org_id, id)` for exactly that, but the two halves
--   of that key cannot both hold for a built-in role: 0010 seeds every built-in role
--   with `org_id IS NULL` (`role_builtin_matches_scope_check` ties `is_builtin` to
--   `org_id is null`), and a composite FK never matches a NULL column, so a
--   `tenant_admin` grant — the role 02.1.1-T2 gives an org's creator (T98) and the
--   role 02.1.2 AC4 protects — could never be inserted. The single-column form
--   0010 uses for the same situation (`role_permission.role_id -> role (id)`, whose
--   exemption is recorded in `SchemaConventionScan`) is not available to a tenant
--   table either: 02.9.1-T2's `TenantFkScan` rule `CompositeForeignKey` flags any
--   foreign key from a tenant table to a table carrying `org_id` that is not the
--   composite tenant key, and its own negative fixture pins exactly that shape
--   (`composite_fk_fixture_catalogue_child` → `role (id)` is a finding).
--
--   So the reference is by value: `role_id` is a `role.id` whose `role.org_id IS NULL`
--   (a built-in) or the caller's own org row. The command layer enforces it —
--   `GrantRoleCommand` resolves the role under RLS and answers 409 `unknown_role`
--   for anything the tenant cannot see (02.1.2 §5) — which is the third isolation
--   layer D8 allows, and the deviation from the plan's composite-FK wording is
--   recorded in the feature plan §3. Do not add a plain `role_id -> role (id)` FK
--   here: it makes `Isolation.Tests` fail, and it does not restore any integrity a
--   built-in role row could satisfy.
--
--   `(org_id, membership_id) -> membership (org_id, id)` IS composite, because the
--   parent is a real tenant table: that is the key that makes a membership of org A
--   invisible — and unpinnable — from org B (AC7).
--
-- `invited_email` is `text`, not `citext`: the `citext` extension is not installed in
-- this database, and the auto-discovered isolation row generator (`TenantRowFactory`)
-- has no value rule for it, so a NOT NULL `citext` column would fail 02.9.1-T2's
-- suite for a reason that has nothing to do with isolation. The command layer
-- lowercases and trims before every compare (02.1.2 §5), and the check constraint
-- below keeps a non-normalised value out of the table at all.
--
-- Column ownership: the invite token columns (`invite_secret_prefix`,
-- `invite_token_hash`, `invite_expires_at`) are created here but stay NULL until
-- 02.1.2-T2 mints the single-use secret (T75); `user_id` stays NULL until a login
-- binds the invite (02.3.1-T2). Neither is a placeholder: a pending membership is a
-- complete row with those columns unset.

create table if not exists public.membership (
  id uuid not null,
  org_id uuid not null,
  user_id uuid,
  invited_email text not null,
  status text not null default 'pending',
  invited_by uuid,
  invite_secret_prefix text,
  invite_token_hash bytea,
  invite_expires_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint membership_pkey primary key (id),
  -- TL T3 / 00 §5.4.6: the composite-FK target; `membership_role` uses it below.
  constraint membership_org_id_key unique (org_id, id),
  constraint membership_organization_fk foreign key (org_id) references public.organization (id),
  -- The states of 02.1.2 §6. `revoked` and `expired` are reachable only from
  -- `pending`; `removed` is terminal (the command layer owns the transitions).
  constraint membership_status_check check (status in ('pending', 'active', 'removed', 'revoked', 'expired')),
  -- FR-02-4: the invited address is compared normalised, so a non-normalised value
  -- must not be storable. (No format check here: the API validates the address, and
  -- a NOT NULL column with no default is one the isolation row generator fills with
  -- its own candidate text, which is not an address.)
  constraint membership_invited_email_normalised_check check (invited_email = lower(invited_email)),
  constraint membership_invited_email_length_check check (char_length(invited_email) between 3 and 320)
);

-- AC1 / §4: at most one PENDING invite per address per org; a re-invite after
-- `removed` or `revoked` creates a new row, which is why the rule is a partial
-- unique index and not a table constraint (PostgreSQL has no partial constraint).
create unique index if not exists membership_pending_email_key
  on public.membership (org_id, invited_email)
  where status = 'pending';

-- One membership per user per org (a user may be a member of several orgs, never
-- twice of one). NULL `user_id` rows — pending invites — are outside the predicate.
create unique index if not exists membership_org_user_key
  on public.membership (org_id, user_id)
  where user_id is not null;

-- The public lookup key of `evidgate_resolve.invite_by_secret(prefix)` (T75):
-- platform-wide unique, because the resolver runs before any tenant scope exists
-- and must map one prefix to exactly one membership. Nullable until T2 mints one;
-- PostgreSQL treats NULLs as distinct, so pending rows without a token do not clash.
create unique index if not exists membership_invite_secret_prefix_key
  on public.membership (invite_secret_prefix);

-- 00 §5.7 P4 / 02.1.2 §8 P3: the member list filters `org_id` and walks
-- `(created_at, id)`, and `status` is in the leading columns because the list is
-- normally "the active members of this org".
create index if not exists membership_org_status_created_id_idx
  on public.membership (org_id, status, created_at, id);

-- 00 §9.1's keyset order of `GET /v1/orgs/{o}/memberships` has no status predicate,
-- and the status-leading index above cannot serve it (an unconstrained middle column
-- breaks the sort order); this is the covering index for the documented page.
create index if not exists membership_org_created_id_idx
  on public.membership (org_id, created_at, id);

create table if not exists public.membership_role (
  id uuid not null,
  org_id uuid not null,
  membership_id uuid not null,
  role_id uuid not null,
  granted_by uuid,
  granted_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint membership_role_pkey primary key (id),
  constraint membership_role_org_id_key unique (org_id, id),
  -- A member holds a role once; re-granting is a no-op in the command layer.
  constraint membership_role_membership_role_key unique (membership_id, role_id),
  -- The composite tenant FK of 00 §5.4.1: a role grant can never point at another
  -- org's membership (AC7). `role_id` is the by-value reference explained above.
  constraint membership_role_membership_fk
    foreign key (org_id, membership_id) references public.membership (org_id, id)
);

create index if not exists membership_role_membership_idx
  on public.membership_role (org_id, membership_id);

-- The last-admin guard (`ActiveMembershipsHoldingRole`) drives its join from one role
-- to its grants; without a `role_id`-leading index it walks every grant of the org
-- (perf review W7: 252 rows read for 2 returned, 208 vs 16 buffers). Mirrors 0010's
-- `role_permission_role_permission_key (role_id, permission_code)`.
create index if not exists membership_role_role_idx
  on public.membership_role (role_id, org_id);

-- The shared concurrency trigger of 0004 (M1W3-01): `updated_at = now()` and
-- `version = old.version + 1` on every UPDATE, one trigger per table. A membership
-- is edited (status, roles), so both tables are mutable and both get it.
drop trigger if exists membership_set_updated_at_and_version on public.membership;
create trigger membership_set_updated_at_and_version
  before update on public.membership
  for each row
  execute function public.set_updated_at_and_version();

drop trigger if exists membership_role_set_updated_at_and_version on public.membership_role;
create trigger membership_role_set_updated_at_and_version
  before update on public.membership_role
  for each row
  execute function public.set_updated_at_and_version();

alter table public.membership enable row level security;
alter table public.membership force row level security;
alter table public.membership_role enable row level security;
alter table public.membership_role force row level security;

-- The tenant policy of 0003/0005 (TL T2): the NULL-safe helper, never a raw
-- current_setting, so a missing tenant context yields zero rows instead of every row.
drop policy if exists membership_app_tenant on public.membership;
create policy membership_app_tenant on public.membership
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists membership_role_app_tenant on public.membership_role;
create policy membership_role_app_tenant on public.membership_role
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- No `TO evidgate_operator` policy on purpose (TL T76, D1): the grant-gated
-- operator policy is 02.8.1's `evidgate.operator_grant_ok` form. Until it lands the
-- operator's SELECT grant below reads zero rows, which is the standing-access
-- contract, not a hole.

-- The migration role needs its own policy on every table: `force row level
-- security` applies to the table owner too, so an expand/contract backfill would
-- otherwise change zero rows without failing (explicit and permissive on purpose;
-- the alternative would be BYPASSRLS, which DECISIONS F5 rejects).
drop policy if exists membership_owner_backfill on public.membership;
create policy membership_owner_backfill on public.membership
  for all
  to evidgate_owner
  using (true)
  with check (true);

drop policy if exists membership_role_owner_backfill on public.membership_role;
create policy membership_role_owner_backfill on public.membership_role
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- 00 §5.4.3 least privilege / M1W3-05: the app role reads, invites, edits and
-- soft-deletes a membership, so it gets SELECT/INSERT/UPDATE and loses DELETE
-- (0001's default privileges hand every new table's DELETE to evidgate_app; a
-- `grant` cannot take it away, so the revoke is load-bearing). `membership_role`
-- is append/remove: a revoked role is a row delete, so DELETE stays.
grant select, insert, update on public.membership to evidgate_app;
revoke delete on public.membership from evidgate_app;
grant select on public.membership to evidgate_operator;
revoke all on public.membership from evidgate_worker_sched;

grant select, insert, update, delete on public.membership_role to evidgate_app;
grant select on public.membership_role to evidgate_operator;
revoke all on public.membership_role from evidgate_worker_sched;
