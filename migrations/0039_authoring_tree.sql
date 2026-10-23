-- 0039_authoring_tree.sql
--
-- Owner: 13.1.1-T1.1. The authoring tree of the G1 "Author" gate (feature plan 13.1.1 §4,
-- FR-13-1, 00 §5.4, DECISIONS A2/D1): the feature tree (`feature_node`), the checklist items
-- (`checklist_item`), the manual test cases (`manual_test_case`), the per-project case-code
-- counter (`authoring_code_counter`) and the append-only version log (`authoring_version`);
-- plus the case-code prefix on `project` (`project.case_prefix`, OI-13-101 as approved).
-- Every row is tenant-scoped: `org_id NOT NULL`, `unique (org_id, id)`, composite FKs only,
-- ENABLE + FORCE row level security with the tenant policy **and** the permissive
-- `evidgate_owner` policy on each of the five tables. No entry in
-- `contracts/rls-exceptions.txt`: none of the three exception kinds applies.
--
-- Lock levels and why they are safe here (plan §11 "Migration order"):
--   * `alter table public.project add column` — ACCESS EXCLUSIVE, metadata only.
--   * `create unique index project_org_case_prefix_uq` — SHARE: it blocks writes to `project`
--     for the duration of the build and nothing is built concurrently inside the Migrator's
--     one transaction (`migrations/README.md`, "Known limits"). It is created **before** the
--     backfill, on the still-nullable column (NULLs are distinct, so the empty state passes),
--     so any duplicate the loop would ever assign aborts the migration with 23505 here.
--   * the dedupe backfill — one `update` per project row that still has a NULL prefix. A
--     one-shot while `project` is small (one row per project); above ~10^5 projects it becomes
--     a lock-heavy rewrite and must be split into add-nullable -> backfill -> set-not-null,
--     keeping this loop as is (it reads only `org_id`/`slug` and writes one row per iteration,
--     so a batched rerun resumes on the rows that still have a NULL prefix).
--   * `alter column case_prefix set not null` and `add constraint project_case_prefix_format`
--     — ACCESS EXCLUSIVE with a validating scan of `project`, once, after the backfill.
-- Everything runs in the Migrator's single transaction, so these locks are held to commit.
--
-- The backfill and every later INSERT go through the same function (`evidgate.next_case_prefix`),
-- so the derivation cannot drift between the migration and the create path. Because the backfill
-- UPDATEs each project row, the 0005 `project_set_updated_at_and_version` trigger bumps
-- `version`/`updated_at` of the backfilled projects — intended: the prefix is part of the row.
--
-- [DECISION] deviations from the plan's DDL, each forced by a repository rule:
--   * `authoring_code_counter.id uuid` + `unique (org_id, id)`: `scripts/check-rls.py` (rule
--     `missing_unique_key`) and `tests/Persistence.IntegrationTests/SchemaConventionScan` require
--     every non-exempt business table to carry `unique (org_id, id)` (TL T3, 00 §5.4.6). The plan's
--     key `primary key (org_id, project_id)` is kept as the row's identity (it is what the
--     allocation UPSERT targets) and the surrogate `id` satisfies the convention — the 0033
--     `policy_activation` precedent, not an exception-list entry.
--   * `content_hash` is checked with `^sha256:[0-9a-f]{64}$` rather than the plan's
--     `like 'sha256:%'`: the value is the RFC 8785 canonical hash, and every other hash column in
--     the schema (0020/0021/0023) pins the full form.
--   * `manual_test_case.steps` is `not null`: the `jsonb_typeof(steps) = 'object'` check of the
--     plan makes a NULL steps column meaningless, and 13.1.1-T1.2's steps schema always writes a
--     document (`{"steps": []}` for a case without steps yet).

-- ---------------------------------------------------------------------------------------------------
-- Part 1: `project.case_prefix` — the stored source of every case code (OI-13-101, AC4).
-- ---------------------------------------------------------------------------------------------------

-- The value is stored, never recomputed from the slug, so a later grammar change (OI-02-01)
-- never rewrites an existing prefix and never invalidates an existing case code.
alter table public.project add column if not exists case_prefix text;

