# Dead-letter runbook

Status: **10.7.1-T1.1 written; T1.2/T2.2 staging drill by a non-author NOT yet executed (AC2/AC3 open).** The triage queries, the classification tree, the replay path and the escalation criteria below are grounded in this base's code and migrations; the replay has **not** been exercised by a second person on staging (T1.2) and the stuck-run half is a separate runbook ([stuck-run](stuck-run.md), T2.1/T2.2).

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified or blocked today · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Purpose, audience and rules

Purpose: let the **second on-call operator** (someone who did not build 01.5.1) take one dead-lettered task row from "the alert fired" to "the work ran, or the cause is documented and escalated", using read-only SQL plus one call to the shipped operator replay route.

Audience: the on-call operator on duty. Prerequisites:

- read access to the `task` and `outbox` tables with the `evidgate_app` role (§3.1 gives the exact statements and roles);
- the operator credential for the API, once 02.x authentication lands (today the operator gate denies every caller: `src/Evidgate.Api/OperatorIngress/OperatorGate.cs:30-33`) ⚠️;
- the alert that brought you here: the dead-letter-depth alert or the sink log line (§2). Neither names a person; there is no pager integration yet ⚠️ (10.3.1-T5).

Rules:

- **Never hand-write an `UPDATE` or `DELETE` on `public.task`.** The replay is the guarded operator path (§5); the only `DELETE` that exists on the table is the 30-day retention sweep (`migrations/0008_platform_task_queue.sql:79-81`, `src/BuildingBlocks/Persistence/Tasks/EvidgateSweeps.cs:84-96`, statement at `src/BuildingBlocks/Persistence/Tasks/SweepTaskHandlers.cs:114-124`). A hand update skips the state guard, the attempt reset and the audit expectations the route encodes.
- **Replay once, then look.** A second replay of the same row is a no-op by construction (§5.3), so the failure mode to avoid is not duplication — it is replaying a poison payload repeatedly instead of classifying it.
- Record ids, closed enum values, counts and timestamps only. Never copy a payload, a raw exception message beyond the redacted `last_error` head, or tenant names into a ticket (`ITaskHandler.cs:57-63` is the redaction rule the rows themselves follow).
- Nothing here is a statement about availability targets; the dead-letter depth number is measured, not an SLA (`plans/00-overview.md:445`).

## 2. Detection: the alert, the sink, and how to confirm it fired

The mechanism's alert seam is `ITaskAlertSink` (`src/BuildingBlocks/Persistence/Tasks/ITaskAlertSink.cs:41-69`), called once per **committed** `dead_letter` transition by both paths (the consumer's retry cap and the claimer's expired lease; `ITaskAlertSink.cs:42-44`).

| What | Where it is today | State |
| --- | --- | --- |
| Alert sink (log line per transition) | `LoggingTaskAlertSink`, log **event `2320`**, warning: task id, task type, queue, attempts, source (`src/BuildingBlocks/Persistence/Tasks/LoggingTaskAlertSink.cs:42-52`) | ✅ shipped, registered as the default (`src/BuildingBlocks/Persistence/Tasks/TasksServiceCollectionExtensions.cs:82`) |
| Counter every transition increments, whatever sink is registered | `task_dead_letter_total` on meter `Evidgate` (`src/BuildingBlocks/Persistence/Tasks/TaskQueueMetrics.cs:19` for the meter, `:27` for the counter; incremented *before* the sink is asked: `src/BuildingBlocks/Persistence/Tasks/TaskAlertNotifier.cs:40-42`) | ✅ shipped |
| Sink failure visibility | log event `2321`, error — the row is still the record (`TaskAlertNotifier.cs:59-63`) | ✅ shipped |
| Consumer dead-letter line (second signal, same transition) | log event `2306`, error (`src/BuildingBlocks/Persistence/Tasks/TaskConsumer.Logging.cs:67-71`) | ✅ shipped |
| Claimer lease-expiry dead-letter line | log event `2350`, warning, one line per batch (`src/BuildingBlocks/Persistence/Tasks/TaskClaimer.cs:288-290`) | ✅ shipped |
| **Dead-letter-depth alert rule and its runbook link** | planned: `infra/alerts/<env>/` does not exist on this base; rules are 10.3.1-T5.1 (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md:135`) and AC7 requires each rule to link a runbook (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:28`) | ⚠️ not shipped; the link target for that rule is **this file** |

