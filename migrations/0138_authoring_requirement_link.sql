-- 0138_authoring_requirement_link.sql
-- Why: coverage is only honest when a link says which wording of the requirement it was made against. A link
--   pins one immutable requirement version, so a later edit shows up as a stale pin instead of silently
--   vouching for text nobody re-read.
-- What: public.requirement_link, one row per (requirement, target) where the target is exactly one checklist
--   item or one manual test case of the same project. Composite keys pin the project of the requirement and of
--   the target, and the version key pins the version to the same requirement. FORCE RLS, minimal grants.
--   Relinking to a newer version and dismissal arrive with a later migration that grants the columns they need.
-- Order: after 0137; the migrator refuses a gap, so 0136 and 0137 must land first.
-- Lock: only a new table is created, and the foreign keys reference tables that no writer holds a long lock on;
--   the 5 s lock_timeout below makes a blocked deploy fail instead of queueing behind a writer.
-- Why one row per (requirement, target) and not per version: a second pin of the same target would count one
--   case twice and let a stale pin hide behind a fresh one. Moving a pin is the later relink's update.
-- Why NULLS NOT DISTINCT: exactly one target column is NULL, and a plain key would never see two links of one target
--   collide. The key is immediate and not partial, so it is also a legal target for the isolation scan.
-- Purge order for the owner erasure path: requirement_link rows first, then requirement_version and requirement
--   (and before any checklist_item or manual_test_case row they name); every key here is immediate. The app
--   role holds no UPDATE, so an unlink is the one DELETE it may issue.
set local lock_timeout = '5s';

create table if not exists public.requirement_link (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  requirement_id uuid not null,
  requirement_version_id uuid not null,
  checklist_item_id uuid,
  manual_test_case_id uuid,
  -- Nullable: a CI or agent principal has no membership row, and MATCH SIMPLE then leaves the key unenforced.
  linked_by_membership_id uuid,
  created_at timestamptz not null default now(),
  constraint requirement_link_pkey primary key (id),
  constraint requirement_link_org_id_key unique (org_id, id),
  constraint requirement_link_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  constraint requirement_link_requirement_fk foreign key (org_id, project_id, requirement_id)
    references public.requirement (org_id, project_id, id),
  -- Names the requirement too, so the pinned version provably belongs to the linked requirement.
  constraint requirement_link_version_fk foreign key (org_id, requirement_id, requirement_version_id)
    references public.requirement_version (org_id, requirement_id, id),
  constraint requirement_link_item_fk foreign key (org_id, project_id, checklist_item_id)
    references public.checklist_item (org_id, project_id, id),
  constraint requirement_link_case_fk foreign key (org_id, project_id, manual_test_case_id)
    references public.manual_test_case (org_id, project_id, id),
  constraint requirement_link_membership_fk foreign key (org_id, linked_by_membership_id)
    references public.membership (org_id, id),
  constraint requirement_link_one_target_check
    check (num_nonnulls(checklist_item_id, manual_test_case_id) = 1),
  -- One link per requirement and target, whatever the version.
  constraint requirement_link_target_key
    unique nulls not distinct (org_id, project_id, requirement_id, checklist_item_id, manual_test_case_id)
);

-- The coverage read lists the links of one requirement in creation order.
create index if not exists requirement_link_requirement_idx
  on public.requirement_link (org_id, project_id, requirement_id, id);

-- Lookups of the composite keys when a target, a version or a membership is purged.
create index if not exists requirement_link_item_idx
  on public.requirement_link (org_id, project_id, checklist_item_id)
  where checklist_item_id is not null;

create index if not exists requirement_link_case_idx
  on public.requirement_link (org_id, project_id, manual_test_case_id)
  where manual_test_case_id is not null;

create index if not exists requirement_link_version_idx
  on public.requirement_link (org_id, requirement_id, requirement_version_id);

create index if not exists requirement_link_member_idx
  on public.requirement_link (org_id, linked_by_membership_id)
  where linked_by_membership_id is not null;

-- Row level security: ENABLE + FORCE, the tenant policy for the app role and the owner policy the erasure
-- path needs (FORCE binds the owner, so without it the purge would delete zero rows without failing).
alter table public.requirement_link enable row level security;
alter table public.requirement_link force row level security;

drop policy if exists requirement_link_tenant on public.requirement_link;
create policy requirement_link_tenant on public.requirement_link
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists requirement_link_owner_backfill on public.requirement_link;
create policy requirement_link_owner_backfill on public.requirement_link
  for all to evidgate_owner using (true) with check (true);

-- Grants: 0001's default privileges give the app role full DML on every new table, so the revoke comes first.
revoke update on public.requirement_link from evidgate_app;
grant select, insert, delete on public.requirement_link to evidgate_app;
revoke all on public.requirement_link from evidgate_operator;
revoke all on public.requirement_link from evidgate_worker_sched;