-- The stem rule, shared by the backfill and every later INSERT. The source slug grammar is
-- `^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$` (0012:25-29, which replaced 0005:42; OI-02-01 / D-A1 may
-- widen it and this derivation moves with it). Steps: drop every non-alphanumeric, take the first
-- four characters, upper-case, and prepend `P` when the result does not start with a letter
-- (`123-shop` -> `123S` -> `P123S`, `flashcards` -> `FLAS`, `shop-web` -> `SHOP`, `ab` -> `AB`).
-- The `length(stem) >= 2` branch is unreachable under this grammar (a slug carries at least two
-- alphanumerics) and stays as the guard for a widened one. The result is 2-5 characters and always
-- matches `project_case_prefix_format` below.
create or replace function evidgate.case_prefix_stem(slug text) returns text
  language sql
  immutable
  as $$
  select case when stem ~ '^[A-Z]' and length(stem) >= 2 then stem else 'P' || stem end
  from (select upper(substring(regexp_replace(slug, '[^a-z0-9]', '', 'g') from 1 for 4)) as stem) s
  $$;

revoke all on function evidgate.case_prefix_stem(text) from public;
grant execute on function evidgate.case_prefix_stem(text) to evidgate_app;

-- The unique index is created **before** the backfill (see the header).
create unique index if not exists project_org_case_prefix_uq
  on public.project (org_id, case_prefix);

-- One function so the backfill and every later INSERT agree. Collision-free and deterministic:
-- the oldest project of an org keeps the bare stem (`created_at, id` order); every other project
-- takes the first candidate that **no row of that org already holds** — the check runs against the
-- whole org, not against the stem's own partition, because two different stems can meet: `sho`,
-- `sho` and `sho2` stem to `SHO`, `SHO` and `SHO2`, and a per-stem counter would hand `SHO2` to
-- the second `sho` project and then `SHO2` again to `sho2` (same for `123`, `123` and `1232` ->
-- `P123`, `P1232`, `P12322`). The candidate loop therefore advances until the value is unused in
-- the org. The suffix truncates the stem so the value stays inside the 8-character format check;
-- candidates are finite (n <= 9 999 999) and an org that exhausts them raises instead of writing
-- a duplicate.
--
-- The per-org transaction-scoped advisory lock serialises two concurrent project creates in the
-- same org (PANEL-CHOICE Q1): the loser waits at the lock, then its `exists` probe — a statement of
-- this VOLATILE function, so it takes a fresh snapshot per statement under READ COMMITTED — sees
-- the winner's committed row and advances to the next candidate. With a *repeated* lock key per
-- org, two orgs never wait for each other. The function is SECURITY INVOKER on purpose: the probe
-- then runs under the caller's RLS, so it can only ever see the caller's own org — which is the
-- scope the uniqueness rule is defined over.
create or replace function evidgate.next_case_prefix(org uuid, slug text) returns text
  language plpgsql
  as $$
  declare
    stem text;
    cand text;
    n integer := 1;
  begin
    perform pg_advisory_xact_lock(hashtext('evidgate.case_prefix'), hashtext(org::text));

    stem := evidgate.case_prefix_stem(slug);

    loop
      cand := case when n = 1 then stem
                   else left(stem, 8 - length(n::text)) || n::text end;
      exit when not exists (
        select 1 from public.project q
         where q.org_id = org and q.case_prefix = cand);
      n := n + 1;
      if n > 9999999 then
        raise exception 'case_prefix exhausted for org %', org using errcode = '23505';
      end if;
    end loop;

    return cand;
  end
  $$;

revoke all on function evidgate.next_case_prefix(uuid, text) from public;
grant execute on function evidgate.next_case_prefix(uuid, text) to evidgate_app;

-- Deterministic assignment order, in one pass: each row takes the first candidate still free in
-- its org, so the result depends only on `(org_id, created_at, id)` and a rerun resumes on the rows
-- that are still NULL. A duplicate can only come from a bug in the candidate rule — and then the
-- unique index created above aborts the migration instead of sharing a prefix.
do $$
declare
  r record;
begin
  for r in select p.id, p.org_id, p.slug
             from public.project p
            where p.case_prefix is null
            order by p.org_id, p.created_at, p.id
  loop
    update public.project
       set case_prefix = evidgate.next_case_prefix(r.org_id, r.slug)
     where id = r.id;
  end loop;