How to confirm the sink fired, without a pager: search the Worker log for event `2320` (or `2306`/`2350` for the transition itself) filtered by the task id from §3.2. If the row is `dead_letter` and no `2320` line exists, the sink did not fire or was replaced — the counter still moved (`TaskAlertNotifier.cs:40-42`), and delivery is at-most-once by design: **the row is the record, the alert is only the notification** (`ITaskAlertSink.cs:55-60`). A captured sink that comes back up does not replay a lost alert; you recover it from the list query.

## 3. Triage

### 3.1 Roles, RLS and what a statement can read

`public.task` and `public.outbox` are **mechanism tables with no row level security on purpose**: the claim scan and the outbox dispatcher walk every org (`migrations/0008_platform_task_queue.sql:16-19`, `migrations/0006_platform_outbox.sql:11-19`, listed as exceptions in `contracts/rls-exceptions.txt:51-52`). Consequences for a triage query:

| Fact | Evidence |
| --- | --- |
| `evidgate_app` holds `select, insert, update, delete` on `public.task` | `migrations/0008_platform_task_queue.sql:155` |
| `evidgate_operator` holds **nothing** on `public.task` | `migrations/0008_platform_task_queue.sql:156` |
| `evidgate_app` holds `select` on `public.outbox` (added with the dispatcher) | `migrations/0009_platform_outbox_dispatch.sql:58` |
| `evidgate_operator` holds **nothing** on `public.outbox` | `migrations/0006_platform_outbox.sql:99` |
| So: run §3.2's `task`/`outbox` statements as `evidgate_app`; there is no tenant filter to set, and one statement reads every tenant's rows | the grants above; `src/BuildingBlocks/Persistence/Tasks/ListDeadLetterTasksQuery.cs:83-93` (the endpoint also reads over the app-role data source) |
| `public.run` **is** FORCE RLS, tenant policy `org_id = evidgate.current_org_id()`; `evidgate_app` sees one org only | `migrations/0031_orchestration_run.sql:143-150` |
| `evidgate_operator` has a `select` grant on `public.run` but **no policy**, so it reads zero rows until 02.8.1's grant-gated policy lands | `migrations/0031_orchestration_run.sql:152-155`; grant at `:167` |
| Cross-tenant `run` reads are possible only through the owner backfill policy (`evidgate_owner`) — the migration role, not an on-call credential ⚠️ | `migrations/0031_orchestration_run.sql:161-163` |

To read a run in its own tenant scope, open a read-only transaction and set the tenant GUC the helper function reads:

```sql
-- role: evidgate_app (RLS applies to public.run under this policy)
begin transaction read only;
set local evidgate.org_id = '<org uuid from task.org_id>';
-- … the §3.2 run statement …
commit;
```

`set local` is scoped to the transaction; `evidgate.current_org_id()` reads exactly that GUC (`migrations/0002_foundation_evidgate_schema.sql:30-33`). Never set it outside a transaction, and never set it to an org you have no incident for.

### 3.2 The statements

Q1 — the dead-letter list: the same table, predicate and ordering as the statement behind `GET /internal/operator/tasks/dead-letter` (`src/BuildingBlocks/Persistence/Tasks/TaskQueueSql.cs:195-210`), with the queue filter spelled out for psql instead of the endpoint's `@queue` parameter:

```sql
select id, org_id, queue, task_type, idempotency_key, attempts, max_attempts, last_error, created_at
  from public.task
 where state = 'dead_letter'          -- one of: queued, active, retry, dead_letter, done
   and ('<queue>' = '' or queue = '<queue>')
 order by created_at, id
 limit 50;
```

