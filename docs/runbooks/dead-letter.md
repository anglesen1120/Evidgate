# Dead-letter runbook

Status: **10.7.1-T1.1 written; T1.2/T2.2 staging drills remain recorded as open (AC2/AC3 open).** This desk check establishes source consistency, not execution history. ⚠️ **Drill pending:** no live-environment evidence was examined to establish whether a non-author has exercised replay or induced a staging incident. The stuck-run half is a separate runbook ([stuck-run](stuck-run.md), T2.1/T2.2).

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified or blocked today · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Purpose, audience and rules

Purpose: let the **second on-call operator** (someone who did not build 01.5.1) take one dead-lettered task row from detection to "the work ran, or the cause is documented and escalated", using read-only SQL plus the shipped operator replay route **once its real-host authentication is enabled** (§5.1).

Audience: the on-call operator on duty. Prerequisites:

- read access to the `task` and `outbox` tables with `evidgate_worker` — the queue service role explicitly granted the raw reads by `0104_platform_worker_role` — plus that session set read only before its first statement (§3.1 gives the statements and guard; this role also holds `update`/`delete` on `public.task`). The request path (`evidgate_app`) holds `insert` only on the raw tables; the table owner is an emergency migration credential, not the on-call path;
- the operator credential for the API, once the real operator gate assigned to **02.8.1-T1** is registered (today `OperatorEndpoints.AddOperatorEndpoints` registers `DenyAllOperatorGate`) ⚠️;
- a way to notice the row: today only the sink log line (§2). The dead-letter-depth rule, its pager and its alert-file link are **not possible yet** (10.3.1-T5.1–T5.3); the `dead_letter` row itself is the record ⚠️.

Rules:

- **Never hand-write an `UPDATE` or `DELETE` on `public.task`.** Replay is the guarded operator path (§5). The Worker runs retention through `TaskSweepTaskHandler.SweepSql`; its default cutoff is **30 days from `task.created_at`**, not from the dead-letter transition (`EvidgateSweeps.TerminalRetentionDays`). A hand update skips the state guard, the attempt reset and the audit row the replay function appends.
- **Replay once, then look.** A second replay *while the row is still queued* is a no-op by construction (§5.3), because nothing but a `dead_letter` row matches the guard. A replay of a row that **dead-lettered again in between is not a no-op**: `evidgate.replay_dead_letter_task` in `0104_platform_worker_role` sets `attempts` back to `0`, granting a poison payload a fresh full budget. Avoid repeated replay of the same crash; classify it (§4) after one replay.
- Record ids, closed enum values, counts and timestamps only. Never copy a payload, a raw exception message beyond the redacted `last_error` head, or tenant names into a ticket (`ITaskHandler` documents the redaction rule).
- Nothing here is a statement about availability targets; the dead-letter depth number is an operational SLI, measured rather than an SLA (`plans/00-overview.md`, §11.3).

## 2. Detection: the alert, the sink, and how to confirm it fired

The mechanism's alert seam is `ITaskAlertSink`, notified after a **committed** `dead_letter` transition from each of three paths: the consumer's retry cap (`TaskConsumer.MarkFailedAsync`), its `missing_tenant` refusal (`TaskConsumer.MissingTenantReason` and the consumer refusal path), and the claimer's expired lease (`TaskClaimer`'s expired-lease notification loop). The interface's own doc comment still says "both" and names the first and third; the implementation has three call sites, and §4 depends on telling them apart. Notification is best-effort, not a durable delivery guarantee.

