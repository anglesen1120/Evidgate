-- 0132_authoring_requirement.sql
-- Why: a project without a repository still needs something its test cases can be traced to. A requirement
--   (epic, user story, acceptance criterion, spec section) is a project-scoped identity whose text changes
--   over time; every change is kept as an immutable version so a later link can pin the exact wording.
-- What: public.requirement (the mutable identity and its head pointer) and public.requirement_version (the
--   append-only body history), their composite tenant keys, the parent-cycle guard, the version immutability
--   guard, FORCE RLS policies and minimal grants. Links, tracker columns and routes arrive with later tasks;
--   `tracker_item_id` is deliberately absent.
-- Order: after 0131; the migrator refuses a gap, so 0130 and 0131 must land first.
-- Lock: only new objects are created, so no existing table is locked.
-- Why no foreign key from the head pointer to the version: the identity row and its first version are each
--   other's parent, so the pair could only be written with a deferred key, and every generic fixture that
--   writes one table alone would then fail at commit. `head_version_no` is instead moved only by the single
--   writer in the same transaction that inserts the version it names (a rollback undoes both), and the
--   `(org_id, requirement_id, version_no)` key below makes a second version with the same number impossible.
-- Purge order for the owner erasure path: requirement_version rows first, then requirement rows (the version
--   key to the identity is immediate). The app role holds no DELETE on either table, so an ordinary delete
--   is the soft `deleted_at` stamp and the history stays readable.
set local lock_timeout = '5s';

create table if not exists public.requirement (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  kind text not null,
  title text not null,
  external_ref text,
  feature_node_id uuid,
  parent_requirement_id uuid,
  head_version_no integer not null,
  deleted_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint requirement_pkey primary key (id),
  constraint requirement_org_id_key unique (org_id, id),
  constraint requirement_project_id_key unique (org_id, project_id, id),
  constraint requirement_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  -- Both optional references are pinned to the same project by the composite key.
  constraint requirement_feature_node_fk foreign key (org_id, project_id, feature_node_id)
    references public.feature_node (org_id, project_id, id),
  constraint requirement_parent_fk foreign key (org_id, project_id, parent_requirement_id)
    references public.requirement (org_id, project_id, id),
  constraint requirement_kind_check
    check (kind in ('user_story', 'acceptance_criterion', 'epic', 'spec_section', 'other')),
  constraint requirement_title_check check (char_length(title) between 1 and 200),
  -- The same character rule as test_case.requirement_ref (0068): a label, never a join key.
  constraint requirement_external_ref_check
    check (external_ref is null
           or (external_ref = btrim(external_ref)
               and char_length(external_ref) between 1 and 128
               and external_ref ~ '^[A-Za-z0-9._:/#-]+$')),
  constraint requirement_head_version_no_check check (head_version_no >= 1),
  constraint requirement_parent_not_self check (parent_requirement_id is null or parent_requirement_id <> id)
);

-- The project list and the tree read live rows in creation order.
create index if not exists requirement_project_list_idx
  on public.requirement (org_id, project_id, created_at, id)
  where deleted_at is null;

-- Children of one parent; also the lookup the composite parent key needs when a parent row is purged.
create index if not exists requirement_parent_idx
  on public.requirement (org_id, project_id, parent_requirement_id)
  where parent_requirement_id is not null;

-- The lookup the composite feature-node key needs when a node row is purged.
create index if not exists requirement_feature_node_idx
  on public.requirement (org_id, project_id, feature_node_id)
  where feature_node_id is not null;