Served by `task_dead_idx` (`migrations/0008_platform_task_queue.sql:136`); the state literal set is pinned by `task_state_check` (`:113`); `state`, `attempts`, `max_attempts`, `last_error` are the columns at `:103-108`.

Q2 — dead-letter depth by queue and type, to see whether the row is an incident or a singleton:

```sql
select queue, task_type, count(*) as depth, min(created_at) as oldest, max(updated_at) as newest
  from public.task
 where state = 'dead_letter'
 group by queue, task_type
 order by depth desc, oldest;
```

Q3 — the row that produced the task, when the dead letter came from the outbox (`task.idempotency_key` is `'outbox:' || outbox.id`, `migrations/0008_platform_task_queue.sql:34-38`):

```sql
select o.id, o.org_id, o.task_type, o.aggregate_type, o.aggregate_id, o.created_at, o.dispatched_at
  from public.outbox o
  join public.task t on t.idempotency_key = 'outbox:' || o.id::text
 where t.id = '<task id>';
```

`outbox` has **no dead-letter state**: it is a row that is `dispatched_at is null` or stamped, and it is never deleted (`migrations/0006_platform_outbox.sql:73-91`, `migrations/0009_platform_outbox_dispatch.sql:28-31`). "Dead outbox rows" in the alert vocabulary therefore means *task* rows; an undispatched backlog is a different signal:

```sql
select count(*) as undispatched, min(created_at) as oldest
  from public.outbox
 where dispatched_at is null;         -- outbox_undispatched_idx
```

(`dispatched_at` at `migrations/0006_platform_outbox.sql:82`; index at `:95`.)

Q4 — the run behind a run-scoped dead letter (deadline reconcile, classify step "tenant-scoped"): run Q3 first, take `o.aggregate_id` as the run id, then in the tenant scope of §3.1:

```sql
select id, state, collection_deadline_at, version, updated_at
  from public.run
 where id = '<run uuid>';
```

`state` is one of `created, queued, running, collecting, complete, incomplete, canceled` (`migrations/0031_orchestration_run.sql:118-119`, default at `:103`); `collection_deadline_at` is at `:104`.

Q5 — one task row by id, **whatever its state**. This is the only query that can verify a replay: Q1 filters `state = 'dead_letter'`, so a row that replayed successfully is invisible there, and absence from that list proves nothing.

```sql
select id, org_id, queue, task_type, attempts, max_attempts, state,
       available_at, locked_until, last_error, created_at, updated_at, version
  from public.task
 where id = '<task id>';          -- task_pkey
```

Columns at `migrations/0008_platform_task_queue.sql:103-111` (`state`, `attempts`, `max_attempts`, `available_at`, `locked_until`, `last_error`, `created_at`, `updated_at`, `version`); served by `task_pkey` (`:112`). Run as `evidgate_app` like Q1 (no RLS on the table, §3.1).

## 4. Classification tree

`last_error` holds the redacted category plus the message head (`ITaskHandler.cs:57-63`); `attempts = max_attempts` on every dead letter (default cap 8, `migrations/0008_platform_task_queue.sql:104-105`). Walk these in order and stop at the first that matches.

