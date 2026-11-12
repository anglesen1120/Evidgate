-- 0104_platform_worker_role.sql
--
-- The outbox dispatcher and the task queue machinery get their own role,
-- `evidgate_worker`, and `evidgate_app` keeps only the INSERT that a producer needs. Until this
-- file, `evidgate_app` held full DML on `public.task` (0008) and SELECT plus the column-scoped
-- UPDATE on `public.outbox` (0006/0009), so the role that serves tenant requests could also read,
-- rewrite and delete every org's queued work. After it, the seat of the queue is a grant.
--
-- THE MATRIX (least privilege; no role appears with a privilege it is not listed with).
--
--   | Role                   | Object                              | Privileges                          |
--   | ---------------------- | ----------------------------------- | ----------------------------------- |
--   | evidgate_worker        | schema public                       | USAGE                               |
--   | evidgate_worker        | public.task                         | SELECT, INSERT, UPDATE, DELETE      |
--   | evidgate_worker        | public.outbox                       | SELECT, UPDATE (dispatched_at)      |
--   | evidgate_app           | public.task                         | INSERT                              |
--   | evidgate_app           | public.outbox                       | INSERT                              |
--   | evidgate_app           | public.task_in_scope                | SELECT                              |
--   | evidgate_app           | public.outbox_in_scope              | SELECT                              |
--   | evidgate_app           | public.outbox_task_in_scope         | SELECT                              |
--   | evidgate_app           | evidgate.list_dead_letter_tasks     | EXECUTE                             |
--   | evidgate_app           | evidgate.replay_dead_letter_task    | EXECUTE                             |
--   | evidgate_operator      | any object above                    | none                                |
--   | evidgate_worker_sched  | any object above                    | none                                |
--   | PUBLIC / other roles   | any object above                    | none                                |
--
-- WHY EACH PRIVILEGE, FROM THE STATEMENTS THAT NEED IT (nothing here is wider than the code):
--   * worker `SELECT, UPDATE, DELETE, INSERT` on `task` — the claim (`TaskQueueSql.ClaimBatch`,
--     `ReclaimExpiredLeases`, `DeadLetterExpiredLeases`), the completion transitions
--     (`TaskLeaseWriter`: state, locked_until, attempts, available_at, last_error), the enqueue
--     (`TaskQueueWriter`, the dispatcher's INSERT … SELECT), the queue-state gauge
--     (`TaskQueueSql.QueueStateSnapshot`) and the retention delete (`TaskSweepTaskHandler.SweepSql`)
--     all run as this role. UPDATE is table-wide because a lease transition writes most columns of
--     the row it holds; the row's identity and the identity columns are protected by the guard
--     predicates of those statements, not by a column grant (a claim whose lease expired matches
--     no row).
--   * worker `SELECT` on `outbox` + `UPDATE (dispatched_at)` — the dispatcher's claim locks and
--     reads whole rows (`OutboxDispatchSql.ClaimUndispatched`, `EnqueueTasks`) and stamps exactly
--     one column (`MarkDispatched`); the column-scoped grant is the 0009 posture, kept for the
--     reason 0009 gives: the mechanism may move a committed row, never rewrite what work it is or
--     for which tenant.
--   * worker INSERT on `task` and no INSERT on `outbox` — the dispatcher creates the task rows;
--     outbox rows are produced by the business transaction as `evidgate_app` (0006) and by nothing
--     else.
--   * app INSERT on both tables — the producer path: `OutboxWriter.InsertSql` inside the tenant
--     transaction, and `TaskQueueWriter.EnqueueSql` when a command writes a platform task on its
--     own transaction (the cancel-kill row). Both are plain INSERT, and neither uses RETURNING:
--     PostgreSQL requires SELECT on every column a RETURNING list names, which is why the
--     INSERT-only shape is a rule here, not an accident.
--   * worker EXECUTE — none. No statement the machinery issues calls an `evidgate` function:
--     `make_interval`, `now`, `uuidv7`, `hashtext` and `pg_try_advisory_xact_lock` are
--     `pg_catalog`, and the EXECUTE check on the `task` row's
--     `public.set_updated_at_and_version()` trigger function happens when the trigger is created,
--     not when a role fires it (0008's probe on postgres:18.6). Granting it would be wider than
--     the statements need.
--
-- THE VIEWS. `task` and `outbox` carry no row level security on purpose (0006/0008: the claim and
-- the dispatch scan every org). Three module reads still need to ask the queue "is this work still
-- owed" from inside a tenant transaction: `FreezeSql.SelectReadyArtifacts`,
-- `ParseRawReportTask.Deferred`'s deferred probe and `ReconcileDeadlineSql.AttemptStates`. They read
-- owner-owned `security_barrier` views filtered by `org_id = evidgate.current_org_id()`, and the app
-- role therefore holds no privilege on the tables themselves: the views are the whole tenant path.
-- The predicate is the same primitive every policy uses, so the boundary is exactly today's: the app
-- role can set `evidgate.org_id` itself (the tenant-scope primitive does it for every request), and
-- what it must never do is write, delete or re-read the queue.
--
--   * `public.task_in_scope` — `id, org_id, task_type, state, idempotency_key, artifact_id`.
--   * `public.outbox_in_scope` — `id, org_id, task_type, dispatched_at, idempotency_key, artifact_id`.
--   * `public.outbox_task_in_scope` — one outbox row left-joined to the task it became
--     (`task.idempotency_key = 'outbox:' || outbox.id` and the same org): `id, org_id,
--     idempotency_key, dispatched_at, task_state, last_error`. The join lives inside the barrier
--     because a caller-side join on a derived key is a caller-side predicate again, and only this
--     view can keep the task side of the join under the tenant filter.
--
-- No view exposes `payload`. `artifact_id` is the one payload field the module reads use, so it is
-- projected as `payload ->> 'artifact_id'` — the same immutable expression `0057`'s partial indexes
-- `task_parse_artifact_idx` and `outbox_parse_artifact_idx` are built on — and the anti-joins compare
-- `artifact_id = a.id::text`. Both operators are leakproof, so the planner may still push the
-- equality below the barrier and use those indexes; a caller qual on `payload` itself would have to
-- be evaluated inside the barrier and would fall back to a scan of the tenant's rows.
-- `last_error` is exposed where it is read (the deferred probe compares it) and nowhere else.
-- `security_barrier` keeps every other caller predicate from being pushed below the tenant filter.
--
-- THE TWO OPERATOR DEFINERS. The dead-letter page and the replay are operator paths, and the Api
-- must not hold the worker credential, so both are owner `SECURITY DEFINER` functions in `evidgate`:
--   * `list_dead_letter_tasks(queue, after_created_at, after_id, limit)` — the keyset page over
--     `task_dead_org_idx`, for the caller's own org only.
--   * `replay_dead_letter_task(task_id, actor)` — the guarded `dead_letter → queued` transition
--     (`attempts` reset to 0, `locked_until` cleared, `last_error` kept: a replay is a re-queue, not
--     an acquittal) plus one chained `audit_log` row through the shared
--     `evidgate.audit_append_user_event` writer, in the same transaction, naming the operator the
--     route authenticated. A null actor is refused rather than written unattributed, the posture
--     the healing-switch route takes for the same reason.
-- Both read the org from `evidgate.current_org_id()` and never from an argument, so a caller cannot
-- name another tenant: a row of another org answers `not_found`, exactly as an absent row does. Both
-- refuse to run without a tenant scope (23514) — the opposite guard to `0052`'s
-- system-scope definer, because a tenant row is what they project. `EXECUTE` is `evidgate_app`'s
-- alone.
--
-- LOCKS. `GRANT`/`REVOKE` take ACCESS SHARE on each named table plus a catalogue update of its ACL:
-- no row is read, rewritten or scanned, writers are not blocked, and the lock is released at commit
-- of this file's own transaction. Creating or replacing a view parses its definition, which opens
-- every source table with ACCESS SHARE — the mode an ordinary reader takes, so only DDL that
-- rewrites those tables waits, never a writer — and holds it to commit; the replace locks the view
-- itself, and dropping one locks that view alone. `CREATE INDEX` is the one statement that also
-- blocks writers: it is not `CONCURRENTLY` (which cannot run inside the migrator's per-script
-- transaction) and takes SHARE on `public.task`, so inserts, updates and deletes queue behind its
-- build until this file commits.
-- `lock_timeout` bounds every wait, so a busy queue fails the migration with 55P03 (rerun at a
-- quieter moment) instead of stalling dispatchers behind a queued lock.
--
-- OPEN OWNER DECISIONS (recorded here, deliberately not changed by this file):
--   * The list leaves no audit row: it reads the caller's own org and changes nothing, so there is
--     nothing to attribute. Whether an operator read of dead letters should append a read event is
--     the owner's call; the replay, the only write on this path, does audit.
--   * Platform dead-letter tasks (`org_id` null: `idempotency.purge`, `task.sweep`) are neither
--     listable nor replayable through these routes. Both take the org from
--     `evidgate.current_org_id()` and a null-org row never equals it, so the exclusion is a
--     consequence of the tenant-scoped design, not a filter someone can drop. Whether operators get
--     a platform-queue page of their own is a separate decision.
--
-- ROLLBACK (application first: no build may depend on the two roles once this is undone):
--   drop function if exists evidgate.replay_dead_letter_task(uuid, uuid);
--   drop function if exists evidgate.list_dead_letter_tasks(text, timestamptz, uuid, integer);
--   drop view if exists public.outbox_task_in_scope;
--   drop view if exists public.outbox_in_scope;
--   drop view if exists public.task_in_scope;
--   drop index if exists public.task_dead_org_idx;
--   grant select, insert, update, delete on public.task to evidgate_app;
--   grant select on public.outbox to evidgate_app;
--   grant update (dispatched_at) on public.outbox to evidgate_app;
--   revoke all on public.task, public.outbox from evidgate_worker;
--   revoke usage on schema public from evidgate_worker;
--   -- and only then, once no connection names it: drop role evidgate_worker;
-- Reverting the grants is what restores the 0006/0008/0009 posture; dropping the role is last
-- because PostgreSQL refuses to drop a role that still holds a privilege somewhere. No row is
-- transformed either way, so the rollback loses nothing but the split; the index is dropped because
-- nothing but the reverted page named it, and `task_dead_idx` still serves the 0008 ordering.
--
-- One file runs in one transaction, so the role, the grants, the views and the definers are never
-- observable apart. `create role` is guarded like 0001, every privilege statement is re-stated, the
-- views are dropped and re-created with the same columns and the index is `if not exists`, so a
-- re-run is a no-op.
set local lock_timeout = '5s';

-- The role itself, created in 0001's shape: NOLOGIN (the bootstrap grants LOGIN + a password, never
-- a committed file), NOINHERIT (a membership granted for convenience never silently adds authority).
do $$
begin
  if not exists (select 1 from pg_catalog.pg_roles where rolname = 'evidgate_worker') then
    create role evidgate_worker nologin noinherit;
  end if;
end
$$;

-- Fail loudly on a role that would break the split, in 0001's shape: a migration role cannot correct
-- SUPERUSER or BYPASSRLS (only a superuser may), so an environment that ships one must not serve.
do $$
declare
  offenders text;
begin
  select string_agg(rolname || ' (' || concat_ws(', ',
           case when rolsuper then 'SUPERUSER' end,
           case when rolbypassrls then 'BYPASSRLS' end,
           case when rolcreatedb then 'CREATEDB' end,
           case when rolcreaterole then 'CREATEROLE' end,
           case when rolreplication then 'REPLICATION' end) || ')', '; ')
    into offenders
    from pg_catalog.pg_roles
   where rolname = 'evidgate_worker'
     and (rolsuper or rolbypassrls or rolcreatedb or rolcreaterole or rolreplication);

  if offenders is not null then
    raise exception 'evidgate_worker carries unsafe attributes (00 §5.4.3): %', offenders;
  end if;
end
$$;

-- Schema reach: USAGE is all the worker needs (it names tables, it never creates one), and 0001's
-- CREATE revoke from PUBLIC stays the only statement about DDL here.
revoke all on schema public from evidgate_worker;
grant usage on schema public to evidgate_worker;

-- The two mechanism tables. Every revoke is re-stated first, so a database whose ACL was widened by
-- hand converges to the matrix above. The broad revoke is the app, operator and PUBLIC posture:
-- 0001's default privileges hand the app role DML and the operator SELECT on every table the
-- migration role creates in `public`, so both are removed from the queue tables by name, and the
-- column-scoped `UPDATE (dispatched_at)` 0009 granted is revoked explicitly because a table-level
-- REVOKE does not remove a column-level grant.
revoke all on public.task, public.outbox from public, evidgate_app, evidgate_operator;
revoke update (dispatched_at) on public.outbox from evidgate_app;

-- The worker keeps the mechanism's privileges; `evidgate_worker_sched` keeps the "no table
-- privilege" posture 0006/0008/0088 give it.
revoke all on public.task from evidgate_worker, evidgate_worker_sched;
grant select, insert, update, delete on public.task to evidgate_worker;

revoke all on public.outbox from evidgate_worker, evidgate_worker_sched;
grant select on public.outbox to evidgate_worker;
grant update (dispatched_at) on public.outbox to evidgate_worker;

-- What the producer keeps: the plain INSERT both writer paths issue, and nothing else. Neither uses
-- `RETURNING`, because PostgreSQL requires SELECT on every column a `RETURNING` list names.
grant insert on public.task, public.outbox to evidgate_app;

-- The tenant read path: owner-owned, security_barrier, projected to the columns the three module
-- statements name, so a later query cannot pivot a "does this work still exist" read into a payload
-- read. The column list is part of that contract, so each view is dropped first: a database that
-- applied an earlier shape of this file converges instead of failing 42P16, and dropping a view
-- takes only that view's lock (creating the replacement parses its sources, so those get the
-- ACCESS SHARE named in LOCKS above).
drop view if exists public.task_in_scope;
drop view if exists public.outbox_in_scope;
drop view if exists public.outbox_task_in_scope;

