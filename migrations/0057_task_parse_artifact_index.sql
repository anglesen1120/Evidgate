-- 0057_task_parse_artifact_index.sql
--
-- WHY: 06.5.1's evidence freeze asks, per ready raw report of the run, whether an `artifact_parse`
-- task for that artifact has NOT finished yet — a parse verdict that is still owed (`state <> 'done'`)
-- is what keeps a report out of the frozen artifact set, so the freeze cannot count an artifact nobody
-- has scanned. `public.task` carries the artifact id inside the payload, the queue keeps every
-- finished row, and the freeze evaluates that anti-join once per ready artifact (up to
-- `FreezeRequest.MaxReadyArtifacts` = 50 000 in one call). Without a usable index the planner has no
-- key for the expression and no partial predicate to narrow with, so the anti-join becomes a
-- sequential scan of the whole queue per freeze — the cost 00 §5.7 P4 forbids on a per-row path.
--
-- WHAT: one partial expression index on `public.task` over `(payload ->> 'artifact_id')` restricted to
-- the rows the anti-join asks about (`task_type = 'artifact_parse' and state <> 'done'`). Every term of
-- the freeze's predicate is in the index definition, so the planner can serve the `NOT EXISTS` from
-- the index alone. `payload ->> 'artifact_id'` is `jsonb_object_field_text`, which is immutable and
-- therefore a legal index expression; the value is the lowercase "D" GUID
-- `CompleteArtifactUpload` writes through `JsonSerializer`, exactly the form `artifact.id::text`
-- produces, so the equality is a plain text comparison. The partial predicate is written exactly as
-- the freeze spells it: a query that names a different spelling (`state = 'queued'`, say) simply does
-- not match the predicate and falls back to its own plan instead of reading a wrong subset.
--
-- GRANTS: none. An index is not a relation a role can be granted on, `evidgate_app` already holds
-- `select` on `public.task` (0008), and the index changes no policy: `public.task` deliberately
-- carries NO row-level-security policy (0008: the claim scans every org), so tenant separation is the
-- explicit `org_id` predicate every statement on it must keep - including the freeze's anti-join.
--
-- LOCKS: `create index` (not `concurrently`, which cannot run inside the migrator's per-script
-- transaction) takes a SHARE lock on `public.task`: readers continue, writes queue behind it. The
-- build itself reads the queue's live rows, so `lock_timeout` bounds the wait for that lock and turns
-- a busy queue into a failed migration the operator retries rather than a stall of every task write.
-- `if not exists` plus the repeatable `set lock_timeout` make a re-run a no-op.
--
-- Idempotent: `create index if not exists` and no down migration — dropping the index would silently
-- return the freeze to a sequential scan of the queue, which is a performance regression, not a
-- rollback of behaviour.

set lock_timeout = '3s';

create index if not exists task_parse_artifact_idx
  on public.task ((payload ->> 'artifact_id'))
  where task_type = 'artifact_parse' and state <> 'done';

reset lock_timeout;