1. **Tenant-scoped task claimed without a tenant.** `org_id` is null but the task type is tenant-bound; the consumer dead-letters it as `missing_tenant` rather than running it unscoped (`src/BuildingBlocks/Persistence/Tasks/ITaskHandler.cs:16-23`). Action: do **not** replay; the producer wrote a platform row for tenant work — that is a bug in the writer, escalate with the `task.id` and the task type.
2. **Transient dependency.** `last_error` names a connection, timeout or pool failure, and attempts reached the cap because the dependency was down for minutes. `[INFERENCE]` from the backoff/last-error text; confirm by the sibling tasks of the same `task_type` recovering with no replay. Action: replay (§5) — the work is unchanged and the dependency is back.
3. **Expired lease / consumer died.** `last_error` begins with `lease expired:` — that exact text is written by the claimer's reclaim (`src/BuildingBlocks/Persistence/Tasks/TaskQueueSql.cs:69`) and event `2350` names the batch (`TaskClaimer.cs:288-290`). Action: replay; a crashing handler on a large payload may repeat, so check the same `task_type` for other lease-expiry dead letters before replaying.
4. **Poison payload.** The same row dead-letters again within one attempt, or every row of one `task_type`/`aggregate_id` fails with a parse or validation error. Action: do not replay a third time; escalate as a producer bug with the `idempotency_key` and the run/aggregate id from Q3.
5. **Handler bug (systemic).** Q2 shows one `task_type` with meaningful depth across several orgs and no dependency outage. Action: escalate to the owning module; replay only after the fix is deployed, and expect the queue to drain itself on the next claim cycle.
6. **Tenant-scoped run path.** A dead letter whose `aggregate_type` is `run` (Q3) and whose run is `collecting` past `collection_deadline_at` — this is §7's known gap; follow §7.2 rather than the generic replay.

If none match, keep the row: it is the evidence. Classification is recorded in the handover note with the numbers only.

## 5. Safe replay through the operator path (01.5.1-T4, landed)

### 5.1 The route

| Item | Value | Evidence |
| --- | --- | --- |
| Verb and path | `POST /internal/operator/tasks/dead-letter/{id:guid}/replay` | `src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:45` (`:121-127` handler) |
| Listing (find the id) | `GET /internal/operator/tasks/dead-letter?queue=&cursor=&limit=` (limit default 50, max 200) | `OperatorEndpoints.cs:44` (`:74-119` handler); `ListDeadLetterTasksQuery.cs:98-101` |
| Mount / visibility | group `/internal/operator/tasks`, `ExcludeFromDescription()`, **no `/v1` mount** — operator routes carry no tenant context | `OperatorEndpoints.cs:12-19` and `:39-42` |
| Auth ingress | `RequireOperatorFilter` → `IOperatorGate.IsOperatorAsync`; denial is 401 `unauthenticated` without a principal, 403 `forbidden` with one | `OperatorEndpoints.cs:41`; `OperatorGate.cs:52-82` |
| Gate implementation today | `DenyAllOperatorGate` — **every real call is denied until 02.x authentication lands**; only test hosts substitute a permitting gate | `OperatorEndpoints.cs:67`; `OperatorGate.cs:30-33`; `tests/Hosts.IntegrationTests/OperatorDeadLetterEndpointTests.cs:40-48` |
| Request body | none (the id is the path) | `OperatorEndpoints.cs:127-132` |
| 200 response | `{ id, state: "queued", attempts: 0, available_at }` | `OperatorEndpoints.cs:136-137`; the response record is `:213` |
| 404 / 409 | `task_not_found` / `task_not_dead_letter` | `OperatorEndpoints.cs:138-143` |

⚠️ **Blocked on a real host today:** with `DenyAllOperatorGate` registered, the route answers 401 for anyone (`OperatorGate.cs:30-33` and `:68-70`). Until 02.x lands, the on-call operator cannot replay over HTTP in staging or production, and the drill T1.2 cannot be executed against a real host — the path is proven only by the integration hosts that install `PermitAllOperatorGate` (`tests/Hosts.IntegrationTests/DeadLetterChainTests.cs:175-183`). Escalate rather than hand-writing the update (§1).

### 5.2 What the replay does to the row

One guarded `UPDATE … RETURNING` with `state = 'dead_letter'` as the predicate (`src/BuildingBlocks/Persistence/Tasks/TaskQueueSql.cs:246-255`):

- `state` → `queued` (`migrations/0008_platform_task_queue.sql:113`);
- `attempts` → `0` — the row gets a full attempt budget again (`:104`; `max_attempts` is untouched, `:105`);
- `available_at` → `now()`, `locked_until` → null, so the row is immediately claimable (`:106-107`, claim index `:133`);
- **`last_error` is kept** — a replay is a re-queue, not an acquittal, and the reason stays visible (`TaskQueueSql.cs:238-245`).