create view public.task_in_scope
  with (security_barrier = true) as
select t.id,
       t.org_id,
       t.task_type,
       t.state,
       t.idempotency_key,
       t.payload ->> 'artifact_id' as artifact_id
  from public.task t
 where t.org_id = evidgate.current_org_id();

create view public.outbox_in_scope
  with (security_barrier = true) as
select o.id,
       o.org_id,
       o.task_type,
       o.dispatched_at,
       o.idempotency_key,
       o.payload ->> 'artifact_id' as artifact_id
  from public.outbox o
 where o.org_id = evidgate.current_org_id();

-- The keyed join stays inside the barrier: the dispatcher derives a task's idempotency key from the
-- outbox row's id, so the pairing is an expression and a caller-side join on it would be a caller
-- predicate again. `dispatched_at` is the outbox half, `task_state` the task half (null while the
-- row is undispatched, or once retention deleted the task — dispatch inserts the task and stamps the
-- row in one transaction, so those are the only two ways to see a null state).
create view public.outbox_task_in_scope
  with (security_barrier = true) as
select o.id,
       o.org_id,
       o.idempotency_key,
       o.dispatched_at,
       t.state as task_state,
       t.last_error
  from public.outbox o
  left join public.task t
    on t.idempotency_key = 'outbox:' || o.id::text
   and t.org_id = o.org_id
 where o.org_id = evidgate.current_org_id();

