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
-- The `quota_counter` index is partial (`project_id is not null`): both summary probes carry that predicate,
-- and the quota gate's tenant-level and project-scoped probes (which name the `coalesce(project_id, …)` scope
-- key and must stay on `quota_counter_scope_key`) cannot use it, so it never competes with the gate's hot path.
--
-- DROP: `release_run_run_idx (org_id, run_id)` is a strict prefix of the new
-- `release_run_run_created_idx (org_id, run_id, created_at, id)`, so every lookup it served (the run's
-- bindings, the composite FK `(org_id, run_id) -> run (org_id, id)` delete check) is served by the new
-- index. No statement or test depends on the dropped index by name (only 0072 created it). The swap is
-- atomic at commit, so the order of the statements below is free.
--
-- LOCKS: plain `create index` inside the migration transaction like 0038 and every index migration
-- since (`create index concurrently` is not available, migrations/README.md "Known limits"). The tables
-- are pilot-size; `lock_timeout` bounds each lock wait, not the time the earlier locks are held until
-- commit. The drop comes first so it never upgrades a SHARE lock this transaction holds on `release_run`
-- (a lock upgrade can deadlock with a concurrent bind), and the hottest write table `quota_counter` is
-- indexed last so its write lock is held the shortest. `if not exists` / `drop … if exists` make a re-run
-- a no-op. No down migration: dropping an index loses no data and a rollback needs none.

set local lock_timeout = '5s';

drop index if exists public.release_run_run_idx;

create index if not exists release_run_run_created_idx
  on public.release_run (org_id, run_id, created_at, id);

create index if not exists project_org_created_id_idx
  on public.project (org_id, created_at, id);

create index if not exists usage_rollup_org_period_idx
  on public.usage_rollup (org_id, period, project_id);

create index if not exists quota_counter_org_period_idx
  on public.quota_counter (org_id, period, project_id) where project_id is not null;
