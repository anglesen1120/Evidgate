-- 0010_identity_permission_catalogue.sql
--
-- Owner: 02.2.1-T1.2. The permission catalogue of 00 §2 / 02 §4.1: `permission`,
-- `role` and `role_permission`, seeded from the content of `contracts/permissions.json`
-- embedded below (the migration is self-contained by design — a migration may not read
-- a repository file at apply time, and the file itself is checksum-locked once applied).
--
-- Three tables, three different RLS postures, all on the 00 §5.4.1 exception list (T3)
-- and all `ENABLE` + `FORCE` (DECISIONS F5):
--
--   * `permission` is a platform catalogue with NO `org_id` column: readable by every
--     org (`using (true)`) and written only by `evidgate_owner` — the app role's write
--     policies are per-command denials (`with check (false)` / `using (false)`), and the
--     INSERT/UPDATE/DELETE privileges 0001's default privileges hand out are revoked on
--     top. A `for all using (true) with check (false)` policy would have been one policy
--     shorter and would have left DELETE allowed (a `for all` USING clause governs
--     DELETE too), which is why the denial is spelled per command.
--   * `role` and `role_permission` carry a nullable `org_id`: `org_id is null` is a
--     built-in (catalogue) row, a non-null `org_id` is a tenant's future custom role
--     (02 §4.5, RECOMMENDATIONS 02-Q3 keeps the write commands behind a flag in V1).
--     Read is `org_id is null or org_id = evidgate.current_org_id()` — the all-org-read
--     policy of 02 §4.5 — and the app role may write only rows of its own org, so the
--     WITH CHECK can never be satisfied by a built-in row and an app-role insert of one
--     is rejected by the policy rather than by a privilege a later migration could
--     re-grant by accident. `role_permission`'s write policies carry a second tie on top
--     of it (W7-SEC-05): the referenced role must itself be an org row of the caller's
--     org, so a grant cannot be attached to a built-in role and widen it for every member
--     who holds it — the `role(id)` FK alone is org-agnostic (see below).
--
-- `role.kind` records who may hold the role (W7-SEC-03): `member` for the five member
-- roles of 00 §2 and every custom role, `service` for `ci_producer`/`agent`, `staff` for
-- `platform_operator`/`billing_operator`. The commands reject a non-member role with 409
-- `unknown_role` and the evaluator's member join filters on `kind = 'member'`, so a staff
-- code (`support:*`, `billing:*`) cannot reach a membership even through a hand-written
-- row — the database backs the "built-ins are platform identities" rule the way the
-- `is_builtin` check backs the scope rule.
--
-- `role.unique (org_id, id)` is the TL T3 tenant key, kept even though built-in rows
-- carry a null `org_id`: it is what a future composite FK to a custom role needs, and the
-- 02.6.1-T2 lint reads the declared key, not the rows. `role_permission.role_id ->
-- role (id)` is deliberately a SINGLE-column FK (feature plan §4): built-in roles have
-- `org_id is null`, so a composite `(org_id, role_id) -> (org_id, id)` reference could
-- never match them — the catalogue is org-agnostic by design. That one FK is the reason
-- `role_permission` carries a `CompositeForeignKey` exemption in
-- tests/Persistence.IntegrationTests/SchemaConventionScan.cs; nothing else about these
-- three tables is exempt.
--
-- `permission.code` is the primary key (there is no surrogate id: the string IS the
-- identity, 00 §2), and the check constraints keep its two halves from drifting from the
-- `resource:action` shape `PermissionAttribute` enforces in C#. `role` and
-- `role_permission` carry the 00 §7 audit columns and the shared
-- `set_updated_at_and_version` trigger of 0004, so a custom-role edit is concurrency-safe
-- from the first day.
--
-- The seed reads the JSON embedded in the `do` block: `permission` rows first (the FK
-- target), then the nine built-in roles of 00 §2, then their grants. Ids are `uuidv7()`
-- (PostgreSQL 18, 00 §7). `SeedMatrixTests.RolesMatchOverview` diffs what this seed
-- produced against `contracts/permissions.json`, so editing the JSON without a new
-- migration fails a test instead of silently desynchronising the database — a migration
-- is history and is never edited to follow the file.