-- 0001's default privileges give `evidgate_app` DML and `evidgate_operator` SELECT on every relation
-- the migration role creates in `public`, and a view is such a relation, so these revokes are
-- load-bearing rather than a formality (0088's precedent). The operator's SELECT has to go: a view
-- whose predicate reads a setting the caller can set itself is not a gate, and the audited operator
-- read path is the definer below, not a standing grant.
revoke all on public.task_in_scope
  from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant select on public.task_in_scope to evidgate_app;

revoke all on public.outbox_in_scope
  from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant select on public.outbox_in_scope to evidgate_app;

revoke all on public.outbox_task_in_scope
  from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant select on public.outbox_task_in_scope to evidgate_app;

-- The operator page's index. `task_dead_idx` (0008) is ordered `(created_at, id)` across every org;
-- the page always names one org, so on that index it walks other orgs' dead letters up to the
-- cursor. `task_dead_org_idx` puts the org first and keeps the keyset order behind it, so a page is
-- one ordered range probe from the cursor. The partial predicate is written exactly as the function
-- spells it, `if not exists` makes a re-run a no-op, and the build takes SHARE on `public.task`
-- (LOCKS above: writers wait for it, readers do not).
create index if not exists task_dead_org_idx
  on public.task (org_id, created_at, id)
  where state = 'dead_letter';

-- The operator dead-letter page. Keyset on `(created_at, id)`, which is `task_dead_org_idx`'s order
-- behind the org it names, for the caller's org only; `p_limit` is clamped to the endpoint's ceiling
-- plus the one extra row it fetches to answer "is there a next page" without a count (200 + 1). The
-- cursor is a pair, because half a keyset would silently restart the page. It writes no audit row
-- because it writes nothing: the page is a read of the caller's own org, and the state change an
-- operator can make from it is the replay, which appends one below.
-- lint: audit-deferred operator-read-only evidgate.list_dead_letter_tasks
create or replace function evidgate.list_dead_letter_tasks(
  p_queue text,
  p_after_created_at timestamptz,
  p_after_id uuid,
  p_limit integer)
returns table (
  id uuid,
  org_id uuid,
  queue text,
  task_type text,
  idempotency_key text,
  attempts integer,
  max_attempts integer,
  last_error text,
  created_at timestamptz)
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_org uuid;
begin
  v_org := evidgate.current_org_id();
  if v_org is null then
    raise exception 'list_dead_letter_tasks needs a tenant scope: evidgate.current_org_id() is null'
      using errcode = '23514';
  end if;

  if (p_after_created_at is null) <> (p_after_id is null) then
    raise exception 'the dead-letter cursor is a pair: created_at and id travel together'
      using errcode = '22023';
  end if;

  return query
    select t.id,
           t.org_id,
           t.queue,
           t.task_type,
           t.idempotency_key,
           t.attempts,
           t.max_attempts,
           t.last_error,
           t.created_at
      from public.task t
     where t.org_id = v_org
       and t.state = 'dead_letter'
       and (p_queue is null or t.queue = p_queue)
       and (p_after_created_at is null
            or (t.created_at, t.id) > (p_after_created_at, p_after_id))
     order by t.created_at, t.id
     limit least(greatest(coalesce(p_limit, 50), 1), 201);
end
$$;

-- The replay. The update is `TaskQueueSql.ReplayDeadLetter`'s predicate plus the tenant filter: the
-- row's state is the capability, so a second replay of the same id answers `not_dead_letter` and
-- nothing is written. The audit row is appended before the function returns, in the same
-- transaction, so a failed append rolls the replay back with it. The update waits on the row lock a
-- claiming consumer may hold, so `lock_timeout` bounds it (0101's definer posture): a busy row
-- answers 55P03 instead of pinning the route's connection behind the claim.
create or replace function evidgate.replay_dead_letter_task(
  p_task_id uuid,
  p_actor uuid)
returns table (
  outcome text,
  available_at timestamptz)
language plpgsql
security definer
set search_path = ''
set lock_timeout = '2000'
as $$
declare
  v_org uuid;
  v_state text;
  v_queue text;
  v_task_type text;
  v_attempts integer;
  v_max_attempts integer;
  v_available_at timestamptz;
begin
  v_org := evidgate.current_org_id();
  if v_org is null then
    raise exception 'replay_dead_letter_task needs a tenant scope: evidgate.current_org_id() is null'
      using errcode = '23514';
  end if;

  if p_actor is null then
    raise exception 'replay_dead_letter_task needs the acting operator: the audit row names it'
      using errcode = '22004';
  end if;

  update public.task t
     set state = 'queued',
         attempts = 0,
         available_at = now(),
         locked_until = null
   where t.id = p_task_id
     and t.org_id = v_org
     and t.state = 'dead_letter'
  returning t.queue, t.task_type, t.attempts, t.max_attempts, t.available_at
       into v_queue, v_task_type, v_attempts, v_max_attempts, v_available_at;

  if v_available_at is null then
    select t.state
      into v_state
      from public.task t
     where t.id = p_task_id
       and t.org_id = v_org;

    return query
      select case when v_state is null then 'not_found' else 'not_dead_letter' end,
             null::timestamptz;
    return;
  end if;

  -- Attribution is the route's authenticated operator, passed in: the function refuses a null one
  -- rather than writing a replay nobody owns. The metadata values are the post-replay row, all
  -- strings by the shared writer's contract, and name no payload.
  perform evidgate.audit_append_user_event(
    'task.dead_letter_replay',
    v_org,
    p_actor,
    'task',
    p_task_id::text,
    pg_catalog.jsonb_build_object(
      'task_id', p_task_id::text,
      'queue', v_queue,
      'task_type', v_task_type,
      'attempts', v_attempts::text,
      'max_attempts', v_max_attempts::text,
      'state', 'queued'),
    null,
    null);

  return query
    select 'replayed'::text, v_available_at;
end
$$;

-- EXECUTE is the app role's alone: the shared writer and the audit chain stay owner-only (0101
-- already revoked them from every runtime role), and neither definer is reachable by the sweep role,
-- which has no business reading a tenant's queue.
revoke all on function evidgate.list_dead_letter_tasks(text, timestamptz, uuid, integer)
  from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant execute on function evidgate.list_dead_letter_tasks(text, timestamptz, uuid, integer)
  to evidgate_app;

revoke all on function evidgate.replay_dead_letter_task(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant execute on function evidgate.replay_dead_letter_task(uuid, uuid) to evidgate_app;
