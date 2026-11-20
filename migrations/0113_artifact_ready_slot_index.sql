-- 0113_artifact_ready_slot_index.sql
--
-- WHY: the attempts page of one run (`GET /v1/runs/{r}/attempts`) counts that run's `ready` artifacts per
-- `(execution_key, attempt_no)` slot for the page's own rows. `artifact_run_idx (org_id, run_id) where
-- state <> 'deleted'` narrows the aggregate to one run, but it still returns every artifact of that run in
-- every state, so each page reads the whole run's artifact set and throws away all but the ready rows of the
-- page's slots. One run of a large suite therefore costs a scan of its entire artifact set per page, while the
-- answer needs only the ready rows of at most `limit` slots.
--
-- SHAPE: one partial index on the columns the aggregate filters, groups and counts by: `run_id` (the page's
-- tenant-scoped filter), then the slot pair the `in (select … from page)` comparison and the `group by` read.
-- `org_id` still leads because the tenant policy and the statement's RLS filter it, as in `artifact_run_idx`.
-- The predicate is the aggregate's `state = 'ready'`, so not-yet-ready and superseded rows never enter the
-- index and the grouping can be answered from it alone (`count(*)` is a key count). `execution_key` and
-- `attempt_no` are NOT NULL on `public.artifact` (0028), so every `ready` row carries its slot: no row is
-- missing from the index for a missing key and no NULLS clause is needed. No column, grant, policy or
-- statement changes; the page statements therefore keep serving their wire output unchanged.
--
-- LOCKS: a plain `create index` inside the migrator's single transaction (0038 and every index migration
-- since; `create index concurrently` cannot run there, see migrations/README.md "Known limits"). It takes a
-- SHARE lock on `public.artifact` for the build, which blocks writes to that table but not reads, and
-- `lock_timeout` bounds the wait for it — not the time the lock is held until commit. Build cost on the
-- pilot-size artifact set is a single-digit number of seconds [INFERENCE: not measured here; the table has no
-- production volume yet).
--
-- ORDER: migration first, then the application. Either order is safe for correctness — the previous page
-- statements read the same rows with or without the index, and the index only changes their plan — but the
-- build's SHARE lock on the artifact table is why it is not deployed in the middle of a reporting burst.
--
-- ROLLBACK: `drop index if exists public.artifact_run_slot_ready_idx;` — an index holds no data, so the
-- rollback loses nothing and needs no application change. No down migration: a re-run of this file is a
-- no-op (`if not exists`).

set local lock_timeout = '5s';

create index if not exists artifact_run_slot_ready_idx
  on public.artifact (org_id, run_id, execution_key, attempt_no)
  where state = 'ready';
