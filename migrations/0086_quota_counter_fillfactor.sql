-- 0086_quota_counter_fillfactor.sql
--
-- WHY: `quota_counter` is the quota gate's write hot path: every admitted call updates the counter row
-- in place (`used`, and `warned_at` once). `updated_at` and `version` are rewritten by the shared trigger
-- of 0004. None of those columns is in any index key or index predicate, so each update can be a heap-only (HOT) update, but only while the page has room for the new tuple
-- version. At the default fillfactor of 100 a full page pushes the new version to another page, which
-- adds an index entry per index per update. `fillfactor = 80` keeps that room for inserts and makes
-- page pruning start earlier, so dead versions are reclaimed before the page fills, as 0045 did for
-- `session` and 0047 does for `usage_rollup`.
--
-- EXISTING PAGES: the setting is catalog-only. The pruning threshold follows it at once, but the insert
-- headroom applies only to pages written after this file runs; pages the table already holds keep their
-- fill until their rows are rewritten or the table is rebuilt. No rewrite
-- (`vacuum full`, `cluster`) is done here: it would hold ACCESS EXCLUSIVE on the gate's table, the table is
-- small (one row per org, project, period and metric), and updates leave dead versions that vacuum
-- reclaims, so the pages are reused and the headroom appears as they are.
--
-- LOCKS: `alter table … set (storage parameter)` takes SHARE UPDATE EXCLUSIVE, which does not block reads
-- or writes; `lock_timeout` bounds the wait for it. A re-run sets the same value, so it is a no-op.
-- No down migration: `alter table public.quota_counter reset (fillfactor)` restores the default and
-- loses no data.

set local lock_timeout = '5s';

alter table public.quota_counter set (fillfactor = 80);