end
$$;

alter table public.project alter column case_prefix set not null;

alter table public.project drop constraint if exists project_case_prefix_format;
alter table public.project add constraint project_case_prefix_format
  check (case_prefix ~ '^[A-Z][A-Z0-9]{1,7}$');

-- The create path needs no change (PANEL-CHOICE Q1): a BEFORE INSERT trigger fills a NULL prefix
-- from the slug through the same stem+dedupe function as the backfill, so `DevSeed` and every test
-- that inserts `(id, org_id, name, slug)` keeps working. `case_prefix` is stored and never
-- recomputed, so an UPDATE of `slug` leaves it alone — the trigger fires on INSERT only.
create or replace function evidgate.project_case_prefix_default() returns trigger
  language plpgsql
  as $$
  begin
    if new.case_prefix is null then
      new.case_prefix := evidgate.next_case_prefix(new.org_id, new.slug);
    end if;
    return new;
  end
  $$;

revoke all on function evidgate.project_case_prefix_default() from public;
grant execute on function evidgate.project_case_prefix_default() to evidgate_app;

drop trigger if exists project_case_prefix_default on public.project;
create trigger project_case_prefix_default
  before insert on public.project
  for each row execute function evidgate.project_case_prefix_default();

-- ---------------------------------------------------------------------------------------------------
-- Part 2: the tree — `feature_node` (13.1.1 §4).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.feature_node (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  parent_id uuid,
  kind text not null,
  title text not null,
  position integer not null,
  deleted_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint feature_node_pkey primary key (id),
  constraint feature_node_org_id_key unique (org_id, id),
  constraint feature_node_project_id_key unique (org_id, project_id, id),
  constraint feature_node_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  -- The parent is pinned to the same project: the composite key cannot match another project's node.
  constraint feature_node_parent_fk foreign key (org_id, project_id, parent_id)
    references public.feature_node (org_id, project_id, id),
  constraint feature_node_kind_check check (kind in ('module', 'feature', 'category', 'sub_category')),
  constraint feature_node_title_check check (char_length(title) between 1 and 200),
  constraint feature_node_position_check check (position >= 0),
  -- The depth ladder of AC1 is the only cycle guard (13.1.1 §Notes): an ancestor chain is strictly
  -- decreasing in `kind`, so a self-parent is the one cycle the database can refuse cheaply.
  constraint feature_node_parent_not_self check (parent_id is null or parent_id <> id)
);

-- Live siblings only: a tombstone keeps its old `position` and can never block a new node.
-- `nulls not distinct` (0007:78; PostgreSQL 15+, the image is 18.6) makes the NULL parent of two
-- root modules compare equal, so roots obey the same uniqueness rule as children.
-- A partial index cannot be DEFERRABLE, so the reorder renumber is two-phased (AC2).
create unique index if not exists feature_node_sibling_position_uq
  on public.feature_node (org_id, project_id, parent_id, position) nulls not distinct
  where deleted_at is null;

-- P4: the only list order of the tree (children of one parent, by position).
create index if not exists feature_node_tree_idx
  on public.feature_node (org_id, project_id, parent_id, position, id)
  where deleted_at is null;

-- ---------------------------------------------------------------------------------------------------
-- Part 3: `checklist_item` (13.1.1 §4, plan 13 §4.1, CR-13-1 states of §6).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.checklist_item (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  feature_node_id uuid not null,
  title text not null,
  description text,
  priority text not null,
  state text not null,
  source text not null,
  position integer not null,
  deleted_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint checklist_item_pkey primary key (id),
  constraint checklist_item_org_id_key unique (org_id, id),
  constraint checklist_item_project_id_key unique (org_id, project_id, id),
  constraint checklist_item_feature_node_fk foreign key (org_id, project_id, feature_node_id)
    references public.feature_node (org_id, project_id, id),
  constraint checklist_item_title_check check (char_length(title) between 1 and 500),
  constraint checklist_item_description_check check (description is null or char_length(description) <= 4000),
  constraint checklist_item_priority_check check (priority in ('critical', 'high', 'medium', 'low')),
  constraint checklist_item_state_check check (state in ('draft', 'active', 'retired')),
  constraint checklist_item_source_check check (source in ('manual', 'ai', 'import')),
  constraint checklist_item_position_check check (position >= 0)
);

