-- 0063_authoring_manual_case_link.sql
--
-- WHY: a manual test case says what a tester must do; the catalog `test_case` says which automated test
-- proves it. The authoring screen links the two so an uncovered manual case is visible, and the link is
-- many-to-many in both directions (one case may be exercised by several tests, one test may cover several
-- cases). The link is an assertion by an author, not a derivation: it is written and removed explicitly by
-- the two link routes, never inferred from names or paths, and it carries no version snapshot — the case's
-- own `authoring_version` rows already record what the case said.
--
-- WHAT: the table, its keys and the same-project insert trigger, the reverse-lookup index behind the covered
-- filter, row level security and the role grants. A row is insert-or-delete and never updated (a re-link is
-- a second insert answered as a no-op by `manual_case_link_uq`), so the app role holds SELECT, INSERT and
-- DELETE but no UPDATE at all — a narrower shape than the append-only authoring tables of 0039, which are not
-- deletable by the app because nothing may erase history. Here the link itself is the whole fact.
--
-- REFERENCES: the case FK is composite on `(org_id, project_id, id)`, so a link can never hold a manual case
-- of another project or tenant. The catalog FK is `(org_id, test_case_id)` as 0020 scopes it, because
-- 03's table carries no same-project key to point at; the product rule "the test case is in the same
-- project" is therefore the `manual_case_link_same_project` trigger, the `manifest_same_project` shape of
-- 0024:178-180 rather than a widened 03 table. `linked_by_membership_id` is who asserted the link and is
-- nullable, exactly like the version log's author, because a service principal writes none.
--
-- ORDER: additive only — the table, its trigger, two indexes, two policies and grants. No reader or writer
-- of another table changes, so it migrates with the routes that use it. No down migration: dropping the
-- table would silently drop every author's coverage claim, which no rollback can restore.

set local lock_timeout = '5s';

create table if not exists public.manual_case_link (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  manual_test_case_id uuid not null,
  test_case_id uuid not null,
  linked_by_membership_id uuid,
  created_at timestamptz not null default now(),
  constraint manual_case_link_pkey primary key (id),
  -- The tenant key every table carries (TL T3): the composite-FK target of any later pointer to one link.
  constraint manual_case_link_org_id_key unique (org_id, id),
  -- The idempotency key: `on conflict … do nothing` on this pair is what makes a repeated link a 200
  -- `changed:false` instead of a second row, and two concurrent links of one pair collapse to one row.
  constraint manual_case_link_uq unique (org_id, project_id, manual_test_case_id, test_case_id),
  constraint manual_case_link_case_fk foreign key (org_id, project_id, manual_test_case_id)
    references public.manual_test_case (org_id, project_id, id),
  constraint manual_case_link_catalog_fk foreign key (org_id, test_case_id)
    references public.test_case (org_id, id),
  constraint manual_case_link_member_fk foreign key (org_id, linked_by_membership_id)
    references public.membership (org_id, id)
);

-- The covered filter and the list row's count/ids read one manual case's links in one index-only lookup;
-- `test_case_id` rides along in the leaf pages, so counting and aggregating need no heap visit.
create index if not exists manual_case_link_case_idx
  on public.manual_case_link (org_id, project_id, manual_test_case_id) include (test_case_id);

-- The other direction: every manual case one catalog test covers.
create index if not exists manual_case_link_catalog_idx
  on public.manual_case_link (org_id, test_case_id);

-- ---------------------------------------------------------------------------------------------------
-- The project half of the catalog reference. A referenced row that does not exist, or that sits in
-- another organization, is left to the foreign keys above; only "same tenant, other project" is this
-- trigger's question. The project probe keeps it quiet while a project row is being written in the same
-- transaction as its first link.
-- ---------------------------------------------------------------------------------------------------

create or replace function evidgate.manual_case_link_same_project() returns trigger
  language plpgsql
  as $$
  begin
    if exists (select 1 from public.test_case c
                where c.org_id = new.org_id and c.id = new.test_case_id and c.project_id <> new.project_id)
       and exists (select 1 from public.project p where p.org_id = new.org_id and p.id = new.project_id) then
      raise exception 'cross_project_reference: the test case belongs to another project' using errcode = '23514';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.manual_case_link_same_project() from public;
-- The trigger fires for whoever inserted the row, so the app role needs EXECUTE on it (the 0039
-- `authoring_version_immutable` grant).
grant execute on function evidgate.manual_case_link_same_project() to evidgate_app;

drop trigger if exists manual_case_link_same_project on public.manual_case_link;
create trigger manual_case_link_same_project
  before insert on public.manual_case_link
  for each row execute function evidgate.manual_case_link_same_project();

-- ---------------------------------------------------------------------------------------------------
-- Row level security. ENABLE + FORCE, the tenant policy `for all to evidgate_app`, and one permissive
-- `evidgate_owner` policy: FORCE binds the owner too, so without it the tenant-erasure sweep would delete
-- zero links without failing (0010:237-260). No policy admits `evidgate_operator`, so its SELECT reaches no
-- row; 0001's default grant is decided explicitly below, the 0039 shape.
-- ---------------------------------------------------------------------------------------------------

alter table public.manual_case_link enable row level security;
alter table public.manual_case_link force row level security;

drop policy if exists manual_case_link_tenant on public.manual_case_link;
create policy manual_case_link_tenant on public.manual_case_link
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists manual_case_link_owner_backfill on public.manual_case_link;
create policy manual_case_link_owner_backfill on public.manual_case_link
  for all to evidgate_owner using (true) with check (true);

-- ---------------------------------------------------------------------------------------------------
-- Grants. 0001's default privileges give `evidgate_app` full DML on every table `evidgate_owner` creates in
-- `public`, so UPDATE is revoked before the minimum is granted: a link is never edited, and a re-link is an
-- insert. DELETE stays because route 21 unlinks, and the row is the fact itself rather than a history entry.
-- ---------------------------------------------------------------------------------------------------

revoke update on public.manual_case_link from evidgate_app;
grant select, insert, delete on public.manual_case_link to evidgate_app;
grant select on public.manual_case_link to evidgate_operator;
revoke all on public.manual_case_link from evidgate_worker_sched;