create table if not exists public.permission (
  code text not null,
  resource text not null,
  action text not null,
  description text not null,
  sensitive boolean not null default false,
  reserved boolean not null default false,
  tenant_assignable boolean not null default true,
  constraint permission_pkey primary key (code),
  constraint permission_code_format_check check (code ~ '^[a-z_]+:[a-z_]+$'),
  constraint permission_resource_format_check check (resource ~ '^[a-z][a-z0-9_]*$'),
  constraint permission_action_format_check check (action ~ '^[a-z][a-z0-9_]*$'),
  -- 00 §2: a permission IS `resource:action`, so the parts can never contradict the code.
  constraint permission_code_matches_parts_check check (code = resource || ':' || action)
);

create table if not exists public.role (
  id uuid not null,
  org_id uuid,
  code text not null,
  name text not null,
  is_builtin boolean not null,
  kind text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint role_pkey primary key (id),
  constraint role_org_id_key unique (org_id, id),
  constraint role_org_code_key unique (org_id, code),
  constraint role_code_format_check check (code ~ '^[a-z][a-z0-9_]*$'),
  constraint role_name_length_check check (char_length(name) between 1 and 64),
  -- Who may hold the role (W7-SEC-03): `member` is the only kind a `membership_role`
  -- may reference — the five member roles of 00 §2 plus every custom role of the
  -- flagged 02.2.1-T2 commands — while `service` (ci_producer, agent) belongs to a
  -- service token and `staff` (platform_operator, billing_operator) to an Evidgate
  -- staff account with no standing membership. The seed carries the kind from
  -- `contracts/permissions.json`; `GrantRole`/`InviteMember` reject a non-member role
  -- with the existing 409 `unknown_role`, and the member path of the evaluator joins
  -- on it, so attaching a staff or service role to a membership grants nothing even if
  -- a row were written around the commands.
  constraint role_kind_check check (kind in ('member', 'service', 'staff')),
  -- A built-in role is exactly the platform row: `org_id is null` is what the catalogue
  -- policy keys on, so the flag cannot disagree with the scope (02 §4.5).
  constraint role_builtin_matches_scope_check check (is_builtin = (org_id is null))
);

-- Built-in role codes are unique platform-wide. `role_org_code_key` cannot enforce that
-- on its own: PostgreSQL treats NULLs as distinct, so two built-in rows could share a
-- code. The partial index below is the catalogue's real uniqueness rule.
create unique index if not exists role_builtin_code_key on public.role (code) where org_id is null;

-- 00 §5.7 P4: `GET /v1/roles` walks the keyset (created_at, id) over the catalogue plus
-- the caller's own org rows; this index is the covering order for both.
create index if not exists role_created_id_idx on public.role (created_at, id);

create table if not exists public.role_permission (
  id uuid not null,
  org_id uuid,
  role_id uuid not null,
  permission_code text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint role_permission_pkey primary key (id),
  constraint role_permission_org_id_key unique (org_id, id),
  constraint role_permission_role_permission_key unique (role_id, permission_code),
  constraint role_permission_role_fk foreign key (role_id) references public.role (id),
  constraint role_permission_permission_fk foreign key (permission_code) references public.permission (code)
);

-- The shared concurrency trigger of 0004 (M1W3-01): `updated_at = now()` and
-- `version = old.version + 1` on every UPDATE, one trigger per table.
drop trigger if exists role_set_updated_at_and_version on public.role;
create trigger role_set_updated_at_and_version
  before update on public.role
  for each row
  execute function public.set_updated_at_and_version();

drop trigger if exists role_permission_set_updated_at_and_version on public.role_permission;
create trigger role_permission_set_updated_at_and_version
  before update on public.role_permission
  for each row
  execute function public.set_updated_at_and_version();

alter table public.permission enable row level security;
alter table public.permission force row level security;
alter table public.role enable row level security;
alter table public.role force row level security;
alter table public.role_permission enable row level security;
alter table public.role_permission force row level security;

-- `permission`: platform catalogue, readable by every org (00 §5.4.1).
drop policy if exists permission_app_read on public.permission;
create policy permission_app_read on public.permission
  for select
  to evidgate_app
  using (true);