create table if not exists public.requirement_version (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  requirement_id uuid not null,
  version_no integer not null,
  body text not null,
  content_hash text not null,
  source text not null,
  author_membership_id uuid,
  reason text,
  created_at timestamptz not null default clock_timestamp(),
  constraint requirement_version_pkey primary key (id),
  constraint requirement_version_org_id_key unique (org_id, id),
  -- The target of a later link pin: the version provably belongs to this requirement.
  constraint requirement_version_requirement_id_key unique (org_id, requirement_id, id),
  -- One number per requirement: the writer's duplicate-number refusal, and the history read's index.
  constraint requirement_version_no_key unique (org_id, requirement_id, version_no),
  constraint requirement_version_requirement_fk foreign key (org_id, project_id, requirement_id)
    references public.requirement (org_id, project_id, id),
  -- Nullable author: a CI or agent principal has no membership row, and MATCH SIMPLE then leaves it unenforced.
  constraint requirement_version_membership_fk foreign key (org_id, author_membership_id)
    references public.membership (org_id, id),
  constraint requirement_version_no_check check (version_no >= 1),
  -- The application normalizes to NFC before counting; char_length counts code points like the UI counter.
  constraint requirement_version_body_check check (char_length(body) <= 20000),
  constraint requirement_version_content_hash_check check (content_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint requirement_version_source_check check (source in ('manual', 'import', 'tracker', 'ai')),
  constraint requirement_version_reason_check check (reason is null or char_length(reason) between 1 and 500)
);

-- Membership lookup of the composite key when a membership row is purged.
create index if not exists requirement_version_member_idx
  on public.requirement_version (org_id, author_membership_id)
  where author_membership_id is not null;

drop trigger if exists requirement_set_updated_at_and_version on public.requirement;
create trigger requirement_set_updated_at_and_version
  before update on public.requirement
  for each row execute function public.set_updated_at_and_version();

-- A reparent may not close a loop. Concurrent reparents inside one project are serialized by a
-- transaction-scoped advisory lock, otherwise two moves that are each valid alone could together form a cycle.
-- The walk reads through the caller's row level security, which is the scope the rule is defined over.
create or replace function evidgate.requirement_parent_acyclic() returns trigger
  language plpgsql
  as $$
  declare
    ancestor uuid;
    steps integer := 0;
  begin
    if tg_op = 'INSERT' or new.parent_requirement_id is null
       or new.parent_requirement_id is not distinct from old.parent_requirement_id then
      return new;
    end if;

    perform pg_advisory_xact_lock(hashtextextended('requirement_tree:' || new.org_id || ':' || new.project_id, 0));

    ancestor := new.parent_requirement_id;
    while ancestor is not null loop
      if ancestor = new.id then
        raise exception 'requirement_parent_cycle: the new parent is a descendant of the requirement'
          using errcode = '23514';
      end if;
      steps := steps + 1;
      if steps > 10000 then
        raise exception 'requirement_parent_cycle: the ancestor chain is too deep to be a tree'
          using errcode = '23514';
      end if;
      select r.parent_requirement_id into ancestor
        from public.requirement r
       where r.org_id = new.org_id and r.project_id = new.project_id and r.id = ancestor;
    end loop;
    return new;
  end
  $$;

revoke all on function evidgate.requirement_parent_acyclic() from public;
grant execute on function evidgate.requirement_parent_acyclic() to evidgate_app;

drop trigger if exists requirement_parent_acyclic on public.requirement;
create trigger requirement_parent_acyclic
  before insert or update of parent_requirement_id on public.requirement
  for each row execute function evidgate.requirement_parent_acyclic();

-- A version is append-only for every role: UPDATE raises always, DELETE raises except for the owner's erasure
-- path (current_user is evidgate_owner inside an owner-owned purge function).
create or replace function evidgate.requirement_version_immutable() returns trigger
  language plpgsql
  as $$
  begin
    if tg_op = 'UPDATE' then
      raise exception 'requirement_version_immutable: a requirement version is append-only (UPDATE)'
        using errcode = '23514';
    elsif current_user <> 'evidgate_owner' then
      raise exception 'requirement_version_immutable: only the owner erasure path deletes a version (DELETE)'
        using errcode = '23514';
    end if;
    return old;
  end
  $$;

revoke all on function evidgate.requirement_version_immutable() from public;
grant execute on function evidgate.requirement_version_immutable() to evidgate_app;

drop trigger if exists requirement_version_immutable on public.requirement_version;
create trigger requirement_version_immutable
  before update or delete on public.requirement_version
  for each row execute function evidgate.requirement_version_immutable();

-- Row level security: ENABLE + FORCE, the tenant policy for the app role and the owner policy the erasure
-- path needs (FORCE binds the owner, so without it the purge would delete zero rows without failing).
alter table public.requirement enable row level security;
alter table public.requirement force row level security;

drop policy if exists requirement_tenant on public.requirement;
create policy requirement_tenant on public.requirement
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists requirement_owner_backfill on public.requirement;
create policy requirement_owner_backfill on public.requirement
  for all to evidgate_owner using (true) with check (true);

alter table public.requirement_version enable row level security;
alter table public.requirement_version force row level security;

drop policy if exists requirement_version_tenant on public.requirement_version;
create policy requirement_version_tenant on public.requirement_version
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists requirement_version_owner_backfill on public.requirement_version;
create policy requirement_version_owner_backfill on public.requirement_version
  for all to evidgate_owner using (true) with check (true);

-- Grants: 0001's default privileges give the app role full DML on every new table, so the revoke comes first.
-- The identity row is soft-deleted and updated per column; the version log is insert-only.
revoke update, delete on public.requirement from evidgate_app;
grant select, insert on public.requirement to evidgate_app;
grant update (kind, title, external_ref, feature_node_id, parent_requirement_id, head_version_no, deleted_at)
  on public.requirement to evidgate_app;
grant select on public.requirement to evidgate_operator;
revoke all on public.requirement from evidgate_worker_sched;

revoke update, delete on public.requirement_version from evidgate_app;
grant select, insert on public.requirement_version to evidgate_app;
grant select on public.requirement_version to evidgate_operator;
revoke all on public.requirement_version from evidgate_worker_sched;
