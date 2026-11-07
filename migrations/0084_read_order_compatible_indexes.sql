-- 0084_read_order_compatible_indexes.sql
--
-- WHY: four read statements ran on an index whose column order did not match what they filter or order
-- by, so each walked a wider set than its result:
--   * Usage summary, counter and roll-up probes: `quota_counter` and `usage_rollup` are filtered by
--     `(org_id, period)` (the page's named projects, the tenant roll-up total, the projects alert
--     counts). Their unique scope keys lead with `(org_id, coalesce(project_id, zero))`, so these probes
--     walked every counter or roll-up of the org and filtered the period afterwards.
--   * Usage summary page: the keyset `order by created_at, id` over `public.project` of one org. The
--     only index leading with org_id is `(org_id, status, created_at, id)`, which cannot return rows in
--     `(created_at, id)` order without a sort when no status is filtered.
--   * Run read: the release bindings of one run are read `where org_id, run_id order by created_at, id`
--     (capped), which `release_run_run_idx (org_id, run_id)` served only through a sort of the run's
--     bindings.
--
-- SHAPE: indexes only; no column, grant, policy or statement changes. The two period indexes end in
-- `project_id` because both statements also filter `project_id is not null` or `project_id = @project`;
-- `metric` is not added because the statements group by it over a per-period set of one org.
--
-- DROP: `release_run_run_idx (org_id, run_id)` is a strict prefix of the new
-- `release_run_run_created_idx (org_id, run_id, created_at, id)`, so every lookup it served (the run's
-- bindings, the composite FK `(org_id, run_id) -> run (org_id, id)` delete check) is served by the new
-- index. No statement, test or migration names the dropped index. A contract step that is safe in the
-- same migration because the new index is created first and an index has no reader besides the planner.
--
-- LOCKS: plain `create index` inside the migration transaction like 0038 and every index migration
-- since (`create index concurrently` is not available, migrations/README.md "Known limits"). The tables
-- are pilot-size; `lock_timeout` makes a busy table fail the migration for a retry instead of stalling
-- writes. `if not exists` / `drop … if exists` make a re-run a no-op. No down migration: dropping an
-- index loses no data and a rollback needs none.

set local lock_timeout = '5s';

create index if not exists quota_counter_org_period_idx
  on public.quota_counter (org_id, period, project_id);

create index if not exists usage_rollup_org_period_idx
  on public.usage_rollup (org_id, period, project_id);

create index if not exists project_org_created_id_idx
  on public.project (org_id, created_at, id);

create index if not exists release_run_run_created_idx
  on public.release_run (org_id, run_id, created_at, id);

drop index if exists public.release_run_run_idx;
