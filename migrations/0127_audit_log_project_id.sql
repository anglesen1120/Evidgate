-- 0127_audit_log_project_id.sql
--
-- WHY: the audit list has no way to answer "what happened in this project". audit_log gets a nullable project tag
-- the project dashboards filter on. It is a tag on the row, not part of the evidence: it is NOT in entry_hash
-- (AuditHashing's canonical fields are unchanged, so every existing chain still verifies), and the chain therefore
-- proves the action happened, not which project it carried. Rows written before this migration, and actions that
-- belong to no project, keep NULL and are never matched by a project filter.
--
-- No foreign key on purpose: audit rows must outlive the project they describe (like object_id, which is text and
-- unreferenced), and a reference from an append-only table would block or cascade a project deletion.
--
-- Append-only is unchanged: evidgate_app holds only INSERT and SELECT on audit_log (0015 revoked UPDATE, DELETE and
-- TRUNCATE), so the tag cannot be rewritten after the insert; a column added to the table is covered by that
-- table-level privilege.
--
-- The partial index serves the filtered keyset page (org_id, project_id, created_at desc, id desc) without a sort;
-- the unfiltered page keeps audit_log_org_created_id_idx.
--
-- LOCKS: the runner applies this file in one transaction (`WithTransactionPerScript`), so the ACCESS EXCLUSIVE lock
-- that ADD COLUMN takes is held until commit, through the CREATE INDEX scan too: reads and inserts on audit_log wait
-- for the whole file. Adding a nullable column without a default is a catalogue change, but the index build is a
-- scan of the table and `create index concurrently` is not available in one transaction (same known limit as
-- 0038_run_project_ref_idx.sql; migrations/README.md, "Known limits"). On a large audit_log, out of band and in this
-- order: (1) `alter table public.audit_log add column project_id uuid;` and commit (the index needs the column),
-- (2) `create index concurrently audit_log_org_project_created_id_idx` with this file's exact definition, outside a
-- transaction, then (3) run this file: its two `if not exists` clauses adopt the column and the index. 5 s lock_timeout.
set local lock_timeout = '5s';

alter table public.audit_log add column if not exists project_id uuid;

create index if not exists audit_log_org_project_created_id_idx
  on public.audit_log (org_id, project_id, created_at desc, id desc)
  where project_id is not null;
