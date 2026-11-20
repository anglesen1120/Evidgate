# Stuck run runbook

Status: **10.7.1-T2.1 written, declared partial; 10.7.1-T2.2 (staging drill) is open.**

T1.2/T2.2 staging drill by a non-author NOT yet executed (AC2/AC3 open).

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Every SQL identifier used below is cited to the migration that creates it, and the full mapping is the [evidence table](#8-evidence-table) in §8. Line cites are on this worktree's `develop` (`c1021544`).

## 1. Scope, roles and rules

**Purpose.** Find a run whose worker died or whose finalize never arrived, decide whether it can be reconciled to a verdict or must be escalated, and settle it **without inventing evidence**. This is the human half of 10.7.1-T2.1: the automated half is the collection-deadline sweep (§5.1).

**Audience.** A second on-call operator who did not build the orchestrator. You need no knowledge of the code beyond this page and the cites in it.

**In scope.** A `public.run` row in a non-terminal state that is past its `collection_deadline_at` (00 §8.1, `plans/00-overview.md:259`). Also the reconcile work queued for that run.

**Out of scope.** Dead-lettered `task` rows and their replay (see the [dead-letter runbook](dead-letter.md)); orphan artifacts (00 §8.4); healing incidents (00 §8.7); a run that is terminal but wrong (a contract defect, not an operational one).

**Prerequisites.**

- A checkout of the release tag this incident is about (every cite here is a line in that tree).
- A database session as `evidgate_app`, obtained the way [environments](environments.md) describes for the environment you are in; a read-only session is enough for everything here except the cross-tenant scan in §3.2, which needs a platform/DBA-owned read session (§3.2).
- The `org_id` and `run_id` you are investigating (both UUIDs; never paste a customer name, URL or repository name into a ticket).
- Nothing else. In particular you do **not** need the API, the CLI, or an operator credential for the detection half.

**Roles (00 §5.4.3, `plans/00-overview.md:185`).** Four roles exist: `evidgate_owner`, `evidgate_app`, `evidgate_operator`, `evidgate_worker_sched`. For this runbook:

| Role | What it can read here | Use it for |
|---|---|---|
| `evidgate_app` | the rows of **one** org, once `evidgate.org_id` is set in the transaction (§3.1); `public.task` and `public.outbox` are mechanism tables with no tenant policy, so an app-role read of those spans every org (`migrations/0008_platform_task_queue.sql:82-87`, `migrations/0006_platform_outbox.sql:14`) | the per-tenant queries, and every sweep/queue inspection |
| `evidgate_owner` | **every** org's rows, through the permissive `run_owner_backfill` policy (`migrations/0031_orchestration_run.sql:161-163`) | nothing you hold: it is a migration/administration credential, not an on-call one ([dead-letter runbook](dead-letter.md) §3.1). §3.2's cross-tenant scan needs a platform/DBA-owned break-glass read session — ⚠️ until the 02.8.1 grant-gated reader exists |
| `evidgate_operator` | the `run` and `evidence_revision` **grants** exist (`migrations/0031_orchestration_run.sql:167`, `migrations/0049_orchestration_evidence_revision.sql:164`) but no operator policy does, so an operator session reads **zero rows** (`migrations/0031_orchestration_run.sql:152-155`) | nothing in this runbook — do not try |
| `evidgate_worker_sched` | no privilege on `run` (`migrations/0031_orchestration_run.sql:168`); only `EXECUTE` on `evidgate.reconcile_overdue_runs` (`migrations/0058_reconcile_overdue_running.sql:87`) | nothing by hand; the Worker uses it |

**Rules.**

1. Every query here is a `SELECT` inside `start transaction read only`. Never write a hand-written `UPDATE`/`DELETE`/`INSERT`. The app role holds no table-level `UPDATE` on `public.run` (`migrations/0031_orchestration_run.sql:165`); it holds exactly three column grants: `update (updated_at)` (`migrations/0034_ingestion_event_and_batch.sql:252`), `update (state)` (`migrations/0036_orchestration_run_state_grant.sql:34`) and `update (current_evidence_revision_id)` (`migrations/0049_orchestration_evidence_revision.sql:255`). Only the pointer move is guarded at the database (`migrations/0049_orchestration_evidence_revision.sql:186-219`); `state` and `updated_at` have no transition guard at all, so a hand `UPDATE` bypasses the adjacency check, the optimistic `version` guard and the `run.state_changed` audit row.
2. The tenant tables are `FORCE ROW LEVEL SECURITY` (`migrations/0031_orchestration_run.sql:143-144`, `migrations/0049_orchestration_evidence_revision.sql:145`). An app-role session with no tenant setting sees zero rows, never an error (`migrations/0002_foundation_evidgate_schema.sql:30-33`).
3. Set the tenant with the parameterised form of `SET LOCAL` — `select set_config('evidgate.org_id', …, true)` inside the transaction — never string-built SQL (00 §5.4.2, `plans/00-overview.md:184`; read-only query transactions, 00 §5.5 C1, `plans/00-overview.md:200`).
4. A verdict that this runbook cannot produce must be escalated with its evidence (§7), not manufactured.

## 2. What "stuck" means here

The run state vocabulary is a closed CHECK (`migrations/0031_orchestration_run.sql:118-119`) and the same seven names are the state machine's vocabulary (`src/Modules/Evidgate.Modules.Orchestrator/Domain/RunStateMachine.cs:8-30`). `complete`, `incomplete` and `canceled` are terminal (`src/Modules/Evidgate.Modules.Orchestrator/Domain/RunStateMachine.cs:68-73`); the other four are the non-terminal set this runbook scans.

| State | Written by | Meaning when a run sits here past `collection_deadline_at` |
|---|---|---|
| `created` | nothing — the comment on `RunStates.Created` says a run is inserted as `queued` (`src/Modules/Evidgate.Modules.Orchestrator/Domain/RunStateMachine.cs:10`), and the CHECK still allows the literal | should be impossible in a normal flow; a row here is a defect or a hand-made fixture. Escalate (§7) |
| `queued` | the create-run insert writes the literal (`src/Modules/Evidgate.Modules.Orchestrator/Infrastructure/Persistence/OrchestratorSql.cs:125-134`, literal at `:131`), and the handler echoes it back in the response (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/CreateRunCommand.cs:539`); the column default is the never-persisted `created` (`migrations/0031_orchestration_run.sql:103`) | still `queued` means **no event batch and no finalize ever arrived** — the run is distinguishable from `running` on exactly that: a `running` run accepted events (or finalized) and then stopped short of `collecting`. Either way no reconcile was ever queued. Escalate (§7) |
| `running` | two writers, both internal: the first accepted **event batch** moves `queued → running` (`src/Modules/Evidgate.Modules.Ingestion/Application/Commands/IngestEventBatch.cs:228-241`, literal at `:127`), and a finalize that arrives before any event steps `queued → running` first (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/FinalizeShardCommand.cs:296-308`) | events were accepted (or a finalize landed) but nothing ever opened `collecting`, so no reconcile was queued. Escalate (§7) |
| `collecting` | the first finalize (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/FinalizeShardCommand.cs:310-321`) | the sweep's target: the reconciler has not produced a verdict yet. Follow §5.1 |
| `complete` / `incomplete` / `canceled` | the finisher (`collecting → complete \| incomplete`, `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileFinisher.cs:297-320`) and, for `canceled`, the cancel route (§5.3) | terminal; not stuck. Out of scope |

Two facts to keep straight, because they decide everything in §5:

- **The reconcile attempt decides three cases and refuses the rest.** It compares `collecting`; it closes an overdue `queued`/`running` run at its deadline (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/RunReconciler.cs:63-66`); and a trigger carrying the `late_evidence` marker appends a revision to an `incomplete` run (`:57-58`). Every other state answers `NotCollectable` (`:69-72`), and the handler then reaches no finisher at all (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileRunTask.cs:78-87`).
- **The adjacency table allows the edges the operator might want** — `queued → incomplete`, `running → incomplete`, and `→ canceled` from every non-terminal state (`src/Modules/Evidgate.Modules.Orchestrator/Domain/RunStateMachine.cs:78-89`). The deadline sweep drives the two `incomplete` edges (§5.2); the cancel route drives `→ canceled` (§5.3).

## 3. Detection (T2.1)

### 3.1 One tenant, as the app role

```sql
-- Role: evidgate_app. Scope: one tenant (RLS). Read-only.
-- migrations/0031_orchestration_run.sql:88-131 (table), :143-150 (FORCE RLS + run_tenant policy)
start transaction read only;
select set_config('evidgate.org_id', :'org_id', true);
select id,
       project_id,
       state,
       collection_deadline_at,
       now() - collection_deadline_at as overdue_by,
       created_at,
       updated_at,
       version,
       current_evidence_revision_id
  from public.run
 where state in ('created', 'queued', 'running', 'collecting')
   and collection_deadline_at <= now()
 order by collection_deadline_at, id;
commit;
```

Notes.

- `set_config(…, true)` is transaction-local, so it dies with the `commit` (00 §5.4.2). Without it the tenant policy matches nothing and you get zero rows, not an error (`migrations/0002_foundation_evidgate_schema.sql:30-33`).
- `collection_deadline_at` is `not null` on every run (`migrations/0031_orchestration_run.sql:104`), so this predicate also matches a `queued` run that never started: that is the intended meaning of "past its deadline".
- `current_evidence_revision_id` is the pointer the finisher sets (`migrations/0036_orchestration_run_state_grant.sql:31-32`); a non-null pointer on a `collecting` run is worth recording in the ticket.
- Cost: `run_overdue_idx` covers `queued`, `running` and `collecting` (`migrations/0058_reconcile_overdue_running.sql:59-61`), so those three literals are index seeks; `created` alone is a sequential scan of `run`, which is fine at pilot volume and is why this query is a maintenance-window tool, not a per-page one.

### 3.2 Every tenant, via a platform/DBA read session (break-glass)

```sql
-- Role: evidgate_owner, run by the platform/DBA owner as a break-glass read session. Read-only.
-- migrations/0031_orchestration_run.sql:161-163 (run_owner_backfill policy, using (true))
start transaction read only;
select org_id,
       id,
       project_id,
       state,
       collection_deadline_at,
       now() - collection_deadline_at as overdue_by,
       created_at,
       updated_at
  from public.run
 where state in ('created', 'queued', 'running', 'collecting')
   and collection_deadline_at <= now()
 order by org_id, collection_deadline_at, id;
commit;
```

This is the query an alert would run, and the one the staging drill (T2.2) uses. `evidgate_owner` is the only role that sees more than one tenant here; `evidgate_operator` sees nothing (§1) and `evidgate_worker_sched` has no table privilege (`migrations/0031_orchestration_run.sql:168`). `evidgate_owner` is a migration/administration credential, not an on-call one ([dead-letter runbook](dead-letter.md) §3.1), so this scan is a **break-glass action**: a platform/DBA-owned read-only session, requested per incident and recorded in the handover note (§7: who, when, why). ⚠️ Until the 02.8.1 grant-gated reader exists there is no audited path an on-call operator can run this through, and taking the credential from an application host is never the alternative.

### 3.3 Triage

| What the query shows | Class | Go to |
|---|---|---|
| `collecting`, past deadline, outbox key present and dispatched, task row `done` | sweep alive, attempt ran, verdict should exist | re-read the run and its revisions (§5.1 step 5) |
| `collecting`, past deadline, no outbox key | the sweep has not seen this run | §5.1 steps 1–2 |
| `collecting`, past deadline, task row `dead_letter` or a task row stuck `active` with an expired lease | queue-side failure | §4, then the [dead-letter runbook](dead-letter.md) — §7.1 when the dead-lettered row is a `…:reconcile:deadline[:n]` key (the sweep re-arms it while attempts remain, three at most) |
| `queued` or `running`, past deadline | the sweep now decides it (`incomplete`); a missing verdict is a chain problem, not a gap | §5.2 — verify the reconcile chain, then §7 for the spent-attempt case |
| `created`, past deadline | abnormal row | §7 — escalate |
| terminal state | not stuck | stop; if the verdict looks wrong, that is a contract defect, not this runbook |

## 4. Lease and epoch inspection (00 §5.3)

00 §5.3 (`plans/00-overview.md:177-179`) describes two lease mechanisms. Only one of them exists in the schema today: **the `task` queue lease**. The runner `job`/`job_lease` tables with a per-lease `epoch` are V2 (`plans/00-overview.md:178`) and no migration creates them — a `job_lease` search over `migrations/` returns nothing. `[OPEN]`

**The run row itself carries no lease and no epoch.** Its columns are `migrations/0031_orchestration_run.sql:88-131` plus `current_evidence_revision_id` (`migrations/0036_orchestration_run_state_grant.sql:31-32`); the only liveness signals are `state`, `collection_deadline_at` and `updated_at`. Do not look for a heartbeat column on `run`; there is none. `[INFERENCE]` from the column list, verified by reading it.

The closest thing to an epoch on the run path is on the event: `public.event.epoch` (`migrations/0034_ingestion_event_and_batch.sql:103`, `:131`), and an event from a stale epoch is one of the eight `missing[]` reasons, `stale_epoch` (`migrations/0049_orchestration_evidence_revision.sql:106`). A stale-epoch event never completes the current job (00 §5.3) — it shows up as a reason on the revision, not as a column you inspect.

### 4.1 The queue lease of one reconcile

```sql
-- Role: evidgate_app. Read-only. `public.task` has no tenant policy, so filter explicitly.
-- migrations/0008_platform_task_queue.sql:94-129 (table), :103-113 (state, attempts, max_attempts,
-- locked_until, version), :135 (task_active_idx)
select id,
       state,
       attempts,
       max_attempts,
       available_at,
       locked_until,
       last_error,
       created_at,
       updated_at
  from public.task
 where task_type = 'orchestrator.run.reconcile'
   and payload ->> 'run_id' = :'run_id'
 order by created_at desc;
```

How to read it.

- `state` is one of `queued`, `active`, `retry`, `dead_letter`, `done` (`migrations/0008_platform_task_queue.sql:113`). `active` means a worker holds the row; the hold is `locked_until = now() + visibility timeout`, 300 s by default (`src/BuildingBlocks/Persistence/Tasks/TasksOptions.cs:34`), stamped by the claim (`src/BuildingBlocks/Persistence/Tasks/TaskQueueSql.cs:137-150`).
- An `active` row with `locked_until <= now()` is reclaimed on the next claim pass while `attempts < max_attempts` (`src/BuildingBlocks/Persistence/Tasks/TaskQueueSql.cs:34-50`), and moved to `dead_letter` once the attempts are used up (`max_attempts` defaults to 8, `migrations/0008_platform_task_queue.sql:105`; dead-letter statement `src/BuildingBlocks/Persistence/Tasks/TaskQueueSql.cs:65-77`). So an expired lease is **not** by itself an incident; an expired lease on a row that keeps reaching the cap is.
- `last_error` is the only error text stored, capped at 1000 characters (`migrations/0008_platform_task_queue.sql:108`, `:127`). Treat it as untrusted free text: quote the run and task ids, not the message body, into tickets.
- `payload ->> 'run_id'` works because the reconcile payload is snake_case on the wire and carries `run_id` (`src/Modules/Evidgate.Modules.Orchestrator.Contracts/RunReconcileTask.cs:151-160`). `org_id` is nullable on `task` (`migrations/0008_platform_task_queue.sql:96`) — a schedule fire such as the sweep's own row carries `OrgId = null` (`src/BuildingBlocks/Persistence/Tasks/TaskScheduleRunner.cs:134`), so do not filter on it.

### 4.2 The reconcile trigger chain of one run

```sql
-- Role: evidgate_app. Read-only. `public.outbox` has no tenant policy (0006), so org_id is explicit.
-- migrations/0006_platform_outbox.sql:73-82 (table and columns), :94 (outbox_org_idem_uq on (org_id, idempotency_key))
select id,
       idempotency_key,
       task_type,
       dispatched_at,
       created_at
  from public.outbox
 where org_id = :'org_id'
   and idempotency_key like 'orchestrator:run:' || :'run_id' || ':reconcile:deadline%'
 order by idempotency_key;
```

- Those keys are the collection-deadline trigger, at most one row per attempt — `…:reconcile:deadline` for the first, `…:reconcile:deadline:{attempt}` for a re-arm, three attempts in total (`src/Modules/Evidgate.Modules.Orchestrator.Contracts/RunReconcileTask.cs:62`, `:76-81`); `:'run_id'` must be the hyphenated lower-case UUID form the key uses.
- `dispatched_at` is null until the dispatcher moves the row into `task` (`migrations/0006_platform_outbox.sql:82`; index `outbox_undispatched_idx`, `:95`). The dispatcher derives the task row's key as `'outbox:' || outbox.id` (`migrations/0006_platform_outbox.sql:46-51`), so once dispatched you can find the task row by that key too.
- A run that never had a finalize has no per-producer key either: those are `orchestrator:run:{run}:reconcile:{producer}` (`src/Modules/Evidgate.Modules.Orchestrator.Contracts/RunReconcileTask.cs:35-42`), enqueued inside the finalize transaction (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/FinalizeShardCommand.cs:323-332`).

### 4.3 Is the sweep running at all?

```sql
-- Role: evidgate_app. Read-only.
-- migrations/0008_platform_task_queue.sql:132 (unique (schedule_name, fire_at)) and :94-129
select state,
       count(*) as fires,
       max(created_at) as last_fire
  from public.task
 where task_type = 'orchestrator.run.reconcile.deadline'
   and created_at > now() - interval '2 hours'
 group by state
 order by state;
```

Expected: a fire every ten minutes — the schedule's cron is `*/10 * * * *` (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileDeadlineSweepTask.cs:56`) on the `maintenance` queue (`src/BuildingBlocks/Persistence/Tasks/EvidgateSweeps.cs:17`), so roughly twelve rows in two hours and a `last_fire` younger than ~20 minutes. A schedule fire cannot pile up: a fire for a still-open schedule is dropped, and one task per fire is guaranteed by the unique key (`migrations/0008_platform_task_queue.sql:132`; 00 §5.3, `plans/00-overview.md:177`). No rows at all means the scheduler, the dispatcher or the worker is down — that is a platform incident, not a stuck run.

## 5. Reconcile-or-cancel decision

### 5.0 The command surface this runbook relies on

Before the decision itself, the whole surface, so no step below points at a path that does not exist. "Internal" means the code path exists but no HTTP route or operator route reaches it, so a human cannot trigger it.

| Path | Reaches an operator? | Where |
|---|---|---|
| Run cancel (`POST /v1/runs/{r}/cancel`) | **yes** — a resolved member holding `run:cancel` | route `src/Modules/Evidgate.Modules.Orchestrator/Endpoints/OrchestratorEndpoints.cs:100`; command and handler `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/CancelRunCommand.cs`; request row `src/Modules/Evidgate.Modules.Orchestrator/Infrastructure/Persistence/RunCancelSql.cs:14-19`; table `migrations/0069_orchestration_run_cancel.sql:50-73`; contract `contracts/openapi.yaml:1527-1553` |
| Run state transition (the guarded edge writer) | internal only | `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/TransitionRunStateCommand.cs:29`, adjacency check `:102-113` |
| Finalize a shard (`POST /v1/runs/{r}/finalize`) — opens `collecting`, enqueues the reconcile | yes, tenant principal with the run's permission | route `src/Modules/Evidgate.Modules.Orchestrator/Endpoints/OrchestratorEndpoints.cs:89`; body `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/FinalizeShardCommand.cs:296-332` |
| Ingest an event batch (`POST /v1/runs/{r}/events`) — moves `queued → running` on the first accepted batch | yes, a producer credential | route `src/Modules/Evidgate.Modules.Ingestion/Endpoints/IngestionEndpoints.cs:49`; transition `src/Modules/Evidgate.Modules.Ingestion/Application/Commands/IngestEventBatch.cs:228-241` |
| Reconcile attempt (`orchestrator.run.reconcile`, tenant-bound) | worker only | `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileRunTask.cs:31-34` |
| Collection-deadline sweep (`orchestrator.run.reconcile.deadline`) | worker only, cron `*/10 * * * *` | `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileDeadlineSweepTask.cs:33-61` |
| Evidence finisher (`collecting → complete \| incomplete`) | internal only | `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileFinisher.cs:297-320` |
| Task dead-letter list and replay (`/internal/operator/tasks/dead-letter…`) | registered, but **denied**: the composed gate refuses every call | `src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:39-45`, default gate `:67` |

Consequence: the human-reachable levers on this base are the event batch and `finalize` — both producer actions that move a run forward (`queued → running → collecting`) — and the cancel route, which moves any non-terminal run to terminal `canceled` but stops execution only later (§5.3). None of them opens a verdict. Everything else in this section is either automated (and verified, not driven) or escalated.

### 5.1 `collecting` past its deadline: reconcile (the sweep owns it)

The sweep exists for exactly this case and for its two earlier siblings: the scan selects `state in ('queued', 'running', 'collecting') and collection_deadline_at <= now()` with the database clock, keyset-paged (`migrations/0058_reconcile_overdue_running.sql:77-78`, body at `:74-82`), with `created` deliberately excluded (`:24-26`). The handler then queues one `orchestrator.run.reconcile` per run, inside that run's own tenant scope, at most three attempts per run (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileDeadlineSweepTask.cs:121-176`, enqueue at `:236-290`). The attempt compares, and the finisher moves `collecting → complete | incomplete` (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileFinisher.cs:297-320`) — `incomplete` when the deadline passed without full evidence, which is the AC3 settle target.

So the operator's job on a `collecting` run is **to verify the chain, not to decide the verdict**:

1. **Is the sweep firing?** §4.3. Nothing in two hours → platform incident; hand off, do not touch the run.
2. **Did the scan name this run?** §4.2. Key absent after at least one fire means the scan did not select it. Re-run §3.1 and confirm the state is one of the three the scan names (`queued`, `running`, `collecting`) and the deadline really is in the past **on the database clock** — the function and the attempt both use `now()` (`migrations/0058_reconcile_overdue_running.sql:78`), not an application clock. `[INFERENCE]`: a skew between your laptop and the database is the usual explanation.
3. **Key present, `dispatched_at` null past one poll interval.** The outbox dispatcher is behind. That is the queue path, not the run: see the [dead-letter runbook](dead-letter.md) and the dispatcher's own health, and escalate if it is down.
4. **Task row exists, `state = 'active'` with an expired lease.** Normal — the next claim pass reclaims it (§4.1). **`state = 'dead_letter'`**: the attempts are gone and the replay path is the operator ingress, which denies every call today (§5.3), so escalate with the task id. When the dead-lettered row is a `…:reconcile:deadline[:n]` key, the sweep re-arms the run on its next fire while attempts remain — three in total, warning event `2427` after that — so recovery steps are [dead-letter runbook](dead-letter.md) §7.1.
5. **Task row `done` and the run still open.** First check *which* trigger that row came from (§4.2 lists both key shapes). A per-producer trigger that finishes `pending` before the deadline is normal: an open shard before the deadline is not a verdict, so the run stays `collecting` (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/RunReconciler.cs:117-122`). A **deadline** row (`…:reconcile:deadline[:n]`) that is `done` while the run is still `queued`, `running` or `collecting` left no verdict, so that attempt is spent and the sweep queues the next one on its next fire (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileDeadlineSweepTask.cs:335-343`); wait one fire before investigating, and if the run is still open after attempts are exhausted (event `2427`) take it to the Orchestrator owner with the task ids, the run id and the timestamps. A `done` attempt that *did* decide the run is normal and needs nothing.

```sql
-- Role: evidgate_app, inside a tenant-scoped read-only transaction. Read-only.
-- migrations/0049_orchestration_evidence_revision.sql:78-89 (columns), :118-119 (index)
start transaction read only;
select set_config('evidgate.org_id', :'org_id', true);
select revision_no, completeness, missing, outcome, created_at
  from public.evidence_revision
 where org_id = :'org_id'
   and run_id = :'run_id'
 order by revision_no desc;
commit;
```

A revision is **the** verdict record and it is append-only (`migrations/0049_orchestration_evidence_revision.sql:162-165`: `revoke update, delete`; insert guard trigger at `:123-142`). `missing` is the closed list of eight reasons, including `missing_result` for a planned execution with no terminal attempt (`migrations/0056_reconcile_missing_result.sql:58-66`). Zero rows here is **not** by itself "no verdict exists": the tenant policy matches nothing without the `set_config` of this transaction, so a missing GUC also returns zero rows and never an error (`migrations/0002_foundation_evidgate_schema.sql:30-33`). Confirm the session is tenant-scoped by re-running §3.1 first — if §3.1 returns this run and this query still shows nothing, no verdict exists, and nothing in this runbook may create one (§6).

### 5.2 `queued` or `running` past the deadline: the sweep closes them `incomplete`

Since `migrations/0058_reconcile_overdue_running.sql` these two states are no longer a gap. The same scan names them (`:77-78`), the handler queues a deadline attempt, and the reconciler decides an overdue `queued`/`running` run instead of answering `NotCollectable` (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/RunReconciler.cs:60-72`), through the declared edges `queued → incomplete` and `running → incomplete` (`src/Modules/Evidgate.Modules.Orchestrator/Domain/RunStateMachine.cs:83-84`). The forced `incomplete` carries at least one reason from 00 §8.5's closed list — `missing_shard` for an open or never-started producer slot, `missing_result` for a planned execution with no terminal attempt — so a run whose executor died before the first event or finalize gets a verdict with reasons, never a PASS.

Verify the chain as in §5.1, with three differences:

- Only the deadline trigger decides these states. Any trigger without the late marker, and any trigger on a run outside `collecting`, answers `NotCollectable` (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/RunReconciler.cs:69-72`); the single exception is a late-evidence trigger on a finished run — `incomplete`, or `complete` inside the window that starts at `run.completed_at` (`migrations/0114_run_completed_at.sql`) — which appends a late revision and never moves a `queued`/`running` run (`:53-65`). A reprocess keeps its own marker and disposition. So a reconcile row whose key carries no `deadline` segment proves nothing about a `queued`/`running` run.
- A `done` deadline attempt that leaves the run open counts as spent: the sweep queues the next attempt on its next fire (§5.1 step 5). Only after the bound (event `2427`) does the run need the Orchestrator owner.
- Three spent attempts — dead letter, `done` with the run still open, or a task row purged after dispatch (`ReconcileDeadlineSweepTask.cs:335-343`) — are the end of the automatic path: warning event `2427` once per fire, nothing queued after it ([dead-letter runbook](dead-letter.md) §7.1).

**Do this instead.** Record the run id, org id, state, `collection_deadline_at`, `created_at`, `updated_at` and the deadline outbox keys that exist (§4.2), then escalate with the task ids once the attempts are spent. Do not hand-write a state transition and do not insert an outbox row (§6).

`created` stays outside the scan on purpose: no shard plan is committed for it and `created → incomplete` is not a declared edge (`migrations/0058_reconcile_overdue_running.sql:24-26`), so a `created` row past its deadline is abnormal and is escalated, never closed by the sweep.

### 5.3 `canceled`: recorded now, execution stopped later

The cancel route is served. `POST /v1/runs/{r}/cancel` — `operationId: cancelRun`, `202` "recorded, not stopped", permission `run:cancel` (`contracts/openapi.yaml:1527-1553`), generated into the SDK (`packages/sdk/gen/sdk.gen.ts:343`), catalogue row `migrations/0010_identity_permission_catalogue.sql:348` — is mapped at `src/Modules/Evidgate.Modules.Orchestrator/Endpoints/OrchestratorEndpoints.cs:100` and handled by `CancelRunCommandHandler` (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/CancelRunCommand.cs`). A resolved member holding `run:cancel` can call it.

One call decides and records in the tenant transaction. The guarded `* → canceled` edge (`TransitionRunStateCommand`, §5.0) moves the run to `canceled` **immediately**, and a run that actually moved gets exactly one `run_cancel_request` row — requester, optional `reason`, `requested_at` and both stop windows (`src/Modules/Evidgate.Modules.Orchestrator/Infrastructure/Persistence/RunCancelSql.cs:14-19`; table, RLS and guard `migrations/0069_orchestration_run_cancel.sql:50-73`, `:113-155`). The row's `unique (org_id, run_id)` makes it one request per run, so a repeat cancel records nothing and cannot open a second window. A terminal run (`complete`, `incomplete`, already `canceled`) is an accepted `202` no-op that writes nothing. An unknown id, another tenant's run and a sibling project's run of the same org all get the one 404 `not_found` body of `GET /v1/runs/{r}` — no existence oracle.

**Not built yet: the stop itself.** Nothing consumes `soft_stop_deadline_at` or `kill_at` on this base — no worker, task type or outbox message reads them, and the only statement on the table is the handler's insert. So a cancel makes the run terminal for every reader (the reconciler can no longer complete it) but does not stop a producer or agent that keeps executing; killing execution is the follow-up those windows exist for. Never present a cancel as proof that execution ended.

For a drill: **AC3's "settled to `incomplete` or `canceled`" is satisfiable through either** — cancel a non-terminal run, then confirm `run.state = 'canceled'`, exactly one `run_cancel_request` row, and a `run.state_changed` audit entry credited to the caller. Do not assert from the row that the executor stopped.

Also note: the operator ingress that would carry any future run-level operator action is fail-closed today. It registers only the task dead-letter list and replay plus the system healing switch (`src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:39-45`, `:49-54`), and the default gate denies everything (`src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:58-61`, `:67`; `src/Evidgate.Api/OperatorIngress/OperatorGate.cs:30-34`).

## 6. Never produce evidence or fabricate a result (AC3)

The run's outcome is the reconciler's comparison of planned shards, acknowledged events and recorded attempts (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/RunReconciler.cs:74-137`), persisted as an `evidence_revision` and then applied to the run state in the same transaction (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileFinisher.cs:256-320`). Nothing else may write it. Therefore, in this runbook's scope:

- **No `INSERT` into `evidence_revision`, ever.** It is append-only with a gap-free `revision_no` guard (`migrations/0049_orchestration_evidence_revision.sql:123-142`, `:162-165`); a hand-written row would be a fabricated verdict and would break the sequence guard.
- **No `UPDATE public.run`**, in any state, for any reason. It bypasses the adjacency check, the `version` guard and the `run.state_changed` audit edge (`migrations/0031_orchestration_run.sql:130`, `:136-141`; `migrations/0036_orchestration_run_state_grant.sql:14-22`).
- **No "settling" a run by editing a `task` row** to make a retry land where you want it. Replay exists for a dead-lettered row and it re-runs the handler; the handler decides, you do not.
- **No re-running a finalize to force a verdict.** A duplicate finalize of a closed slot changes nothing and enqueues nothing (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/FinalizeShardCommand.cs:279-294`).
- **A missing verdict stays missing.** `incomplete` with a reason is a *result* the platform produced; "no revision" is the honest answer when nothing ran. Report it as such; do not translate it into a state change.

If the incident needs an outcome the platform cannot produce, the answer is the escalation in §5.2/§7, not a query.

## 7. Known gaps

**The S152 carried gap is closed (S165); its residual is fairness, not liveness.** The collection-deadline sweep now names `queued`, `running` and `collecting` (`migrations/0058_reconcile_overdue_running.sql:77-78`) and closes a pre-collecting run past `collection_deadline_at` as `incomplete` with reasons (§5.2), so a run whose executor died before the first event or finalize no longer waits forever. What remains is budget: a fire drains at most 10 pages of 100 runs (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileDeadlineSweepTask.cs:62,69`), the keyset cursor restarts at the first org every fire, and a run whose three attempts are spent (or that simply stays overdue) keeps being scanned without progress — so a tenant late in `(org_id, id)` order can be starved by runs ahead of it. Warning event `2426` marks a fire that hit the page bound and `2427` marks the runs that spent their attempts (`ReconcileDeadlineSweepTask.cs:351-361`); a carried row records this fairness gap at close-out.

Other gaps this runbook states plainly.

- **`created` stays outside the scan.** The predicate names three literals (`migrations/0058_reconcile_overdue_running.sql:77-78`) and excludes `created` because no shard plan is committed for it and `created → incomplete` is not a declared edge (`:24-26`); a `created` row past its deadline is abnormal and escalated (§5.2).
- **The deadline re-arm is bounded, not unlimited.** The S152 dead-letter gap (`docs/status-carried.md:420`) is closed: the sweep queues attempt `n+1` once attempt `n` is spent — dead-lettered, `done` while the run was still open, or its task row purged after dispatch (`src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/ReconcileDeadlineSweepTask.cs:335-343`) — up to three (`src/Modules/Evidgate.Modules.Orchestrator.Contracts/RunReconcileTask.cs:62`), and logs warning event `2427` for the runs that spent all three, after which nothing more is queued and the run needs an operator. Recovery steps are [dead-letter runbook](dead-letter.md) §7.1.
- **No runner lease or epoch to inspect.** `job` / `job_lease` with a per-lease `epoch` (00 §5.3, `plans/00-overview.md:178`) is V2 and has no migration; `run` has no lease or heartbeat column. §4 inspects the `task` lease instead, and says so. `[OPEN]`
- **Cancel stops nothing yet.** The route is served and records the request (§5.3), but no worker, task type or outbox message consumes `soft_stop_deadline_at` / `kill_at`, so an executor that keeps running after a cancel is not stopped by this base.
- **The operator ingress denies every call** (§5.3), so the dead-letter replay route the queue path needs is not usable by a human today either.
- **The alert link does not exist yet.** 10.7.1-T2.1's done-when is "runbook committed and linked from the stuck-run/incomplete-rate alert" (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:105`); the alert rules themselves are 10.3.1-T5 and are not in the tree. `[OPEN]`
- **No drill has run.** T2.2 (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:106`) is a staging drill by a non-author: kill a worker mid-run, then follow this runbook. `[INFERENCE]` recipe for the drill, from the state table: create a run with two producer slots, finalize one so the run enters `collecting`, and let `collection_deadline_at` pass without the second finalize; the sweep then settles it to `incomplete`, or cancel it through the route and read `canceled`; both are the observable AC3 outcomes (§5.3). Use fixture tenants and synthetic identifiers only.

## 8. Evidence table

Every SQL identifier this runbook uses, and where it is created or granted. The PR that adds this file copies this table into its body.

| Identifier | Kind | Cite |
|---|---|---|
| `public.run` | table | `migrations/0031_orchestration_run.sql:88-131` |
| `run.state` + `run_state_check` (`created`, `queued`, `running`, `collecting`, `complete`, `incomplete`, `canceled`) | column + CHECK | `migrations/0031_orchestration_run.sql:103`, `:118-119` |
| `run.collection_deadline_at` | column (`not null`) | `migrations/0031_orchestration_run.sql:104` |
| `run.version`, `run.updated_at` | columns (shared trigger) | `migrations/0031_orchestration_run.sql:106-108`, `:136-141` |
| `run.current_evidence_revision_id` | column (0036) + app write grant (0049) | `migrations/0036_orchestration_run_state_grant.sql:31-32`; `migrations/0049_orchestration_evidence_revision.sql:255` |
| `run_project_created_idx` | index | `migrations/0031_orchestration_run.sql:134` |
| `run` RLS: enable + force, `run_tenant` (app, `evidgate.current_org_id()`), `run_owner_backfill` (owner, `true`) | policies | `migrations/0031_orchestration_run.sql:143-150`, `:161-163` |
| `run` grants: app `select, insert` + `revoke update, delete`; operator `select` (no policy → zero rows); worker_sched `revoke all` | grants | `migrations/0031_orchestration_run.sql:152-155`, `:165-168` |
| `run` column grants to `evidgate_app`: `update (updated_at)`, `update (state)`, `update (current_evidence_revision_id)` — no table-level `UPDATE` | column grants | `migrations/0034_ingestion_event_and_batch.sql:252`; `migrations/0036_orchestration_run_state_grant.sql:34`; `migrations/0049_orchestration_evidence_revision.sql:255`; table-level `revoke update, delete` `migrations/0031_orchestration_run.sql:165` |
| `run.completed_at` (nullable; stamped once by `collecting -> complete`) + `run_completed_at_state_check` + `run_completed_at_guard_trg`/`run_completed_at_insert_guard_trg`: app UPDATE is `42501` (no column grant); a value change by any other role is `23514 run_completed_at_immutable`; INSERT with a non-null instant is `23514 run_completed_at_insert_refused` | column + CHECK + triggers | `migrations/0114_run_completed_at.sql` |
| `guard_run_evidence_pointer` + trigger `run_evidence_pointer_guard` (the only DB guard on those grants) | function + trigger | `migrations/0049_orchestration_evidence_revision.sql:186-219` |
| `evidgate.current_org_id()` | function (`nullif(current_setting('evidgate.org_id', true), '')`) | `migrations/0002_foundation_evidgate_schema.sql:30-33` |
| `evidgate.reconcile_overdue_runs(uuid, uuid, integer)` | function (stable, security definer, `search_path = ''`; selects `queued`, `running`, `collecting`) | `migrations/0058_reconcile_overdue_running.sql:65-83` (`create or replace` at `:65`) |
| `reconcile_overdue_runs` grants: `revoke all from public`, `grant execute to evidgate_worker_sched` | grants | `migrations/0058_reconcile_overdue_running.sql:85-87` |
| `run_overdue_idx` (`where state in ('queued', 'running', 'collecting')`; replaced `run_collecting_idx`) | index | `migrations/0058_reconcile_overdue_running.sql:59-61`, drop at `:63` |
| `public.evidence_revision` | table | `migrations/0049_orchestration_evidence_revision.sql:78-89` |
| `evidence_revision.completeness` (`complete \| incomplete`) | column + CHECK | `migrations/0049_orchestration_evidence_revision.sql:83`, `:97` |
| `evidence_revision.missing` (`text[]`, closed reason list incl. `stale_epoch`, `missing_result`) | column + CHECK | `migrations/0049_orchestration_evidence_revision.sql:84`, `:102-110`; `migrations/0056_reconcile_missing_result.sql:58-66` |
| `evidence_revision.revision_no` + gap-free insert guard | column + trigger | `migrations/0049_orchestration_evidence_revision.sql:82`, `:123-142` |
| `evidence_revision` RLS: force, `evidence_revision_tenant`, `evidence_revision_owner_backfill` | policies | `migrations/0049_orchestration_evidence_revision.sql:145-160` |
| `evidence_revision` grants: append-only for app (`revoke update, delete`), operator `select` | grants | `migrations/0049_orchestration_evidence_revision.sql:162-165` |
| `public.task` | table | `migrations/0008_platform_task_queue.sql:94-129` |
| `task.state` (`queued`, `active`, `retry`, `dead_letter`, `done`) | column + CHECK | `migrations/0008_platform_task_queue.sql:103`, `:113` |
| `task.attempts`, `task.max_attempts` (default 8), `task.available_at`, `task.locked_until`, `task.last_error`, `task.version` | columns | `migrations/0008_platform_task_queue.sql:104-111`, caps `:117-128` |
| `task.org_id` (nullable; platform tasks carry none) | column | `migrations/0008_platform_task_queue.sql:96` |
| `task_active_idx` (`where state = 'active'`), `task_dead_idx`, `task_terminal_idx` | indexes | `migrations/0008_platform_task_queue.sql:135-137` |
| `task` unique `(schedule_name, fire_at)` | unique index | `migrations/0008_platform_task_queue.sql:132` |
| `task` grants: app full DML (retention sweep owns the DELETE); operator and worker_sched `revoke all` | grants | `migrations/0008_platform_task_queue.sql:155-157` |
| `public.outbox` | table | `migrations/0006_platform_outbox.sql:73-82` |
| `outbox.dispatched_at`, `outbox_undispatched_idx` | column + index | `migrations/0006_platform_outbox.sql:82`, `:95` |
| `outbox_org_idem_uq` unique `(org_id, idempotency_key)` | unique index | `migrations/0006_platform_outbox.sql:94` |
| `public.outbox` has no tenant policy (org filter is explicit) | design note | `migrations/0006_platform_outbox.sql:14`; also `src/Modules/Evidgate.Modules.Orchestrator/Infrastructure/Persistence/ReconcileDeadlineSql.cs:19-23` |
| `public.event.epoch` | column + CHECK | `migrations/0034_ingestion_event_and_batch.sql:103`, `:131` |
| `run:cancel` permission | catalogue row (route served) | `migrations/0010_identity_permission_catalogue.sql:348`; route `contracts/openapi.yaml:1527-1553`, generated `packages/sdk/gen/sdk.gen.ts:343` |
| `POST /runs/{r}/cancel` served, `run:cancel`, `202` recorded | route + handler | `src/Modules/Evidgate.Modules.Orchestrator/Endpoints/OrchestratorEndpoints.cs:100`; `src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/CancelRunCommand.cs` |
| `public.run_cancel_request` (one request per run, no DELETE) | table + CHECKs + column grants | `migrations/0069_orchestration_run_cancel.sql:50-73`, grants `:102-108`, immutability/state guard `:113-155` |
| `run.org_id`, `run.project_id`, `run.created_at` | columns | `migrations/0031_orchestration_run.sql:90-91`, `:107` |
| `task.task_type`, `task.payload`, `task.created_at` | columns | `migrations/0008_platform_task_queue.sql:98-99`, `:109` |
| `outbox.org_id`, `outbox.task_type`, `outbox.payload`, `outbox.idempotency_key`, `outbox.created_at` | columns | `migrations/0006_platform_outbox.sql:75-81` |
| `evidence_revision.org_id`, `.run_id`, `.outcome`, `.created_at` | columns | `migrations/0049_orchestration_evidence_revision.sql:80-81`, `:85`, `:89` |
| `set_config(text, text, boolean)` — the transaction-local tenant setting the policies read | built-in, used per 00 §5.4.2 | pattern `plans/00-overview.md:184`; read side `migrations/0002_foundation_evidgate_schema.sql:30-33` |
| task-type literals `orchestrator.run.reconcile` and `orchestrator.run.reconcile.deadline`, the key `orchestrator:run:{run}:reconcile:deadline` and its `:{attempt}` re-arm form (bound `DeadlineMaxAttempts = 3`) | constants (not schema) | `src/Modules/Evidgate.Modules.Orchestrator.Contracts/RunReconcileTask.cs:24`, `:135`, `:77-83`, `:62` |

Aliases in the queries (`overdue_by`, `fires`, `last_fire`) are output labels, not schema identifiers.