-- P4: the item table of one node, filtered by state, in position order.
create index if not exists checklist_item_list_idx
  on public.checklist_item (org_id, project_id, feature_node_id, state, position, id)
  where deleted_at is null;

-- ---------------------------------------------------------------------------------------------------
-- Part 4: `manual_test_case` (13.1.1 §4, CR-13-1 states of §6).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.manual_test_case (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  checklist_item_id uuid not null,
  code text not null,
  title text not null,
  description text,
  precondition text,
  steps jsonb not null,
  priority text not null,
  state text not null,
  source text not null,
  deleted_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint manual_test_case_pkey primary key (id),
  constraint manual_test_case_org_id_key unique (org_id, id),
  -- The composite-FK target `manual_case_link_case_fk` references (13.1.1 §4, 13.1.1-T4).
  constraint manual_test_case_project_id_key unique (org_id, project_id, id),
  -- Idempotent re-import by code (AC15): one case per project and code.
  constraint manual_test_case_code_key unique (org_id, project_id, code),
  constraint manual_test_case_checklist_item_fk foreign key (org_id, project_id, checklist_item_id)
    references public.checklist_item (org_id, project_id, id),
  -- `<PREFIX>-<n>`: the prefix grammar of `project_case_prefix_format` plus a 3..10-digit number.
  constraint manual_test_case_code_check check (code ~ '^[A-Z][A-Z0-9]{1,7}-[0-9]{3,10}$'),
  constraint manual_test_case_title_check check (char_length(title) between 1 and 500),
  constraint manual_test_case_description_check check (description is null or char_length(description) <= 4000),
  constraint manual_test_case_precondition_check check (precondition is null or char_length(precondition) <= 4000),
  -- `contracts/schemas/authoring-steps.v1.json` owns the inner shape (13.1.1-T1.2); the database
  -- only insists that the document is an object.
  constraint manual_test_case_steps_check check (jsonb_typeof(steps) = 'object'),
  constraint manual_test_case_priority_check check (priority in ('critical', 'high', 'medium', 'low')),
  constraint manual_test_case_state_check check (state in ('draft', 'approved', 'retired')),
  constraint manual_test_case_source_check check (source in ('manual', 'ai', 'import'))
);

-- P4: the case table of one item, filtered by state, in code order — and the item side of the FK.
create index if not exists manual_test_case_list_idx
  on public.manual_test_case (org_id, project_id, checklist_item_id, state, code);

-- ---------------------------------------------------------------------------------------------------
-- Part 5: `authoring_code_counter` — one row per project, the case-code allocator (13.1.1 §4).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.authoring_code_counter (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  next_value bigint not null default 1,
  -- The row's identity and the target of the allocation UPSERT.
  constraint authoring_code_counter_pkey primary key (org_id, project_id),
  -- TL T3 / 00 §5.4.6 (see the header): the surrogate tenant key, not an exception-list entry.
  constraint authoring_code_counter_org_id_key unique (org_id, id),
  constraint authoring_code_counter_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  -- The allocator formats with at least three digits and caps one project at 10^9 codes; beyond it
  -- the handler answers `409 quota_exceeded` instead of writing a code the format check would refuse.
  constraint authoring_code_counter_next_value_check check (next_value between 1 and 1000000001)
);