There is **no `replayed` state**: `Replayed` is the *outcome* enum of the service (`src/BuildingBlocks/Persistence/Tasks/ReplayDeadLetterTask.cs:12-22`); the stored state vocabulary stays `queued, active, retry, dead_letter, done` (`migrations/0008_platform_task_queue.sql:113`). If a list still shows a row you replayed, the row was claimed again and failed again — read the new `attempts` and `last_error`, not a "replayed" marker.

### 5.3 Idempotency of the replay, and of the handler

- The **replay** is idempotent by the state guard: a second POST finds a non-`dead_letter` row and answers 409 instead of re-queueing (`ReplayDeadLetterTask.cs:40-45` and `:90-102`; `OperatorEndpoints.cs:26-27`). No `Idempotency-Key` and no `ETag` (same cite).
- The **handler** is at-least-once and must be idempotent on the work's natural key, which is the producer's `task.idempotency_key` (`ITaskHandler.cs:30-35`); the queue itself dedupes inserts by `task_idem_uq` (`migrations/0008_platform_task_queue.sql:34-38`; `:131`). For a run reconcile this is explicit: the finisher appends at most one evidence revision per attempt and never rewrites one, and a run that left `collecting` never reaches it (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileFinisher.cs:19-31`; the no-op branch is `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileRunTask.cs:74-82`).

### 5.4 Audit

The two operator task routes call the queue services directly; **no audit row is written for a list or a replay** (`OperatorEndpoints.cs:22-28` and `:127-144`), and the only operator-surface write that records a principal is the healing switch (`OperatorEndpoints.cs:165-205`). The replay's own record is the row: `last_error`, `attempts`, `updated_at`, and the `version` bump from the shared trigger (`migrations/0008_platform_task_queue.sql:139-143`). ⚠️ A tenant-visible audit of an operator replay does not exist yet; 02.8.1's grant-gated operator read path is where it belongs (`migrations/0031_orchestration_run.sql:152-155`). Record the operator's own action in the handover note (time, task id, who).

### 5.5 Steps

1. Classify (§4). Do not replay a poison payload or a `missing_tenant` row.
2. Get the id from Q1 or the list route (§5.1) and **write it down**: steps 4–5 and §6 all read that one row by id with Q5.
3. POST the replay with the operator credential. Expect 200 with `state: "queued"`, `attempts: 0`.
4. Watch the Worker log for the row: event `2306` again means a second dead letter; event `2320` means it dead-lettered again; absence of both plus a `done` state means it ran.
5. Re-read the row with **Q5** by that id — not Q1: Q1 lists only rows still in `dead_letter`, so a row you replayed successfully disappears from it and its absence proves nothing. Expect `state` `queued`/`active`/`done` and `attempts` below `max_attempts`. If Q5 shows `dead_letter` again, it goes to §4 step 4 and then §6.

## 6. Verification and escalation

After the replay, confirm from the row — read with **Q5** by the id you saved, since Q1 no longer lists the row once it leaves `dead_letter` — and, for run-scoped work, from the run:

- the task row's `state` is `done` (state vocabulary `migrations/0008_platform_task_queue.sql:113`) or `queued`/`active` with `attempts` below `max_attempts`; `updated_at` and `version` should have moved past the values the row had in `dead_letter` (`:110-111`).
- for a run-scoped task, Q4 shows the run out of `collecting` (`migrations/0031_orchestration_run.sql:118-119`).

Escalate (hand off with ids, counts, timestamps) when any of these holds:

- the same row dead-lettered again after exactly one replay;
- depth in Q2 is growing for one `task_type` and no dependency outage explains it;
- the row is a `missing_tenant` platform row for tenant-bound work;
- the dead-lettered row is older than ~25 days: retention deletes terminal rows after 30 (`src/BuildingBlocks/Persistence/Tasks/EvidgateSweeps.cs:84-96`; the delete is `src/BuildingBlocks/Persistence/Tasks/SweepTaskHandlers.cs:114-124`), and a replayed row must be replayed *before* the sweep removes it;
- the route answers 401/403 (§5.1) and the work is tenant-visible — escalate to the platform owner, never bypass;
- for a run-scoped task, the run is still `collecting` after the replay (§7.2).

## 7. Known gaps

### 7.1 Carried gap: the collection-deadline outbox key is not re-armed after a dead letter (S152, CR-10)

Carried row, verbatim from [`../status-carried.md:420`](../status-carried.md): *"The deadline reconcile is enqueued once per run with outbox key `orchestrator:run:{run}:reconcile:deadline`, and the sweep's probe skips a run whose key exists in any state. If that task dead-letters, the run stays `collecting` and is never re-queued. Fix: re-arm on dead-letter (attempt-suffixed key or a bounded attempt counter) with a test that forces the dead-letter"* (receiver: next reconcile/outbox task).

How it strands a run, in code:

| Link | Evidence |
| --- | --- |
| One deadline key per run, by design (the dedupe is the point) | `src/Modules/Evidgate.Modules.Orchestrator.Contracts/RunReconcileTask.cs:50-62` |
| The sweep's probe reads **any** outbox row with that key — no `dispatched_at` filter, so a dispatched or long-since-dead row still counts as "already queued" | `src/Modules/Evidgate.Modules.Orchestrator/Infrastructure/Persistence/ReconcileDeadlineSql.cs:48-53`; called at `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileDeadlineSweepTask.cs:197-216` |
| Outbox rows are never deleted, so the key is permanent | `migrations/0009_platform_outbox_dispatch.sql:28-31` |
| The sweep only ever sees `state = 'collecting'` runs past the database clock | `migrations/0056_reconcile_missing_result.sql:132-150` (`:144-145`) |
| A reconcile attempt does nothing when the run is not `collecting` | `ReconcileRunTask.cs:74-82` |
| Nothing else moves a `collecting` run: the only edge out is the finisher's guarded transition | `ReconcileFinisher.cs:19-31`; `migrations/0031_orchestration_run.sql:118-119` |

So: deadline task dead-letters → key persists in `outbox` → every later sweep fire treats the run as already queued → the run stays `collecting` past its deadline forever, and no alert is tied to that condition yet (the last row of §2's table).

**What the operator does today:**

1. Find the deadline outbox row for the run (§3.2 Q4 gives `org_id` and the run id; then):

   ```sql
   select o.id, o.created_at, o.dispatched_at
     from public.outbox o
    where o.org_id = '<org uuid>'
      and o.idempotency_key = 'orchestrator:run:' || '<run uuid>' || ':reconcile:deadline';
   ```

   (`outbox_idempotency_key_length_check` bounds the key at 256, `migrations/0006_platform_outbox.sql:80` for the column and `:89` for the check; the key format is `RunReconcileTask.cs:61-62`.)
2. Find the task row the dispatcher derived from it (`'outbox:' || o.id`, `migrations/0008_platform_task_queue.sql:34-38`) and confirm it is the dead letter:

   ```sql
   select t.id, t.state, t.attempts, t.max_attempts, t.last_error, t.created_at
     from public.task t
    where t.idempotency_key = 'outbox:' || '<outbox id>';
   ```

3. Replay that task id through §5 (§5.1's ⚠️ applies: 401 until 02.x). The replay re-runs the same reconcile attempt, which settles the run through the finisher because the run is still `collecting` (`ReconcileRunTask.cs:74-82`).
4. Verify. Re-run Q4 in the tenant scope, and re-run the sweep's own predicate to see whether the run is still overdue:

   ```sql
   -- role: evidgate_app, inside the tenant-scope transaction of §3.1
   select r.org_id, r.id, r.collection_deadline_at
     from public.run r
    where r.state = 'collecting'
      and r.collection_deadline_at <= now();
   ```

   This is `evidgate.reconcile_overdue_runs`' predicate with its cursor dropped (`migrations/0056_reconcile_missing_result.sql:142-149`); the function itself is executable only by `evidgate_worker_sched` (`:152-154`), so an operator reproduces the predicate rather than calling it.
5. If the run is still `collecting` after the replay: **escalate**. The sweep will not re-arm it (§7.1 table), the key is permanent, and the fix is code (receiver: the next reconcile/outbox task). Do not hand-insert a second outbox row: `outbox_org_idem_uq` (`migrations/0006_platform_outbox.sql:94`) rejects the same key, and a different key would make the run settle twice.

### 7.2 Other gaps this runbook states plainly

- **No operator authentication** — `DenyAllOperatorGate` denies every real caller, so §5 is not executable on a real host today (`OperatorGate.cs:30-33`); the target is 02.x.
- **No audit row for a replay** (§5.4) ⚠️.
- **No dead-letter-depth alert rule, no pager, no runbook link in an alert file** — the rule set is 10.3.1-T5.1 (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md:135`); this file is the link target when it lands.
- **Alert delivery is at-most-once and non-durable**; a lost alert is recovered by Q1 (`ITaskAlertSink.cs:55-60`).
- **30-day retention** removes the row (`EvidgateSweeps.cs:84-96`, `SweepTaskHandlers.cs:114-124`); after that a dead letter is only reconstructable from `outbox` (which is never deleted, `migrations/0009_platform_outbox_dispatch.sql:28-31`) and from logs.
- **Dead-letter depth is not an SLA** (`plans/00-overview.md:445`).