| What | Where it is today | State |
| --- | --- | --- |
| Alert sink (log line per transition) | `LoggingTaskAlertSink`, log **event `2320`**, warning: task id, task type, queue, attempts, source | ✅ shipped; `TasksServiceCollectionExtensions` registers it by default |
| Counter every notification increments, whatever sink is registered | `task_dead_letter_total` on meter `Evidgate` (`TaskQueueMetrics`); `TaskAlertNotifier` increments before calling the sink | ✅ shipped; process failure can still interrupt post-commit notification |
| Sink failure visibility | log event `2321`, error (`TaskAlertNotifier`) — the row is still the record | ✅ shipped |
| Consumer dead-letter line (second signal, same transition) | log event `2306`, error (`TaskConsumer`'s logging partial) | ✅ shipped |
| Claimer lease-expiry dead-letter line | log event `2350`, **error**, one line per batch with count and queue, **not task id** (`TaskClaimer`) | ✅ shipped; emitted before the claim transaction commits |
| **Dead-letter-depth alert rule and its runbook link** | `infra/alerts/<env>/` is absent; **10.3.1-T5.1** owns rules, **T5.2** links, **T5.3** routing (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md`) | ⚠️ **Not possible yet:** no deployed-rule link or pager to follow; this file is the intended runbook target |

How to inspect notification without a pager: search event `2320` or `2306` by the task id from §3.2. Search `2350` by queue and time only: it has no task id and, being logged before commit, is not proof of a committed transition. A `dead_letter` row without `2320` does **not** prove sink failure/replacement or a counter increment: process failure or log loss can intervene. `TaskAlertNotifier` increments before calling the sink, but delivery is at-most-once and non-durable (`ITaskAlertSink`): **the row is the record, the alert is only the notification**. A recovered sink does not replay a lost alert; recover the incident through the list query.

## 3. Triage

### 3.1 Roles, RLS, grants and the read-only guard

`public.task` and `public.outbox` are **mechanism tables with no row level security on purpose**: the claim scan and outbox dispatcher walk every org (`0008_platform_task_queue`, `0006_platform_outbox`; the named table entries in `contracts/rls-exceptions.txt`). `0104_platform_worker_role` separates the cross-tenant worker role from the request path, which cannot read the raw tables. Consequences for triage:

| Fact | Evidence |
| --- | --- |
| `evidgate_worker` holds `select, insert, update, delete` on `public.task` | `migrations/0104_platform_worker_role.sql` (grant block) |
| `evidgate_worker` holds `select` and `update (dispatched_at)` on `public.outbox` | same migration; the outbox dispatcher's statements |
| `evidgate_app` holds **`insert` only** on `public.task` and `public.outbox` — no `select`, `update` or `delete` on either | same migration (the revokes) |
| `evidgate_operator` holds **nothing** on either table | `0008_platform_task_queue`, `0006_platform_outbox`, and the revokes in `0104_platform_worker_role` |
| So: run §3.2's `task`/`outbox` statements as `evidgate_worker`; there is no tenant filter to set, and one statement reads every tenant's rows | the grants above |
| The **operator dead-letter route** is the tenant-scoped way in: `evidgate.list_dead_letter_tasks(...)` and `evidgate.replay_dead_letter_task(...)` are owner `SECURITY DEFINER` functions, `search_path = ''`, `EXECUTE` granted to `evidgate_app` alone. Neither function takes an org: each route requires `?org_id=`, opens that org's tenant transaction, and the function reads the org from `evidgate.current_org_id()`, so a task of another org answers not found. Neither route needs the worker credential | `0104_platform_worker_role` (`evidgate.list_dead_letter_tasks`, `evidgate.replay_dead_letter_task`); `ListDeadLetterTasksQuery`, `ReplayDeadLetterTask` |
| Tenant-path module reads use the owner-owned `security_barrier` views `public.task_in_scope`, `public.outbox_in_scope` and `public.outbox_task_in_scope` (the keyed outbox→task join), which filter on `org_id = evidgate.current_org_id()` and are `select`-only to `evidgate_app` | `0104_platform_worker_role` (the three view definitions and their revoke/grant block) |
| `public.run` **is** FORCE RLS, tenant policy `org_id = evidgate.current_org_id()`; `evidgate_app` sees one org only | `0031_orchestration_run`, `run_tenant` policy |
| `evidgate_operator` has a `select` grant on `public.run` but **no run policy**, so it reads zero rows | `0031_orchestration_run`; broader operator access belongs to **02.8.1-T2.1**, but no `run`-specific enabling migration is shipped |
| Cross-tenant `run` reads require the owner backfill policy (`evidgate_owner`) — an **emergency owner-role path**, not an on-call credential; the operator role's grant alone cannot bypass FORCE RLS | `0031_orchestration_run`, `run_owner_backfill` policy |

**Which role runs what.** Two different credentials appear below, and they are not interchangeable:

- **`evidgate_worker`** — the queue service role, for raw triage statements in §3.2 and §7.1; `0104_platform_worker_role` grants the reads and also `update`/`delete` on `public.task`, so the read-only guard is mandatory.
- **`evidgate_app`** — the request role, for tenant-scoped `public.run` reads (`0031_orchestration_run`, `run_tenant`) and the operator list/replay functions (`0104_platform_worker_role`); those functions see only the org in `evidgate.org_id`.

**Protect the session before its first statement.** `public.task` has no RLS at all, and the role you triage with holds `update` and `delete` on it (`migrations/0104_platform_worker_role.sql`), so nothing in the database turns a mistyped statement into an error. One session-level guard makes every statement that follows read-only — Q1, Q2, Q3, the undispatched-backlog query, Q5 and §7.1 steps 1–2 — and an accidental `insert`/`update`/`delete` then fails with SQLSTATE `25006` instead of touching a row:

```sql
-- role: evidgate_worker, a fresh session used for triage only
set session characteristics as transaction read only;
```

Issue it on its own, before any statement that reads the queue, and on a session with nothing open: inside a transaction block it only sets the default for the transactions that follow. A reconnected session needs it again. The tenant-scope transaction below and Q4 already spell `begin transaction read only`; the session default is what covers the statements that have no explicit `begin`.

**One tenant, in a read-only transaction.** `public.run` is RLS-protected, so its statements need the tenant GUC the helper function reads. Set it with the parameterised form of `set local` — `select set_config(…, true)` inside the transaction, never string-built SQL — the same form [stuck-run](stuck-run.md) §3.1 uses:

```sql
-- role: evidgate_app (RLS applies to public.run under this policy)
begin transaction read only;
select set_config('evidgate.org_id', '<org uuid from task.org_id>', true);
-- … the §3.2 run statement …
commit;
```

The third argument `true` makes the setting transaction-local; `evidgate.current_org_id()` reads that GUC (`0002_foundation_evidgate_schema`). Never set it outside a transaction or to an org you have no incident for. `ListDeadLetterTasksQuery.ExecuteAsync` and `ReplayDeadLetterTask.ExecuteAsync` enter the org's tenant scope before invoking the definer functions.

### 3.2 The statements

Every statement in this section runs in the read-only session of §3.1 (`set session characteristics as transaction read only;`), and Q4 also needs its tenant-scope transaction. Q1, Q2, Q3, the backlog query and Q5 read raw queue tables as **`evidgate_worker`**, granted by `0104_platform_worker_role`; Q4 reads `public.run` as **`evidgate_app`**, granted and tenant-filtered by `0031_orchestration_run`. None writes; SQLSTATE `25006` after an accidental write means the guard worked.

Q1 — the dead-letter list: the same table, predicate and ordering as `evidgate.list_dead_letter_tasks` (`0104_platform_worker_role`), called by `ListDeadLetterTasksQuery` for `GET /internal/operator/tasks/dead-letter`. The queue filter is spelled out for psql instead of `p_queue`. The route returns **one org's** rows (required `?org_id=`); this `evidgate_worker` statement reads every org, including null-org platform tasks that the tenant-scoped route cannot list or replay.

```sql
-- role: evidgate_worker
select id, org_id, queue, task_type, idempotency_key, attempts, max_attempts, last_error, created_at
  from public.task
 where state = 'dead_letter'          -- one of: queued, active, retry, dead_letter, done
   and ('<queue>' = '' or queue = '<queue>')
 order by created_at, id
 limit 50;
```

Q1 reads every org and orders by `(created_at, id)` with no `org_id` predicate, so the cross-org `task_dead_idx` (defined with the table by `0008_platform_task_queue`) is the index that matches its shape; whether the planner uses it needs a live plan, not a desk check. `task_dead_org_idx` (`org_id, created_at, id`, defined in `0104_platform_worker_role`) is the org-first index for the tenant-scoped list definer, not for Q1. `task_state_check` pins the state literals.

Q2 — dead-letter depth by queue and type, to see whether the row is an incident or a singleton:

```sql
-- role: evidgate_worker
select queue, task_type, count(*) as depth, min(created_at) as oldest, max(updated_at) as newest
  from public.task
 where state = 'dead_letter'
 group by queue, task_type
 order by depth desc, oldest;
```

Q3 — the producer row when the task came from outbox: `OutboxDispatchSql` derives `task.idempotency_key` as `'outbox:' || outbox.id::text` (also documented by `0008_platform_task_queue`):

```sql
-- role: evidgate_worker
select o.id, o.org_id, o.task_type, o.aggregate_type, o.aggregate_id, o.created_at, o.dispatched_at
  from public.outbox o
  join public.task t on t.idempotency_key = 'outbox:' || o.id::text
 where t.id = '<task id>';
```

`outbox` has **no dead-letter state**: it is undispatched (`dispatched_at is null`) or stamped. Runtime roles cannot delete it: `0006_platform_outbox` and `0009_platform_outbox_dispatch` revoke deletion, and `0104_platform_worker_role` grants `evidgate_worker` only `select` and `update (dispatched_at)`. Only the owner migration credential `evidgate_owner` could delete a row; it is not the on-call path (§3.1). A "dead outbox row" means a derived *task*; an undispatched backlog is different:

```sql
-- role: evidgate_worker
select count(*) as undispatched, min(created_at) as oldest
  from public.outbox
 where dispatched_at is null;         -- outbox_undispatched_idx
```

`0006_platform_outbox` defines `outbox.dispatched_at` and `outbox_undispatched_idx`.

Q4 — the run behind an outbox-backed run-scoped dead letter: save the task's `org_id` from Q1/Q5 (or Q3's `o.org_id`) and take Q3's `o.aggregate_id` as the run id. Then use the tenant scope of §3.1; Q4 itself does **not** return `org_id`:

```sql
-- role: evidgate_app, inside the tenant-scope transaction of §3.1
select id, state, collection_deadline_at, version, updated_at
  from public.run
 where id = '<run uuid>';
```

`0031_orchestration_run` defines `run_state_check` (`created, queued, running, collecting, complete, incomplete, canceled`), the `created` default, and `collection_deadline_at`.

Q5 — one task row by id, **whatever its state**. This is the only query that can verify a replay: Q1 filters `state = 'dead_letter'`, so a row that replayed successfully is invisible there, and absence from that list proves nothing.

```sql
-- role: evidgate_worker
select id, org_id, queue, task_type, attempts, max_attempts, state,
       available_at, locked_until, last_error, created_at, updated_at, version
  from public.task
 where id = '<task id>';          -- task_pkey
```

`0008_platform_task_queue` defines these columns and `task_pkey`. Run as `evidgate_worker`, whose raw-table read grant is in `0104_platform_worker_role` (no queue-table RLS, §3.1).

## 4. Classification tree

`last_error` holds a redacted category and message head (`ITaskHandler`, `TaskErrorText`). The reason prefix identifies the class; attempts alone do not. Budget-driven paths use the retry-cap check in `TaskConsumer.MarkFailedAsync` or the `attempts >= max_attempts` predicate in `TaskQueueSql.DeadLetterExpiredLeases` (default cap 8 in `0008_platform_task_queue`). A `missing_tenant` refusal instead dead-letters on its first claim after refusal, bypassing retry handling (`TaskConsumer.MissingTenantReason`); its attempts may be below the cap. Walk in order, treating root-cause classifications as hypotheses until live evidence supports them.

1. **Tenant-scoped task claimed without a tenant.** `last_error` starts `missing_tenant:` (`TaskConsumer.MissingTenantReason`), `org_id` is null, and its `ITaskHandler` requires a tenant. The consumer refuses instead of running unscoped and bypasses retry handling. Low attempts mean immediate refusal, not remaining retries. **Do not replay**; escalate the producer bug with task id and type.
2. **Transient dependency.** Connection, timeout or pool failure in `last_error` suggests this class, but does not establish the outage or recovery. `[INFERENCE]` ⚠️ **Drill pending:** confirm current dependency health and sibling-task recovery from live telemetry. Replay (§5) only after recovery is evidenced.
3. **Expired lease / consumer died.** `last_error` starts `lease expired:` (`TaskQueueSql.DeadLetterExpiredLeases`); `TaskClaimer` emits batch event `2350` with queue/count, not task id. Check sibling lease-expiry dead letters and Worker health before one replay: a crashing handler may repeat.
4. **Poison payload.** Repeat failure of one row, or parse/validation failures across one type/aggregate, is a hypothesis, not proof. A normal cap-8 handler can exhaust several fresh claims; one failure is not a one-attempt budget. ⚠️ **Drill pending:** establish the repeated pattern from live rows/logs. If one replay dead-letters again, **stop replaying** and escalate with the idempotency key and Q3's aggregate id.
5. **Handler bug (systemic).** Cross-org depth for one `task_type` suggests shared failure but cannot distinguish a code defect from dependency or configuration failure. ⚠️ **Drill pending:** correlate live deployment/configuration and dependency evidence before naming the cause. Escalate to the module owner; after a fix, existing `dead_letter` rows still need guarded replay or the bounded deadline re-arm (§7.1), not an ordinary claim.
6. **Tenant-scoped run path.** A dead letter whose `aggregate_type` is `run` (Q3) and whose run is `queued`, `running` or `collecting` past `collection_deadline_at` — the deadline trigger's case — gets a bounded re-arm from the sweep (three attempts at most, §7.1), so follow §7.1 rather than the generic replay.

If none match, keep the row: it is the evidence. Classification is recorded in the handover note with the numbers only.

## 5. Safe replay through the operator path (01.5.1-T4, landed)

### 5.1 The route

| Item | Value | Evidence |
| --- | --- | --- |
| Verb and path | `POST /internal/operator/tasks/dead-letter/{id:guid}/replay?org_id={org}` | `MapOperatorEndpoints` (the `MapPost` on the `/internal/operator/tasks` group) and `ReplayDeadLetterAsync` |
| Listing (find the id) | `GET /internal/operator/tasks/dead-letter?org_id={org}&queue=&cursor=&limit=` (limit default 50, max 200) | `MapOperatorEndpoints` (the `MapGet` on the same group), `ListDeadLetterAsync`, `ListDeadLetterTasksQuery.DefaultLimit` and `.MaxLimit` |
| `org_id` (both routes) | **required**; absent → 400 `request_invalid` with detail `{"path":"org_id","reason":"required"}` (not a free-text `detail` string) | `ListDeadLetterAsync` and `ReplayDeadLetterAsync` (the `org_id` guards); `ErrorDetail` in `ErrorEnvelope` |
| What the org reaches | the route opens that org's tenant transaction; the definer function reads `evidgate.current_org_id()` and sees nothing beyond it | `ListDeadLetterTasksQuery.ExecuteAsync`, `ReplayDeadLetterTask.ExecuteAsync`, `0104_platform_worker_role` |
| Credential | neither route needs the worker connection string: they run on the app role and reach the queue only through the definer functions | `0104_platform_worker_role` (the EXECUTE grants to `evidgate_app`) |
| Mount / visibility | group `/internal/operator/tasks`, `ExcludeFromDescription()`, **no `/v1` mount** | `MapOperatorEndpoints` (`MapGroup("/internal/operator/tasks")` with the two endpoint filters) |
| Auth ingress | `RequireOperatorFilter` → `IOperatorGate.IsOperatorAsync`; denial is 401 `unauthenticated` without a principal, 403 `forbidden` with one | `RequireOperatorFilter` in `OperatorGate`; the group filters in `MapOperatorEndpoints` |
| Gate implementation today | `DenyAllOperatorGate` — **every real call is denied until 02.x authentication lands**; only test hosts substitute a permitting gate | `AddOperatorEndpoints` (registers `DenyAllOperatorGate`), `DenyAllOperatorGate`, `PermitAllOperatorGate` in `OperatorDeadLetterEndpointTests` |
| Request body | none (the id is the path, the org is the query parameter) | the `ReplayDeadLetterAsync` signature |
| Replay actor | the authenticated principal's user id (`NameIdentifier` / `sub`), which becomes the audit row's actor; unauthenticated → 401, authenticated without a usable id → 403 | `ReplayDeadLetterAsync` (the principal and `sub` checks) |
| 200 response | `{ id, state: "queued", attempts: 0, available_at }` | `ReplayDeadLetterAsync` success branch; `ReplayDeadLetterTaskResponse` |
| 404 / 409 | `task_not_found` / `task_not_dead_letter` | `ReplayDeadLetterAsync` outcome switch (`ReplayDeadLetterOutcome`) |

⚠️ **Blocked on a real host today:** with `DenyAllOperatorGate` registered, the route answers 401 for anyone (`DenyAllOperatorGate.IsOperatorAsync`, and `RequireOperatorFilter`'s 401/403 branch). Until 02.x lands, the on-call operator cannot replay over HTTP in staging or production, and the drill T1.2 cannot be executed against a real host — the path is proven only by the integration hosts that install `PermitAllOperatorGate` (`DeadLetterChainTests`). Escalate rather than hand-writing the update (§1).

### 5.2 What the replay does to the row

The route calls `evidgate.replay_dead_letter_task(p_task_id uuid, p_actor uuid)` — an owner `SECURITY DEFINER` function (`search_path = ''`, `EXECUTE` to `evidgate_app` alone) on the app-role tenant transaction (`ReplayDeadLetterTask.ReplaySql`) — which performs one guarded `UPDATE … RETURNING` with `state = 'dead_letter'` as the predicate, reads the org it may touch from `evidgate.current_org_id()`, appends one audit row with `p_actor`, and returns `replayed` (with the new `available_at`), `not_found` or `not_dead_letter` (`0104_platform_worker_role`, `evidgate.replay_dead_letter_task`). A null org raises `23514` and a null actor `22004` rather than replaying unattributed; the row of another org is invisible to the guard and answers `not_found`:

- `state` → `queued` (`0008_platform_task_queue`, `task_state_check`);
- `attempts` → `0` — the row gets a full attempt budget again (`task.attempts` in `0008_platform_task_queue`; `max_attempts` is untouched);
- `available_at` → `now()`, `locked_until` → null, so the row is immediately claimable (the replay `UPDATE` in `0104_platform_worker_role`; claim index `task_claim_idx`);
- **`last_error` is kept** — a replay is a re-queue, not an acquittal, and the reason stays visible (the function's guarded `UPDATE` keeps the column out of its `SET` list).

There is **no `replayed` state**: `Replayed` is the *outcome* enum of the service (`ReplayDeadLetterOutcome`); the stored state vocabulary stays `queued, active, retry, dead_letter, done` (`0008_platform_task_queue`, `task_state_check`). If a list still shows a row you replayed, the row was claimed again and failed again — read the new `attempts` and `last_error`, not a "replayed" marker.

### 5.3 Idempotency of the replay, and of the handler

- The **replay** is idempotent by the state guard: a second POST finds a row that is no longer `dead_letter`, `evidgate.replay_dead_letter_task` returns `not_dead_letter`, and the route answers 409 instead of re-queueing (`ReplayDeadLetterTask.ExecuteAsync` maps the function's outcome; the `ReplayDeadLetterAsync` outcome switch). An outcome the map does not know fails the call closed rather than being treated as a replay. No `Idempotency-Key` and no `ETag` (same cite).
- The **handler** is at-least-once and must be idempotent on the work's natural key, which is the producer's `task.idempotency_key` (`ITaskHandler`); the queue itself dedupes inserts by `task_idem_uq` (`0008_platform_task_queue`; the key forms are in its header comment). For a run reconcile this is explicit: the finisher appends at most one evidence revision per attempt and never rewrites one (`ReconcileFinisher`, the equal-evidence guard; `0049_orchestration_evidence_revision`, the append-only revoke), and a terminal run, or one the reconciler answers `NotCollectable` for, never reaches it (`ReconcileRunTask`, the no-finisher dispositions).

### 5.4 Audit

`evidgate.replay_dead_letter_task` appends **one audit row per replay**, in the same transaction as the guarded `UPDATE` that made the row `queued`; `not_found` and `not_dead_letter` append nothing because they change nothing (`0104_platform_worker_role`, `evidgate.replay_dead_letter_task`; `ReplayDeadLetterTask` maps its outcome for the route). The list route writes no audit row: it reads `evidgate.list_dead_letter_tasks(...)` and returns rows of one org. The replay's own record is the row: `last_error`, `attempts`, `updated_at`, and the `version` bump from the shared trigger `task_set_updated_at_and_version` (`0008_platform_task_queue`). The audit row names the operator principal's user id as its actor (`ReplayDeadLetterAsync`); ⚠️ the gate denies every caller until 02.x (§5.1), so no actor has been recorded on a real host yet — still write the operator's action into the handover note (time, task id, who).

### 5.5 Steps

1. Classify (§4). Do not replay a poison payload or a `missing_tenant` row.
2. Get the id from Q1 or the list route (§5.1) and **write it down** together with the task's `org_id`: steps 4–5 and §6 all read that one row by id with Q5, and the replay must name the org.
3. POST the replay with the operator credential and `?org_id=<the task's org>`. Expect 200 with `state: "queued"`, `attempts: 0`; a missing org is 400 `request_invalid` with detail `{"path":"org_id","reason":"required"}`, an org that does not own the task is 404 `task_not_found`.
4. Watch the Worker log for the row by id: a second dead letter writes **both** `2306` (the consumer's line) and `2320` (the sink's line) for a consumer-path death; a lease-expiry death writes `2350` with a queue count only, no id. Absence of those, plus `state` `done` in Q5, means it ran.
5. Re-read the row with **Q5** by that id — not Q1: Q1 lists only rows still in `dead_letter`, so a row you replayed successfully disappears from it and its absence proves nothing. Expect `state` `queued`/`active`/`done` and `attempts` below `max_attempts`. If Q5 shows `dead_letter` again, repeat §4 from the top — the second death may be a different class from the first (a `missing_tenant` refusal or an expired lease, not the handler failure you replayed) — then §6.

## 6. Verification and escalation

After the replay, confirm from the row — read with **Q5** by the id you saved, since Q1 no longer lists the row once it leaves `dead_letter` — and, for run-scoped work, from the run:

- the task row's `state` is `done` (`0008_platform_task_queue`, `task_state_check`) or `queued`/`active` with `attempts` below `max_attempts`; `updated_at` and `version` should have moved past the values the row had in `dead_letter` (`task.updated_at`, `task.version`).
- for a run-scoped task, Q4 shows the run out of `queued`/`running`/`collecting` (`0031_orchestration_run`, `run_state_check`).

Escalate (hand off with ids, counts, timestamps) when any of these holds:

- the same row dead-lettered again after exactly one replay;
- depth in Q2 is growing for one `task_type` and no dependency outage explains it;
- the row is a `missing_tenant` platform row for tenant-bound work;
- the dead-lettered row is older than ~25 days: retention deletes terminal rows after 30 (`EvidgateSweeps.TerminalRetentionDays` and the `TaskSweep` definition; the delete is `TaskSweepTaskHandler.SweepSql`), and a replayed row must be replayed *before* the sweep removes it;
- the route answers 401/403 (§5.1) and the work is tenant-visible — escalate to the platform owner, never bypass;
- for a run-scoped task, the run is still `queued`, `running` or `collecting` after the replay, or the sweep has spent its last deadline attempt on it (§7.1).

## 7. Known gaps

### 7.1 The collection-deadline trigger: a bounded re-arm after a dead letter (S165, closes the S152 carried row)

The S152 carried row ([`../status-carried.md`](../status-carried.md), the "Closed by collection deadline re-arm (#348, b9b30f0c)" row) is closed by `0058_reconcile_overdue_running` and the sweep's attempt chain. The deadline trigger has one outbox key per attempt — `orchestrator:run:{run}:reconcile:deadline` for the first, `…:reconcile:deadline:{attempt}` for a re-arm (`RunReconcileContract.DeadlineIdempotencyKey`) — and the sweep queues the next attempt only once the previous one is **spent**, up to `RunReconcileContract.DeadlineMaxAttempts = 3`. An attempt is spent when nothing will come of it: its task row **dead-lettered**, it finished **`done`** while the run was still overdue (the attempt decided "nothing to decide" for this run, or lost the race to a no-op), or its task row was **purged by retention after dispatch** — the outbox row stays and the trigger still spent a slot (`ReconcileDeadlineSweepTaskHandler.IsSpent`). A spent attempt is never retried; the chain moves to the next key.

What the sweep decides per overdue run (`ReconcileDeadlineSweepTaskHandler.PlanAttempt`), reading each attempt key's dispatch flag and task state through one probe for the whole page (`ReconcileDeadlineSql.AttemptStates`):

| What the probe shows for one run | Sweep action |
| --- | --- |
| no outbox row for attempt 1 | queues attempt 1 |
| attempts `1..n-1` each spent (dead letter, `done`, or purged after dispatch), no row for attempt `n` | queues attempt `n` |
| a row that is not spent: undispatched, or its task `queued`, `active` or waiting a retry | queues nothing — that attempt is work in progress, the sweep waits |
| all three attempts spent | queues nothing; one warning event `2427` per fire carries the count (`ReconcileDeadlineSweepTaskHandler.LogAttemptsExhausted`) |

So a dead letter no longer strands the run: the next fire re-arms it, twice at most. "The key exists" stopped meaning "the attempt happened", because the probe reads the dispatch flag and the task state instead of the key's presence; outbox rows themselves are still never deleted (no application role holds `delete`: `0006_platform_outbox` and `0009_platform_outbox_dispatch` revoke it). A task row is purged by retention after 30 days (`EvidgateSweeps.TerminalRetentionDays`); being dispatched with no task row left is read as spent, so the purge advances the chain instead of ending it.

The operator is involved only after the third attempt: event `2427` names how many runs spent all three this fire, each of them stays overdue with no further row ever queued, and the third attempt's dead letter is the row to inspect (§5 replay, or escalate in step 5).

**What the operator does today:**

1. Find the deadline outbox rows for the run — one per attempt (§3.2 Q4 gives `org_id` and the run id; then):

   ```sql
   -- role: evidgate_worker, in the read-only session of §3.1 (set session characteristics as transaction read only)
   select o.id, o.idempotency_key, o.created_at, o.dispatched_at
     from public.outbox o
    where o.org_id = '<org uuid>'
      and o.idempotency_key like 'orchestrator:run:' || '<run uuid>' || ':reconcile:deadline%';
   ```

   (`outbox_idempotency_key_length_check` bounds the key at 256; the key format is `RunReconcileContract.DeadlineIdempotencyKey` and the attempt bound `RunReconcileContract.DeadlineMaxAttempts` — at most three keys, the last being `…:reconcile:deadline:3`.)
2. Find the task row the dispatcher derived from it (`'outbox:' || o.id`, the key-form comment in `0008_platform_task_queue`) and confirm it is the dead letter:

   ```sql
   -- role: evidgate_worker, same read-only session
   select t.id, t.state, t.attempts, t.max_attempts, t.last_error, t.created_at
     from public.task t
    where t.idempotency_key = 'outbox:' || '<outbox id>';
   ```

3. Replay that task id through §5 (§5.1's ⚠️ applies: 401 until 02.x). The replay re-runs that reconcile attempt: with the run still past `collection_deadline_at` in `queued` or `running` (or `collecting`), the attempt decides it and the run reaches its verdict in the finisher (`ReconcileRunTask`, the finisher dispatch). Replaying the third attempt does not buy a fourth: the sweep stops at `RunReconcileContract.DeadlineMaxAttempts = 3`, so a run still overdue after it needs step 5.
4. Verify. Re-run Q4 in the tenant scope, and re-run the sweep's own predicate to see whether the run is still overdue:

   ```sql
   -- role: evidgate_app, inside the tenant-scope transaction of §3.1
   select r.org_id, r.id, r.state, r.collection_deadline_at
     from public.run r
    where r.state in ('queued', 'running', 'collecting')
      and r.collection_deadline_at <= now();
   ```

   This is `evidgate.reconcile_overdue_runs`' predicate with its cursor dropped (`0058_reconcile_overdue_running`); the function itself is executable only by `evidgate_worker_sched` (same migration, the EXECUTE revoke/grant), so an operator reproduces the predicate rather than calling it.
5. If the run is still overdue after the replay, or after the third attempt: **escalate**. The sweep stops at its attempt bound and says so with warning event `2427` (counts only, once per fire), so nothing further is queued for that run and the fix is an operator action, not another row. Do not hand-insert a second outbox row: it is an unaudited write that bypasses the outbox writer, `outbox_org_idem_uq` (`0006_platform_outbox`) rejects a key that already exists, and an attempt for a run the reconciler answers `NotCollectable` for reaches no finisher (`ReconcileRunTask`, the no-finisher dispositions, set in `RunReconciler`). Escalate with the run id, the outbox ids and their attempt suffixes, the task ids and the timestamps.

### 7.2 Other gaps this runbook states plainly

- **No operator authentication** — `DenyAllOperatorGate` denies every real caller, so §5 is not executable on a real host today; the target is 02.x.
- **The replay actor is only as good as the principal** (§5.4): a real host refuses every caller until 02.x, so no replay has been attributed to a person yet.
- **No dead-letter-depth alert rule, no pager, no runbook link in an alert file** — the rule set is 10.3.1-T5.1–T5.3 (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md`, §10.3.1-T5); this file is the link target when it lands.
- **Alert delivery is at-most-once and non-durable**; a lost alert is recovered by Q1 (`ITaskAlertSink`, the remarks on delivery).
- **30-day retention** removes the row (`EvidgateSweeps.TerminalRetentionDays`, `TaskSweepTaskHandler.SweepSql`); after that a dead letter is only reconstructable from `outbox` (which is never deleted: no application role holds `delete`, `0006_platform_outbox` and `0009_platform_outbox_dispatch`) and from logs.
- **Dead-letter depth is not an SLA** (`plans/00-overview.md` §11, Operational SLIs: measured, not SLA).

## 8. Drill status

`T1.2` (dead-letter drill) and `T2.2` (stuck-run drill) are **not executed**: no staging incident has been induced, no non-author has run this file, and — as §5.1/§7.2 record — a real staging host cannot replay at all until 02.x lands. AC2 and AC3 are open (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md`, §2 acceptance criteria). The drill, when it can run, must be done on a host with a substituting `IOperatorGate` in a staging profile and with fixture tenants only.

## Evidence table

Each SQL identifier this runbook uses, and where the schema defines it.

| Identifier | Kind | Migration (object) |
| --- | --- | --- |
| `public.task` | table | `0008_platform_task_queue` (`create table public.task`) |
| `task_pkey` (the by-id lookup Q5 uses) | primary key | `0008_platform_task_queue` (`task_pkey`) |
| `task.org_id` (nullable: platform work) | column | `0008_platform_task_queue` (`task.org_id`) |
| `task.queue`, `task.task_type` | columns | `0008_platform_task_queue` (`task.queue`, `task.task_type`) |
| `task.payload` (64 KiB cap) | column | `0008_platform_task_queue` (`task.payload`, `task_payload_size_check`) |
| `task.idempotency_key` | column | `0008_platform_task_queue` (`task.idempotency_key`, `task_idempotency_key_length_check`) |
| `'outbox:'` and `'schedule:'` key forms | key derivation (comment) | `0008_platform_task_queue` (the key-and-index header comment; `task_idem_uq`, `task_fire_uq`) |
| `task.state` | column | `0008_platform_task_queue` (`task.state`) |
| `task.attempts`, `task.max_attempts` | columns | `0008_platform_task_queue` (`task.attempts`, `task.max_attempts`) |
| `task.available_at`, `task.locked_until` | columns | `0008_platform_task_queue` (`task.available_at`, `task.locked_until`) |
| `task.last_error` (1000 chars) | column | `0008_platform_task_queue` (`task.last_error`, `task_last_error_length_check`) |
| `task.created_at`, `task.updated_at`, `task.version` | columns | `0008_platform_task_queue` (`task.created_at`, `task.updated_at`, `task.version`) |
| `task_state_check` (the `queued`/`active`/`retry`/`dead_letter`/`done` literal set) | CHECK constraint | `0008_platform_task_queue` (`task_state_check`) |
| `dead_letter` (the only terminal state an operator acts on) | literal | `0008_platform_task_queue` (`task_state_check`) |
| `task_idem_uq` | unique index | `0008_platform_task_queue` (`task_idem_uq`) |
| `task_claim_idx` (claim order for a replayed row) | partial index | `0008_platform_task_queue` (`task_claim_idx`) |
| `task_dead_idx` (`(created_at, id)` for Q1) | partial index | `0008_platform_task_queue` (`task_dead_idx`) |
| `task_terminal_idx` (retention scan) | partial index | `0008_platform_task_queue` (`task_terminal_idx`) |
| `task_set_updated_at_and_version` | trigger | `0008_platform_task_queue` (`task_set_updated_at_and_version`) |
| `grant select, insert, update, delete on public.task to evidgate_worker` (the triage role) and `revoke` the same from `evidgate_app`, which keeps `insert` only | grants | `0104_platform_worker_role` (supersedes the `0008_platform_task_queue` grant to `evidgate_app`) |
| `revoke all on public.task from evidgate_operator` | revoke | `0008_platform_task_queue` (`revoke all … from evidgate_operator`) |
| `public.outbox` | table | `0006_platform_outbox` (`create table public.outbox`) |
| `outbox.org_id` (not null) | column | `0006_platform_outbox` (`outbox.org_id`) |
| `outbox.aggregate_type`, `outbox.aggregate_id` | columns | `0006_platform_outbox` (`outbox.aggregate_type`, `outbox.aggregate_id`) |
| `outbox.task_type` | column | `0006_platform_outbox` (`outbox.task_type`) |
| `outbox.idempotency_key` | column | `0006_platform_outbox` (`outbox.idempotency_key`, `outbox_idempotency_key_length_check`) |
| `outbox.dispatched_at` (the only "state" outbox has) | column | `0006_platform_outbox` (`outbox.dispatched_at`) |
| `outbox.created_at` | column | `0006_platform_outbox` (`outbox.created_at`) |
| `outbox_org_id_uq` | unique index `(org_id, id)` | `0006_platform_outbox` (`outbox_org_id_uq`) |
| `outbox_org_idem_uq` (the deadline key's dedupe) | unique index `(org_id, idempotency_key)` | `0006_platform_outbox` (`outbox_org_idem_uq`) |
| `outbox_undispatched_idx` | partial index | `0006_platform_outbox` (`outbox_undispatched_idx`) |
| `grant insert on public.outbox to evidgate_app` (the write path; `update` is column-scoped below) | grant | `0006_platform_outbox` (`grant insert … to evidgate_app`) |
| `revoke select, update, delete on public.outbox from evidgate_app` (so no application role can delete an outbox row) | revoke | `0006_platform_outbox` (`revoke select, update, delete … from evidgate_app`) |
| `revoke all on public.outbox from evidgate_operator` | revoke | `0006_platform_outbox` (`revoke all … from evidgate_operator`) |
| `grant select` and `update (dispatched_at) on public.outbox to evidgate_worker` (the dispatcher); `insert` stays with `evidgate_app`, which loses the `select` and the column grant | grants | `0104_platform_worker_role` (supersedes the `0009_platform_outbox_dispatch` column grant) |
| `public.task_in_scope`, `public.outbox_in_scope`, `public.outbox_task_in_scope` (`security_barrier`, `where org_id = evidgate.current_org_id()`, `select` to `evidgate_app` only) | views (the tenant path into the queue; the third is the keyed outbox→task join) | `0104_platform_worker_role` (the three view definitions and their revoke/grant block) |
| `evidgate.list_dead_letter_tasks(p_queue text, p_after_created_at timestamptz, p_after_id uuid, p_limit int)` | owner `SECURITY DEFINER` function, `search_path = ''`, `EXECUTE` to `evidgate_app` only | `0104_platform_worker_role` (`evidgate.list_dead_letter_tasks`) |
| `evidgate.replay_dead_letter_task(p_task_id uuid, p_actor uuid)` → `replayed` / `not_found` / `not_dead_letter` | owner `SECURITY DEFINER` function, org from `evidgate.current_org_id()`, one audit row per replay naming `p_actor` | `0104_platform_worker_role` (`evidgate.replay_dead_letter_task`) |
| `public.run` | table | `0031_orchestration_run` (`create table public.run`) |
| `run.id`, `run.org_id` | columns | `0031_orchestration_run` (`run.id`, `run.org_id`) |
| `run.version` (optimistic token) | column | `0031_orchestration_run` (`run.version`) |
| `run.updated_at` | column | `0031_orchestration_run` (`run.updated_at`) |
| `run_pkey` (the by-id run lookup) | primary key | `0031_orchestration_run` (`run_pkey`) |
| `run.state` | column | `0031_orchestration_run` (`run.state`) |
| `run.collection_deadline_at` | column | `0031_orchestration_run` (`run.collection_deadline_at`) |
| `run_state_check` literal set (`collecting`, `complete`, `incomplete`, `canceled`, …) | CHECK | `0031_orchestration_run` (`run_state_check`) |
| `enable row level security` and `force row level security` on `public.run` | RLS | `0031_orchestration_run` (`alter table public.run … row level security`) |
| `run_tenant` policy (`for all to evidgate_app`) | policy | `0031_orchestration_run` (`run_tenant`) |
| `run_owner_backfill` policy (cross-tenant reads, owner only) | policy | `0031_orchestration_run` (`run_owner_backfill`) |
| `grant select on public.run to evidgate_operator` (no policy → zero rows today) | grant | `0031_orchestration_run` (`grant select on public.run to evidgate_operator`) |
| `evidgate.current_org_id()` reading `current_setting('evidgate.org_id', true)` | function | `0002_foundation_evidgate_schema` (`evidgate.current_org_id`) |
| `evidgate.org_id` (the GUC §3.1 writes with `select set_config(…, true)`, kept transaction-local) | session setting | `0002_foundation_evidgate_schema` (the setting read by `evidgate.current_org_id`) |
| `grant execute on function evidgate.current_org_id() to evidgate_app, evidgate_operator` | grant | `0002_foundation_evidgate_schema` (`grant execute on function evidgate.current_org_id()`) |
| `run_overdue_idx` (`where state in ('queued', 'running', 'collecting')`, replaced `run_collecting_idx`) | partial index | `0058_reconcile_overdue_running` (`run_overdue_idx`; `run_collecting_idx` dropped) |
| `evidgate.reconcile_overdue_runs` (signature `uuid, uuid, integer`; selects `state in ('queued', 'running', 'collecting')` past the DB clock) | function | `0058_reconcile_overdue_running` (`evidgate.reconcile_overdue_runs`) |
| `evidgate_worker_sched` is the only role with EXECUTE on `evidgate.reconcile_overdue_runs` | grant | `0058_reconcile_overdue_running` (the EXECUTE revoke/grant for `evidgate.reconcile_overdue_runs`) |

Desk check: 2026-11-21, wave fd69e03b; not checkable without a live environment: staging drill by a non-author (AC2/AC3), log-backend retention of events 2306/2320/2350, the deployed operator gate and credential provisioning, replay behaviour of a concrete handler, pager routing, and real dead-letter depth.