-- ---------------------------------------------------------------------------------------------------
-- Part 6: `authoring_version` — the append-only version log (13.1.1 §4, AC5/AC6/AC14).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.authoring_version (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  entity_kind text not null,
  entity_id uuid not null,
  version_no integer not null,
  batch_id uuid not null,
  change_kind text not null,
  content jsonb not null,
  content_hash text not null,
  source text not null,
  author_membership_id uuid,
  reason text,
  ai_run_id uuid,
  created_at timestamptz not null default clock_timestamp(),
  constraint authoring_version_pkey primary key (id),
  constraint authoring_version_org_id_key unique (org_id, id),
  -- One version number per entity, per project: the writer's duplicate-version refusal (AC5).
  constraint authoring_version_entity_key
    unique (org_id, project_id, entity_kind, entity_id, version_no),
  -- Nullable author: a CI/agent principal has no membership row, and MATCH SIMPLE leaves the key
  -- unenforced then (the 0020:40 `owner_membership_id` shape).
  constraint authoring_version_membership_fk foreign key (org_id, author_membership_id)
    references public.membership (org_id, id),
  constraint authoring_version_entity_kind_check
    check (entity_kind in ('feature_node', 'checklist_item', 'manual_test_case')),
  constraint authoring_version_version_no_check check (version_no >= 1),
  constraint authoring_version_change_kind_check
    check (change_kind in ('create', 'update', 'delete', 'restore', 'import')),
  constraint authoring_version_content_check check (jsonb_typeof(content) = 'object'),
  constraint authoring_version_content_hash_check check (content_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint authoring_version_source_check check (source in ('manual', 'ai', 'import')),
  constraint authoring_version_reason_check check (reason is null or char_length(reason) between 1 and 500)
);

-- P4: the timeline keyset `(created_at desc, id desc)` of route 22 (13.1.1 §5) is scanned backwards
-- by this index; the batch index serves the batch grouping field and 13.1.2's batch reads.
create index if not exists authoring_version_timeline_idx
  on public.authoring_version (org_id, project_id, created_at desc, id desc);

create index if not exists authoring_version_batch_idx
  on public.authoring_version (org_id, project_id, batch_id);

-- ---------------------------------------------------------------------------------------------------
-- Part 7: the database-owned `updated_at`/`version` increment on the three mutable entity tables.
-- `authoring_code_counter` and `authoring_version` deliberately carry no `version` column (13.1.1 §4).
-- ---------------------------------------------------------------------------------------------------

drop trigger if exists feature_node_set_updated_at_and_version on public.feature_node;
create trigger feature_node_set_updated_at_and_version
  before update on public.feature_node
  for each row execute function public.set_updated_at_and_version();

drop trigger if exists checklist_item_set_updated_at_and_version on public.checklist_item;
create trigger checklist_item_set_updated_at_and_version
  before update on public.checklist_item
  for each row execute function public.set_updated_at_and_version();

drop trigger if exists manual_test_case_set_updated_at_and_version on public.manual_test_case;
create trigger manual_test_case_set_updated_at_and_version
  before update on public.manual_test_case
  for each row execute function public.set_updated_at_and_version();

-- ---------------------------------------------------------------------------------------------------
-- Part 8: `authoring_version` is append-only, one rule for every role (AC6, C6).
-- UPDATE raises for every role, `evidgate_owner` included; DELETE raises for every role except
-- `evidgate_owner`, whose erasure purge must be an owner-owned SECURITY DEFINER function
-- (`current_user = 'evidgate_owner'` inside it, the `evidgate.purge_expired_idempotency` shape of
-- 0007:137-159 under T80) — the hand-over to 06.4.4-T1.2 and 11.2.1, not a caller of this migration.
-- ---------------------------------------------------------------------------------------------------

create or replace function evidgate.authoring_version_immutable() returns trigger
  language plpgsql
  as $$
  begin
    if tg_op = 'UPDATE' then
      raise exception 'authoring_version_immutable: an authoring version is append-only (UPDATE)'
        using errcode = '23514';
    elsif current_user <> 'evidgate_owner' then
      raise exception 'authoring_version_immutable: only the owner erasure path deletes a version (DELETE)'
        using errcode = '23514';
    end if;
    return old;
  end
  $$;

revoke all on function evidgate.authoring_version_immutable() from public;
-- The trigger fires for whoever wrote the row, so the app role needs EXECUTE on it exactly as it
-- needs it on 0037's `forbid_run_identity_update` and 0004's `set_updated_at_and_version`.
grant execute on function evidgate.authoring_version_immutable() to evidgate_app;

drop trigger if exists authoring_version_immutable on public.authoring_version;
create trigger authoring_version_immutable
  before update or delete on public.authoring_version
  for each row execute function evidgate.authoring_version_immutable();

-- ---------------------------------------------------------------------------------------------------
-- Part 9: row level security. ENABLE + FORCE (DECISIONS F5) on the five tables, the tenant policy
-- `for all to evidgate_app`, and one permissive `evidgate_owner` policy each: `force row level
-- security` binds the owner too, so without it the backfill and 11.2.1's erasure sweep would update
-- and delete zero rows without failing (0010:237-260). The owner policy is `for all` because the
-- erasure path needs DELETE as the owner.
-- ---------------------------------------------------------------------------------------------------

alter table public.feature_node enable row level security;
alter table public.feature_node force row level security;

drop policy if exists feature_node_tenant on public.feature_node;
create policy feature_node_tenant on public.feature_node
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists feature_node_owner_backfill on public.feature_node;
create policy feature_node_owner_backfill on public.feature_node
  for all to evidgate_owner using (true) with check (true);

alter table public.checklist_item enable row level security;
alter table public.checklist_item force row level security;

drop policy if exists checklist_item_tenant on public.checklist_item;
create policy checklist_item_tenant on public.checklist_item
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists checklist_item_owner_backfill on public.checklist_item;
create policy checklist_item_owner_backfill on public.checklist_item
  for all to evidgate_owner using (true) with check (true);

alter table public.manual_test_case enable row level security;
alter table public.manual_test_case force row level security;

drop policy if exists manual_test_case_tenant on public.manual_test_case;
create policy manual_test_case_tenant on public.manual_test_case
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists manual_test_case_owner_backfill on public.manual_test_case;
create policy manual_test_case_owner_backfill on public.manual_test_case
  for all to evidgate_owner using (true) with check (true);

alter table public.authoring_code_counter enable row level security;
alter table public.authoring_code_counter force row level security;

drop policy if exists authoring_code_counter_tenant on public.authoring_code_counter;
create policy authoring_code_counter_tenant on public.authoring_code_counter
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists authoring_code_counter_owner_backfill on public.authoring_code_counter;
create policy authoring_code_counter_owner_backfill on public.authoring_code_counter
  for all to evidgate_owner using (true) with check (true);

alter table public.authoring_version enable row level security;
alter table public.authoring_version force row level security;

drop policy if exists authoring_version_tenant on public.authoring_version;
create policy authoring_version_tenant on public.authoring_version
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists authoring_version_owner_backfill on public.authoring_version;
create policy authoring_version_owner_backfill on public.authoring_version
  for all to evidgate_owner using (true) with check (true);

-- ---------------------------------------------------------------------------------------------------
-- Part 10: grants. 0001's default privileges give `evidgate_app` full DML on every table
-- `evidgate_owner` creates in `public`, so every block below revokes what the app must not do
-- *before* it grants the minimum (0027/0028/0031/0032 precedent). The three entity tables are
-- soft-deleted, never physically deleted by the app: `update` is granted (edits, the soft-delete
-- stamp and the restore), `delete` is not. `authoring_code_counter` is the one row set updated in
-- place. `authoring_version` is insert-only for the app — its UPDATE/DELETE are refused here and
-- by the trigger above, for every role.
-- ---------------------------------------------------------------------------------------------------

revoke delete on public.feature_node from evidgate_app;
grant select, insert, update on public.feature_node to evidgate_app;
grant select on public.feature_node to evidgate_operator;
revoke all on public.feature_node from evidgate_worker_sched;

revoke delete on public.checklist_item from evidgate_app;
grant select, insert, update on public.checklist_item to evidgate_app;
grant select on public.checklist_item to evidgate_operator;
revoke all on public.checklist_item from evidgate_worker_sched;

revoke delete on public.manual_test_case from evidgate_app;
grant select, insert, update on public.manual_test_case to evidgate_app;
grant select on public.manual_test_case to evidgate_operator;
revoke all on public.manual_test_case from evidgate_worker_sched;

revoke delete on public.authoring_code_counter from evidgate_app;
grant select, insert, update on public.authoring_code_counter to evidgate_app;
grant select on public.authoring_code_counter to evidgate_operator;
revoke all on public.authoring_code_counter from evidgate_worker_sched;

grant select, insert on public.authoring_version to evidgate_app;
revoke update, delete on public.authoring_version from evidgate_app;
grant select on public.authoring_version to evidgate_operator;
revoke all on public.authoring_version from evidgate_worker_sched;