## 8. Drill status

`T1.2` (dead-letter drill) and `T2.2` (stuck-run drill) are **not executed**: no staging incident has been induced, no non-author has run this file, and — as §5.1/§7.2 record — a real staging host cannot replay at all until 02.x lands. AC2 and AC3 are open (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:23-24`). The drill, when it can run, must be done on a host with a substituting `IOperatorGate` in a staging profile and with fixture tenants only.

## Evidence table

Each SQL identifier this runbook uses, and where the schema defines it.

| Identifier | Kind | Migration file:line |
| --- | --- | --- |
| `public.task` | table | `migrations/0008_platform_task_queue.sql:94` |
| `task_pkey` (the by-id lookup Q5 uses) | primary key | `migrations/0008_platform_task_queue.sql:112` |
| `task.org_id` (nullable: platform work) | column | `migrations/0008_platform_task_queue.sql:96` |
| `task.queue`, `task.task_type` | columns | `migrations/0008_platform_task_queue.sql:97-98` |
| `task.payload` (64 KiB cap) | column | `migrations/0008_platform_task_queue.sql:99` and `:128` |
| `task.idempotency_key` | column | `migrations/0008_platform_task_queue.sql:100` |
| `'outbox:'` and `'schedule:'` key forms | key derivation (comment) | `migrations/0008_platform_task_queue.sql:34-38` |
| `task.state` | column | `migrations/0008_platform_task_queue.sql:103` |
| `task.attempts`, `task.max_attempts` | columns | `migrations/0008_platform_task_queue.sql:104-105` |
| `task.available_at`, `task.locked_until` | columns | `migrations/0008_platform_task_queue.sql:106-107` |
| `task.last_error` (1000 chars) | column | `migrations/0008_platform_task_queue.sql:108` and `:127` |
| `task.created_at`, `task.updated_at`, `task.version` | columns | `migrations/0008_platform_task_queue.sql:109-111` |
| `task_state_check` (the `queued`/`active`/`retry`/`dead_letter`/`done` literal set) | CHECK constraint | `migrations/0008_platform_task_queue.sql:113` |
| `dead_letter` (the only terminal state an operator acts on) | literal | `migrations/0008_platform_task_queue.sql:113` |
| `task_idem_uq` | unique index | `migrations/0008_platform_task_queue.sql:131` |
| `task_claim_idx` (claim order for a replayed row) | partial index | `migrations/0008_platform_task_queue.sql:133` |
| `task_dead_idx` (`(created_at, id)` for Q1) | partial index | `migrations/0008_platform_task_queue.sql:136` |
| `task_terminal_idx` (retention scan) | partial index | `migrations/0008_platform_task_queue.sql:137` |
| `task_set_updated_at_and_version` | trigger | `migrations/0008_platform_task_queue.sql:139-143` |
| `grant … on public.task to evidgate_app` | grant (the triage role) | `migrations/0008_platform_task_queue.sql:155` |
| `revoke all on public.task from evidgate_operator` | revoke | `migrations/0008_platform_task_queue.sql:156` |
| `public.outbox` | table | `migrations/0006_platform_outbox.sql:73` |
| `outbox.org_id` (not null) | column | `migrations/0006_platform_outbox.sql:75` |
| `outbox.aggregate_type`, `outbox.aggregate_id` | columns | `migrations/0006_platform_outbox.sql:76-77` |
| `outbox.task_type` | column | `migrations/0006_platform_outbox.sql:78` |
| `outbox.idempotency_key` | column | `migrations/0006_platform_outbox.sql:80` |
| `outbox.dispatched_at` (the only "state" outbox has) | column | `migrations/0006_platform_outbox.sql:82` |
| `outbox.created_at` | column | `migrations/0006_platform_outbox.sql:81` |
| `outbox_org_id_uq` | unique index `(org_id, id)` | `migrations/0006_platform_outbox.sql:93` |
| `outbox_org_idem_uq` (the deadline key's dedupe) | unique index `(org_id, idempotency_key)` | `migrations/0006_platform_outbox.sql:94` |
| `outbox_undispatched_idx` | partial index | `migrations/0006_platform_outbox.sql:95` |
| `revoke all on public.outbox from evidgate_operator` | revoke | `migrations/0006_platform_outbox.sql:99` |
| `grant select on public.outbox to evidgate_app` | grant | `migrations/0009_platform_outbox_dispatch.sql:58` |
| `grant update (dispatched_at) on public.outbox to evidgate_app` | column grant | `migrations/0009_platform_outbox_dispatch.sql:59` |
| `public.run` | table | `migrations/0031_orchestration_run.sql:88` |
| `run.id`, `run.org_id` | columns | `migrations/0031_orchestration_run.sql:89-90` |
| `run.version` (optimistic token) | column | `migrations/0031_orchestration_run.sql:106` |
| `run.updated_at` | column | `migrations/0031_orchestration_run.sql:108` |
| `run_pkey` (the by-id run lookup) | primary key | `migrations/0031_orchestration_run.sql:109` |
| `run.state` | column | `migrations/0031_orchestration_run.sql:103` |
| `run.collection_deadline_at` | column | `migrations/0031_orchestration_run.sql:104` |
| `run_state_check` literal set (`collecting`, `complete`, `incomplete`, `canceled`, …) | CHECK | `migrations/0031_orchestration_run.sql:118-119` |
| `enable row level security` and `force row level security` on `public.run` | RLS | `migrations/0031_orchestration_run.sql:143-144` |
| `run_tenant` policy (`for all to evidgate_app`) | policy | `migrations/0031_orchestration_run.sql:146-150` |
| `run_owner_backfill` policy (cross-tenant reads, owner only) | policy | `migrations/0031_orchestration_run.sql:161-163` |
| `grant select on public.run to evidgate_operator` (no policy → zero rows today) | grant | `migrations/0031_orchestration_run.sql:167` |
| `evidgate.current_org_id()` reading `current_setting('evidgate.org_id', true)` | function | `migrations/0002_foundation_evidgate_schema.sql:30-33` |
| `grant execute on function evidgate.current_org_id() to evidgate_app, evidgate_operator` | grant | `migrations/0002_foundation_evidgate_schema.sql:44` |
| `run_collecting_idx` | partial index | `migrations/0056_reconcile_missing_result.sql:85-87` |
| `evidgate.reconcile_overdue_runs` (signature `uuid, uuid, integer`; selects `state = 'collecting'` past the DB clock) | function | `migrations/0056_reconcile_missing_result.sql:132-150` |
| `evidgate_worker_sched` is the only role with EXECUTE on `evidgate.reconcile_overdue_runs` | grant | `migrations/0056_reconcile_missing_result.sql:152-154` |