-- Writable only by the migration owner (02 §4.5). One denial per write command: a
-- `for all` policy's USING clause also governs DELETE, so a single `for all … using
-- (true) with check (false)` policy would have left DELETE permitted.
drop policy if exists permission_app_insert_denied on public.permission;
create policy permission_app_insert_denied on public.permission
  for insert
  to evidgate_app
  with check (false);

drop policy if exists permission_app_update_denied on public.permission;
create policy permission_app_update_denied on public.permission
  for update
  to evidgate_app
  using (false)
  with check (false);

drop policy if exists permission_app_delete_denied on public.permission;
create policy permission_app_delete_denied on public.permission
  for delete
  to evidgate_app
  using (false);

-- `role` / `role_permission`: read the catalogue and the org's own rows; write only the
-- org's own rows (built-ins are `org_id is null`, so the WITH CHECK can never match one).
drop policy if exists role_catalogue_read on public.role;
create policy role_catalogue_read on public.role
  for select
  to evidgate_app
  using (org_id is null or org_id = evidgate.current_org_id());

drop policy if exists role_tenant_insert on public.role;
create policy role_tenant_insert on public.role
  for insert
  to evidgate_app
  with check (org_id = evidgate.current_org_id());

drop policy if exists role_tenant_update on public.role;
create policy role_tenant_update on public.role
  for update
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists role_permission_catalogue_read on public.role_permission;
create policy role_permission_catalogue_read on public.role_permission
  for select
  to evidgate_app
  using (org_id is null or org_id = evidgate.current_org_id());

drop policy if exists role_permission_tenant_insert on public.role_permission;
create policy role_permission_tenant_insert on public.role_permission
  for insert
  to evidgate_app
  with check (org_id = evidgate.current_org_id()
              -- W7-SEC-05: the row's org must also be the referenced role's org, so the
              -- app role cannot attach a grant to a built-in role (`org_id is null`) and
              -- widen it for every member of the tenant. The tenant predicate stays the
              -- FIRST top-level AND operand: the isolation self-check of 02.6.1-T2
              -- recognises a scoped policy by that position.
              and exists (select 1
                            from public.role r
                           where r.id = role_id
                             and r.org_id = evidgate.current_org_id()));

drop policy if exists role_permission_tenant_update on public.role_permission;
create policy role_permission_tenant_update on public.role_permission
  for update
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id()
              -- The same tie for UPDATE: without it the hole would simply move here
              -- (`update role_permission set role_id = <built-in id>`).
              and exists (select 1
                            from public.role r
                           where r.id = role_id
                             and r.org_id = evidgate.current_org_id()));

-- The migration role needs its own policy on every table: `force row level security`
-- applies to the table owner too, so the seed's own statements and any later
-- expand/contract backfill would otherwise change zero rows without failing (explicit
-- and permissive on purpose — the alternative would be BYPASSRLS, which DECISIONS F5
-- rejects). The seed below runs as `evidgate_owner`, so these policies are load-bearing
-- for this very file.
drop policy if exists permission_owner_backfill on public.permission;
create policy permission_owner_backfill on public.permission
  for all
  to evidgate_owner
  using (true)
  with check (true);

drop policy if exists role_owner_backfill on public.role;
create policy role_owner_backfill on public.role
  for all
  to evidgate_owner
  using (true)
  with check (true);

drop policy if exists role_permission_owner_backfill on public.role_permission;
create policy role_permission_owner_backfill on public.role_permission
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- Grants (00 §5.4.3 least privilege). `permission` is read-only for the app role: the
-- INSERT/UPDATE/DELETE that 0001's default privileges granted on every new table are
-- revoked here, so the denial does not depend on the policies alone. `role` /
-- `role_permission` keep INSERT/UPDATE for the flagged custom-role commands of
-- 02.2.1-T2 (org-scoped rows only, per the policies above) and lose DELETE: a role is
-- archived, never hard-deleted. `evidgate_operator` keeps the same SELECT-without-a-policy
-- posture as 0004/0005 — the grant-gated operator policy is 02.8.1-T1.1's, and until it
-- lands the operator reads zero rows here.
grant select on public.permission to evidgate_app;
revoke insert, update, delete on public.permission from evidgate_app;
grant select on public.permission to evidgate_operator;
revoke all on public.permission from evidgate_worker_sched;

grant select, insert, update on public.role to evidgate_app;
revoke delete on public.role from evidgate_app;
grant select on public.role to evidgate_operator;
revoke all on public.role from evidgate_worker_sched;

grant select, insert, update on public.role_permission to evidgate_app;
revoke delete on public.role_permission from evidgate_app;
grant select on public.role_permission to evidgate_operator;
revoke all on public.role_permission from evidgate_worker_sched;

-- Seed (02.2.1-T1.1): the exact content of `contracts/permissions.json`, embedded so the
-- migration is self-contained. `permission` first (the FK target), then the built-in
-- roles, then their grants. `on conflict` / `where not exists` make the block idempotent
-- for a database that already carries catalogue rows, and `uuidv7()` is the 00 §7 id.
do $seed$
declare
  catalogue jsonb := $catalogue$
{
  "version": 1,
  "comment": "Single source of every permission string (00 §2, 02 D2). `permissions[]` is the platform catalogue seeded into `public.permission`; `roles[]` are the built-in roles seeded into `public.role` / `public.role_permission` (owner 02.2.1-T1, migration 0010). `resource` + `action` must equal `code` (`resource:action`, lower_snake). `sensitive` marks a permission that gates secret-adjacent data or returns a plaintext secret once; `reserved` marks a catalogue-only string whose first consumer is a V2/V3 story (V1 has no endpoint or handler for it); `tenant_assignable=false` marks a code a member principal must never hold (T79): a custom role containing one is rejected 422 `permission_not_assignable`, and the evaluator filters them out of a member's grant set. The C# constants are generated from this file by scripts/generate-permissions.py; editing this file without regenerating fails scripts/check-permissions-drift.sh.",
  "roles": [
    {"code": "viewer", "name": "Viewer", "kind": "member", "description": "Read granted reports. No secrets: no `sensitive` permission and no non-assignable code (00 §2).", "permissions": ["artifact:read", "case:read", "decision:read", "organization:read", "policy:read", "project:read", "role:read", "run:read", "suite:read"]},
    {"code": "qa_engineer", "name": "QA engineer", "kind": "member", "description": "Drafts, runs and triage. Cannot change release policy (00 §2): no `policy:write`, no case/suite/fixture write (03 §4.5).", "permissions": ["ai_draft:request", "artifact:read", "artifact:read_sensitive", "artifact:write", "case:read", "decision:read", "draft:dry_run", "draft:merge_request", "draft:write", "healing:triage", "job:cleanup", "organization:read", "policy:read", "project:read", "role:read", "run:cancel", "run:create", "run:read", "suite:read"]},
    {"code": "test_maintainer", "name": "Test maintainer", "kind": "member", "description": "Approves tests and healing; owns case/suite/fixture writes (00 §2, 03 §4.5). Cannot approve a sensitive candidate they authored (DECISIONS §5.5) — a two-person rule enforced by the approval flow, not by a permission.", "permissions": ["agent_pool:read", "ai_draft:request", "artifact:read", "artifact:read_sensitive", "artifact:write", "case:read", "case:write", "decision:read", "draft:dry_run", "draft:merge_request", "draft:write", "environment:write", "fixture:write", "healing:approve", "healing:revoke", "job:cleanup", "organization:read", "policy:read", "project:read", "role:read", "run:cancel", "run:create", "run:read", "suite:read", "suite:write"]},
    {"code": "release_approver", "name": "Release approver", "kind": "member", "description": "Owns policy revisions and waivers (00 §2, 07). A waiver never changes a test result.", "permissions": ["artifact:read", "artifact:read_sensitive", "case:read", "decision:read", "organization:read", "policy:read", "policy:write", "project:read", "role:read", "run:read", "suite:read"]},
    {"code": "tenant_admin", "name": "Tenant admin", "kind": "member", "description": "Membership, integrations and quota (00 §2). Secret values are never readable: no `artifact:read_sensitive` and no `billing:*`. Policy stays with `release_approver` (the org creator holds both, T98).", "permissions": ["agent_pool:read", "agent_pool:write", "ai_draft:request", "artifact:read", "artifact:write", "audit:read", "case:read", "case:write", "ci_binding:write", "decision:read", "draft:dry_run", "draft:merge_request", "draft:write", "environment:write", "fixture:write", "healing:configure", "ingest_token:read", "ingest_token:write", "membership:read", "membership:write", "organization:read", "organization:write", "policy:read", "project:create", "project:read", "project:write", "role:read", "role:write", "run:cancel", "run:create", "run:read", "security_event:read", "service_identity:read", "service_identity:write", "sso:read", "sso:write", "suite:read", "suite:write", "support:approve", "support:read", "usage:read", "webhook:read", "webhook:write"]},
    {"code": "ci_producer", "name": "CI producer", "kind": "service", "description": "Service identity minted per CI job for exactly one project: {run:create, event:write, artifact:write} (FR-02-7, AC4). It cannot approve, read other projects or change policy.", "permissions": ["artifact:write", "event:write", "run:create"]},
    {"code": "agent", "name": "Agent", "kind": "service", "description": "Service identity per (tenant, pool) (V2, 02.5): claimed jobs and uploads in its pool only — no approvals, policy or other projects (00 §2).", "permissions": ["artifact:write", "event:write", "job:claim", "job:execute"]},
    {"code": "platform_operator", "name": "Platform operator", "kind": "staff", "description": "Evidgate staff. No standing tenant access: support access needs a scope + expiry grant the tenant approves (FR-02-13).", "permissions": ["support:grant", "support:read"]},
    {"code": "billing_operator", "name": "Billing operator", "kind": "staff", "description": "Evidgate staff. Reads the usage ledger and invoice data across tenants, audited; no artifact or test data access (00 §2, 12).", "permissions": ["billing:read", "billing:write"]}
  ],
  "permissions": [
    {"code": "agent_pool:read", "resource": "agent_pool", "action": "read", "description": "Read agent pool definitions and their capability match (V2, 05.7.3).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "agent_pool:write", "resource": "agent_pool", "action": "write", "description": "Create or change an agent pool (V2, 05.7.3).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "ai_draft:request", "resource": "ai_draft", "action": "request", "description": "Request an AI-generated case/suite draft (V2, 03.6.x). The draft is never gate-eligible.", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "artifact:read", "resource": "artifact", "action": "read", "description": "Read artifact metadata and obtain a short-lived download URL (00 §9.3).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "artifact:read_sensitive", "resource": "artifact", "action": "read_sensitive", "description": "Read artifact content classified `sensitive` (traces, DOM/a11y snapshots) on every path that can return it; each issuance is audited (00 §7, 06.2.1).", "sensitive": true, "reserved": false, "tenant_assignable": true},
    {"code": "artifact:write", "resource": "artifact", "action": "write", "description": "Request an upload URL and complete an upload for a run/attempt (00 §9.3).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "audit:read", "resource": "audit", "action": "read", "description": "Read and export the tenant audit log, support-grant actions included (00 §9.3, 02.7.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "billing:read", "resource": "billing", "action": "read", "description": "Read the usage ledger and invoice data across tenants (staff only, every read audited; 00 §2, 12).", "sensitive": true, "reserved": false, "tenant_assignable": false},
    {"code": "billing:write", "resource": "billing", "action": "write", "description": "Load price cards, adjust the ledger and issue statements on the operator ingress; adjustments and statements are two-person (12, T69).", "sensitive": true, "reserved": false, "tenant_assignable": false},
    {"code": "case:read", "resource": "case", "action": "read", "description": "Read the case catalog and case revisions (00 §9.3, 03).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "case:write", "resource": "case", "action": "write", "description": "Write case revisions in the catalog (03 §4.5).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "ci_binding:write", "resource": "ci_binding", "action": "write", "description": "Create, change or disable a project CI binding, including the one-time repository challenge (02.4.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "decision:read", "resource": "decision", "action": "read", "description": "Read a run's or release's exact evaluation revision and its reasons (00 §9.3, 07).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "draft:dry_run", "resource": "draft", "action": "dry_run", "description": "Run a sandbox dry run of a draft (V2, 03.6.1).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "draft:merge_request", "resource": "draft", "action": "merge_request", "description": "Open a pull/merge request from a draft, idempotent by (branch, commit) (V2, 03.6.1).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "draft:write", "resource": "draft", "action": "write", "description": "Create and edit authoring drafts (V2, 03.6.1). A draft is never gate-eligible.", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "environment:write", "resource": "environment", "action": "write", "description": "Create an immutable environment revision (00 §9.3, 05.1.2).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "event:write", "resource": "event", "action": "write", "description": "Send an event batch for a run and finalize a producer shard (00 §9.3). Service identities only: the CI producer or the agent (04, 05).", "sensitive": false, "reserved": false, "tenant_assignable": false},
    {"code": "fixture:write", "resource": "fixture", "action": "write", "description": "Write fixture revisions (03 §4.5). Reading a fixture reuses `suite:read` (T56).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "healing:approve", "resource": "healing", "action": "approve", "description": "Approve, reject or revoke a healing candidate, bound to candidate_hash + base commit (V2, 00 §8.7, 08).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "healing:configure", "resource": "healing", "action": "configure", "description": "Change the healing kill switch for the tenant/project; the most restrictive scope wins (00 §8.7, 08.6.3).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "healing:revoke", "resource": "healing", "action": "revoke", "description": "Revoke an approved candidate or a knowledge record, blocking future use (V2, 00 §8.7, 08).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "healing:triage", "resource": "healing", "action": "triage", "description": "Read incidents and candidates, label an incident and set the kill switch's read side (00 §2, 08). Human labels are the only ground truth.", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "ingest_token:read", "resource": "ingest_token", "action": "read", "description": "List project ingest-token metadata (id, name, prefix, status, last_used_at) — never the secret or its HMAC (02.4.2).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "ingest_token:write", "resource": "ingest_token", "action": "write", "description": "Create, rotate or revoke a project ingest token; the plaintext secret is returned exactly once (02.4.2).", "sensitive": true, "reserved": false, "tenant_assignable": true},
    {"code": "job:claim", "resource": "job", "action": "claim", "description": "Claim a queued job matching the agent's pool and capability (V2, 00 §9.3, 05.7).", "sensitive": false, "reserved": true, "tenant_assignable": false},
    {"code": "job:cleanup", "resource": "job", "action": "cleanup", "description": "Run a side-effecting job cleanup, e.g. abandoning a lease or purging a cancelled job's workspace (V2).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "job:execute", "resource": "job", "action": "execute", "description": "Heartbeat the current job lease and report its execution events (V2, 00 §9.3, 05.7).", "sensitive": false, "reserved": true, "tenant_assignable": false},
    {"code": "membership:read", "resource": "membership", "action": "read", "description": "Read org memberships and pending invites (02 §4.2, T59).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "membership:write", "resource": "membership", "action": "write", "description": "Invite, change the roles of or remove a member; the last-admin guard rejects removing the last tenant_admin (02.1.2).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "organization:read", "resource": "organization", "action": "read", "description": "Read the organizations the caller is a member of, and one organization's settings (02.1.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "organization:write", "resource": "organization", "action": "write", "description": "Create an organization and change its settings (slug, retention default, allowed email domains) (02.1.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "policy:read", "resource": "policy", "action": "read", "description": "Read policy revisions, waivers and quarantine entries — open to every member role, viewer included, so an exception is never hidden (T58, 07.1.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "policy:write", "resource": "policy", "action": "write", "description": "Create or activate a policy revision and create/approve/revoke a waiver (00 §2, 07).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "project:create", "resource": "project", "action": "create", "description": "Create a project in an org (org-level, not per-project; 00 §9.3, T21).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "project:read", "resource": "project", "action": "read", "description": "Read project settings such as retention and capture options (T21: every member role).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "project:write", "resource": "project", "action": "write", "description": "Change project settings such as retention and the artifact kind policy (T21: tenant_admin).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "role:read", "resource": "role", "action": "read", "description": "Read the permission catalogue and the built-in roles (any member; 02.2.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "role:write", "resource": "role", "action": "write", "description": "Create or change a custom role; built-in roles are immutable (409 builtin_role_immutable) and non-assignable codes are rejected (422 permission_not_assignable) (02.2.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "run:cancel", "resource": "run", "action": "cancel", "description": "Record a cancel request for a run; a cancelled run never becomes complete (00 §8.1, 05.4.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "run:create", "resource": "run", "action": "create", "description": "Upload a manifest, create a run, rerun or reprocess, and bind a run to a release (00 §9.3).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "run:read", "resource": "run", "action": "read", "description": "Read run state, the evidence revision, report comparisons and environment revisions (00 §9.3).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "security_event:read", "resource": "security_event", "action": "read", "description": "Read the org's security events: auth failures and support access in V1, injection attempts from V2 (00 §9.3, 11.5.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "service_identity:read", "resource": "service_identity", "action": "read", "description": "List agent service identities of the tenant (V2, 02.5).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "service_identity:write", "resource": "service_identity", "action": "write", "description": "Create, rotate or revoke an agent service identity (V2, 02.5).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "sso:read", "resource": "sso", "action": "read", "description": "Read the org's SAML/SSO connection (V3, 02.3.3).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "sso:write", "resource": "sso", "action": "write", "description": "Create, change or remove the org's SAML/SSO connection (V3, 02.3.3).", "sensitive": false, "reserved": true, "tenant_assignable": true},
    {"code": "suite:read", "resource": "suite", "action": "read", "description": "Read suite revisions, their diffs and the fixtures they reference (00 §9.3, T56).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "suite:write", "resource": "suite", "action": "write", "description": "Write suite revisions (03 §4.5). A published revision is immutable.", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "support:approve", "resource": "support", "action": "approve", "description": "Approve or revoke a support grant for the caller's org; approver ≠ requester (00 §9.3, 02.8.1).", "sensitive": true, "reserved": false, "tenant_assignable": true},
    {"code": "support:grant", "resource": "support", "action": "grant", "description": "Request operator access with scope, reason and expiry on the operator session (00 §9.3, 02.8.1).", "sensitive": true, "reserved": false, "tenant_assignable": false},
    {"code": "support:read", "resource": "support", "action": "read", "description": "Read support grants: the tenant's own grants, or the operator's own requests (T59, 02.8.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "usage:read", "resource": "usage", "action": "read", "description": "Read the org's usage summary (counters vs quota) and ledger (00 §9.3, 12).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "webhook:read", "resource": "webhook", "action": "read", "description": "Read outbound webhook endpoints and their delivery log, never the signing secret (T61, 07.6.2).", "sensitive": false, "reserved": false, "tenant_assignable": true},
    {"code": "webhook:write", "resource": "webhook", "action": "write", "description": "Create, change or delete an outbound webhook endpoint; https only, SSRF-blocked targets (T61, 07.6.2).", "sensitive": false, "reserved": false, "tenant_assignable": true}
  ]
}
$catalogue$::jsonb;
begin
  insert into public.permission (code, resource, action, description, sensitive, reserved, tenant_assignable)
  select p.code, p.resource, p.action, p.description, p.sensitive, p.reserved, p.tenant_assignable
    from jsonb_to_recordset(catalogue -> 'permissions')
      as p(code text, resource text, action text, description text,
           sensitive boolean, reserved boolean, tenant_assignable boolean)
  on conflict (code) do update
     set resource = excluded.resource,
         action = excluded.action,
         description = excluded.description,
         sensitive = excluded.sensitive,
         reserved = excluded.reserved,
         tenant_assignable = excluded.tenant_assignable;

  insert into public.role (id, org_id, code, name, is_builtin, kind)
  select uuidv7(), null, r.code, r.name, true, r.kind
    from jsonb_to_recordset(catalogue -> 'roles') as r(code text, name text, kind text, permissions jsonb)
   where not exists (
           select 1 from public.role ro where ro.org_id is null and ro.code = r.code);

  insert into public.role_permission (id, org_id, role_id, permission_code)
  select uuidv7(), null, ro.id, granted.code
    from jsonb_to_recordset(catalogue -> 'roles') as r(code text, name text, permissions jsonb)
    cross join lateral jsonb_array_elements_text(r.permissions) as granted(code)
    join public.role ro on ro.org_id is null and ro.code = r.code
   where not exists (
           select 1 from public.role_permission rp
            where rp.role_id = ro.id and rp.permission_code = granted.code);
end
$seed$;
