-- 0036_orchestration_run_state_grant.sql
--
-- Owner: 05.3.1-T1.1. Two additions to `run` (migration 0031) for the 00 §8.1 run state machine
-- (feature plan 05.3.1 §4/§6, rulings T82/T3, contract C4):
--
--   * `current_evidence_revision_id uuid null` — the run's current evidence revision. 05.3.1's
--     `GET /v1/runs/{r}` exposes it (T1.4) and 05.3.2's reconciler sets it when a verdict lands.
--     05.2.1's `run` predates evidence revisions (`evidence_revision` is created by 05.3.2-T5), so
--     the column lands here WITHOUT its foreign key: [DECISION] (coordinator, 2026-10-13) 0036 adds
--     the nullable column and no FK; 05.3.2-T5 adds the composite
--     `(org_id, current_evidence_revision_id) -> evidence_revision (org_id, id)` key once that table
--     exists. A FK to a table that does not exist cannot be created, so the plan §4 snippet that
--     shows it is aspirational for the deploy order (plan §11), not part of this file.
--   * the app-role column grant `update (state)` — the one column 05.3.1's guarded transition writes
--     (`TransitionRunStateCommandHandler`'s single `update public.run set state = @state`). 0031
--     revoked table-level UPDATE on `run` and granted SELECT/INSERT only (0031:165-166), and 0034
--     granted `update (updated_at)` for the T91 `FOR SHARE` row lock. This file adds `state` alone
--     and nothing wider: `purpose`, `replay_grant_id`, `current_evidence_revision_id`,
--     `collection_deadline_at`, `late_window_minutes`, `ci_run_ref` and `components` stay outside the
--     app role's write grants, so a run's state is the only column the app role can move.
--     `version`/`updated_at` need no grant of their own: the shared `set_updated_at_and_version`
--     trigger (0004, attached to `run` by 0031:138-141) assigns them without a column privilege.
--
-- No policy, trigger or index: the new column is covered by 0031's table-scoped `run_tenant` /
-- `run_owner_backfill` policies and the `run_set_updated_at_and_version` trigger, and the state
-- machine's read is the primary-key `select … for no key update` (0031's `run_pkey`), so no index is added.
--
-- Idempotent like 0025/0027-0035: `add column if not exists` plus a repeated `grant`, so a re-run is
-- a no-op.

alter table public.run
  add column if not exists current_evidence_revision_id uuid;

grant update (state) on public.run to evidgate_app;
