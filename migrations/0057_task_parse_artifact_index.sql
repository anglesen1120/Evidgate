-- 0057_task_parse_artifact_index.sql
--
-- WHY: the evidence freeze leaves a `ready` raw report out of the frozen set until its parse verdict exists, and
-- reads two rows for that: an unfinished `artifact_parse` task row and an undispatched `artifact_parse` outbox row
-- (the window between the upload commit and the dispatcher's task insert). It evaluates both once per ready
-- artifact, up to `FreezeRequest.MaxReadyArtifacts` = 50 000 in one call, so each lookup must be an index probe and
-- never a scan of either mechanism table.
--
-- WHAT: two partial expression indexes keyed by `org_id` and the payload's artifact id, restricted to the rows the
-- freeze asks about, so both equality terms are index columns and neither table is read for rows outside the
-- predicate. `payload ->> 'artifact_id'` is `jsonb_object_field_text`, immutable and therefore a legal index
-- expression; the value is the lowercase "D" GUID `CompleteArtifactUpload` writes through `JsonSerializer`, exactly
-- the form `artifact.id::text` produces, so the equality is a plain text comparison. The partial predicates are
-- written exactly as the freeze spells them: a query naming a different spelling keeps its own plan instead of
-- reading a subset the predicate never promised.
--
-- GRANTS: none. An index is not a relation a role can be granted on, and `evidgate_app` already holds `select` on
-- both tables. Neither table carries a row-level-security policy (the dispatcher scans every org by design), so the
-- explicit `org_id` predicate stays the tenant isolation; the index repeats it as a column, it never replaces it.
--
-- LOCKS: `create index` (not `concurrently`, which cannot run inside the migrator's per-script transaction) takes a
-- SHARE lock on each table: readers continue, writes queue behind it. `set local lock_timeout` bounds that wait and
-- turns a busy table into a failed migration the operator retries, rather than a stall of every task write.
--
-- Idempotent: `create index if not exists` and no down migration — dropping an index would silently return the
-- freeze to a scan of a mechanism table, a performance regression rather than a rollback of behaviour.

set local lock_timeout = '5s';

create index if not exists task_parse_artifact_idx
  on public.task (org_id, (payload ->> 'artifact_id'))
  where task_type = 'artifact_parse' and state <> 'done';

create index if not exists outbox_parse_artifact_idx
  on public.outbox (org_id, (payload ->> 'artifact_id'))
  where task_type = 'artifact_parse' and dispatched_at is null;
