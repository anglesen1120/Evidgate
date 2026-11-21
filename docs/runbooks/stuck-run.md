# Stuck run runbook

Status: **10.7.1-T2.1 written, declared partial; 10.7.1-T2.2 (staging drill) is open.**

⚠️ **Drill pending.** Repository records leave T1.2/T2.2 staging drills by a non-author open (AC2/AC3). No live environment or drill result was verified by this desk check. Deployment/migration state, approved credential availability, incident query results, pilot-volume query cost, actual fire cadence, clock skew, dispatcher lag and the proposed drill's outcome still need live evidence. Nothing below claims a drill ran.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Every SQL identifier used below is mapped in the [evidence table](#8-evidence-table). Migration names refer to `migrations/`; code citations name symbols or routes rather than release-specific line numbers.

## 1. Scope, roles and rules

**Purpose.** Find a run whose worker died or whose finalize never arrived, decide whether it can be reconciled to a verdict or must be escalated, and settle it **without inventing evidence**. This is the human half of 10.7.1-T2.1: the automated half is the collection-deadline sweep (§5.1).

**Audience.** A second on-call operator who did not build the orchestrator. You need no knowledge of the code beyond this page and the cited symbols.

**In scope.** A `public.run` row in a non-terminal state that is past its `collection_deadline_at` (`plans/00-overview.md`, §8.1 Run). Also the reconcile work queued for that run.

**Out of scope.** Dead-lettered `task` rows and their replay (see the [dead-letter runbook](dead-letter.md)); orphan artifacts (00 §8.4); healing incidents (00 §8.7); a run that is terminal but wrong (a contract defect, not an operational one).

**Prerequisites.**

- A checkout of the deployed release and confirmation of its applied migrations. ⚠️ Deployment is drill-pending.
- Approved read-only sessions obtained through the environment's access process ([environments](environments.md)): `evidgate_app` for §§3.1 and 5.1's revision query; `evidgate_worker` for §§4.1–4.3, whose SELECT grants are in `0104_platform_worker_role`; `evidgate_owner` only for §3.2's emergency cross-tenant run scan. Never retrieve service credentials from an application host.
- Define the `psql` variables `org_id` and `run_id` as valid UUIDs before using the SQL. `:'org_id'` and `:'run_id'` are `psql` interpolation, not server-side parameter syntax. Never paste customer names, URLs or repository names into a ticket.
- The detection half does not need the product API or CLI. It does need the approved database roles above; credential availability and actual results remain drill-pending.

**Roles.** Six database roles exist after `0104_platform_worker_role` and `0105_platform_operator_role`: the four original roles plus `evidgate_worker` and `evidgate_platform_operator`. For this runbook:

| Role | What it can read here | Use it for |
|---|---|---|
| `evidgate_app` | one org's `run`/`evidence_revision` rows after transaction-local `evidgate.org_id`; raw `task`/`outbox` are INSERT-only after `0104_platform_worker_role`; scoped projections expose only selected queue fields | §§3.1 and 5.1's revision query, not §§4.1–4.3 |
| `evidgate_worker` | raw `task` SELECT and `outbox` SELECT, without tenant RLS (`0104_platform_worker_role`) | approved, explicitly read-only sessions for §§4.1–4.3; this role also has task DML, so the read-only guard is mandatory |
| `evidgate_owner` | every org's run rows through `run_owner_backfill` (`0031_orchestration_run`) | §3.2 emergency read-only scan only: it is a migration/administration credential, not ordinary on-call access ([dead-letter runbook](dead-letter.md) §3.1) |
| `evidgate_operator` | run/evidence SELECT grants exist, but these tables have no matching operator policy, so return zero rows (`0031_orchestration_run`, `0049_orchestration_evidence_revision`) | nothing in this runbook; newer support-grant policies on other tables do not grant these reads |
| `evidgate_worker_sched` | no run table privilege; EXECUTE on `evidgate.reconcile_overdue_runs` (`0058_reconcile_overdue_running`) | automated schedule/sweep path only |
| `evidgate_platform_operator` | restricted operator-facade role created by `0105_platform_operator_role`, not a substitute for raw queue SELECT or tenant-run access | no supplied query uses this role; do not confuse it with `evidgate_operator` |

**Rules.**

1. Every inspection below is read-only and wrapped in `start transaction read only` / `commit`. Never hand-write `UPDATE`/`DELETE`/`INSERT`. The app has no table-level run UPDATE (`0031_orchestration_run`); its three column grants are `updated_at` (`0034_ingestion_event_and_batch`), `state` (`0036_orchestration_run_state_grant`) and `current_evidence_revision_id` (`0049_orchestration_evidence_revision`).
2. The database guards both the latest same-run evidence pointer (`guard_run_evidence_pointer`, `0049_orchestration_evidence_revision`) and allowed state edges (`run_guard_transition_trg`, `0099_run_slot_transition_guards`, replaced function in `0114_run_completed_at`). Manual state writes still bypass `TransitionRunStateCommandHandler`'s expected-version comparison and canonical audit/outbox work; the shared version/timestamp trigger does not replace those checks.
3. Tenant tables use FORCE RLS (`0031_orchestration_run`, `0049_orchestration_evidence_revision`). Missing/empty tenant context yields zero app-role rows, not an error (`evidgate.current_org_id`, `0002_foundation_evidgate_schema`). Set the tenant using `select set_config('evidgate.org_id', …, true)` inside the transaction, never string-built SQL (`plans/00-overview.md`, §§5.4.2 and 5.5 C1).
4. A verdict this runbook cannot produce must be escalated with its evidence (§7), not manufactured.
5. SQL was manually desk-checked against the final migration schema; **parse not machine-verified**. Actual permissions, results and execution plans remain drill-pending.

## 2. What "stuck" means here

`run_state_check` in `0031_orchestration_run` and `RunStates`/`RunStateMachine` define the same seven states. `complete`, `incomplete` and `canceled` are terminal; the other four are the detection set.

| State | Written by | Meaning past `collection_deadline_at` |
|---|---|---|
| `created` | schema default; normal `OrchestratorSql.InsertRun` explicitly inserts `queued` (`RunStates.Created` documents this) | abnormal in the normal create flow; escalate, do not invent a shard plan |
| `queued` | `OrchestratorSql.InsertRun`; `CreateRunCommandHandler` returns `queued` | no committed accepted event batch or successful finalize has started collection in the normal flow; a deadline reconcile may already be queued. Follow §5.2 |
| `running` | accepted `IngestEventBatch` moves `queued → running`; first finalize also traverses this edge before entering collecting in the same transaction | normally accepted events without a successful finalize. A successful finalize does not leave a committed intermediate `running` state. Deadline reconcile may be queued; follow §5.2 |
| `collecting` | `FinalizeShardCommandHandler` on first successful slot close | reconciliation path; inspect the chain and revisions (§5.1), do not assume no prior revision exists |
| `complete` / `incomplete` / `canceled` | `ReconcileFinisher` for comparison settlements; cancel route for `canceled` | terminal, not stuck; a wrong verdict is outside this recovery procedure |

Two facts decide §5:

- **`RunReconciler.ReconcileAsync` supports more than ordinary collection.** It compares collecting; closes overdue queued/running on ordinary non-late, non-reprocess triggers; accepts admitted late evidence for incomplete or complete; and has distinct reprocess dispositions. A live reprocess without a prior revision waits for collection. `Pending`/`NotCollectable` do not invoke `ReconcileFinisher` (`ReconcileRunTaskHandler`).
- **The allowed edges include `queued → incomplete`, `running → incomplete`, and every non-terminal state → canceled** (`RunStateMachine`, `0114_run_completed_at`). The sweep drives deadline recovery (§5.2); cancel records the terminal cancellation (§5.3).

## 3. Detection (T2.1)

### 3.1 One tenant, as the app role

```sql
-- Role: evidgate_app; run SELECT + run_tenant policy: 0031_orchestration_run.
-- Pointer column: 0036_orchestration_run_state_grant.
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

- `set_config(…, true)` ends with this transaction; missing context returns zero rows (`evidgate.current_org_id`).
- The deadline is NOT NULL (`0031_orchestration_run`), including queued runs that never started.
- The pointer is what `ReconcileFinisher` updates; record any existing revision rather than assuming none exists.
- Cost: `run_overdue_idx` covers only queued/running/collecting (`0058_reconcile_overdue_running`). This four-state query does not imply that partial-index predicate; three seeks plus a created-only scan are not guaranteed. ⚠️ Pilot-volume cost is unmeasured and drill-pending: obtain an approved execution plan and latency/row-count evidence before choosing operational frequency.

### 3.2 Every tenant, as the owner role (emergency only)

```sql
-- Role: evidgate_owner, emergency read-only session.
-- Cross-tenant run_owner_backfill policy: 0031_orchestration_run.
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

The owner policy permits this cross-tenant scan; the app is tenant-scoped, the operator has no matching run policy and the scheduler has no run SELECT (`0031_orchestration_run`). Use `evidgate_owner` only through the emergency access process, recording who, when and why. Do not borrow an application-host credential. An approved live session and scan result remain drill-pending; a future alert could run such a scan, but no deployed alert link is claimed (§7).

### 3.3 Triage

| What the query shows | Class | Go to |
|---|---|---|
| collecting, overdue, trigger dispatched and task done | handler returned; an earlier ordinary attempt may have returned Pending rather than written a verdict | inspect trigger identity/timing and revisions (§5.1 step 5) |
| collecting, overdue, no deadline outbox key | no durable deadline trigger visible; not proof that the scan never selected it | §5.1 steps 1–2, including paging/enqueue failures |
| collecting, overdue, task dead-lettered or active with expired lease | queue-side inspection needed; expired leases alone are reclaimable | §4 and [dead-letter runbook](dead-letter.md) §7.1 for bounded deadline re-arm |
| queued or running, overdue | deadline closure is implemented; inspect the chain | §5.2; escalate spent attempts |
| created, overdue | abnormal row | §7; escalate |
| terminal | not stuck | stop; wrong verdict is outside this procedure |

## 4. Lease and epoch inspection (00 §5.3)

The implemented lease here is the task queue lease. Runner `job`/`job_lease` and per-lease epoch remain V2 (`plans/00-overview.md`, §5.3); see the **Not possible yet:** block in §7 for enabling tasks.

**Run has no lease, epoch or heartbeat column.** Its final columns include `0031_orchestration_run`, the pointer in `0036_orchestration_run_state_grant`, identity fields in `0037_run_identity`, and `completed_at` in `0114_run_completed_at`. State, deadline and update time are inspection signals, not an executor heartbeat.

`public.event.epoch` exists (`0034_ingestion_event_and_batch`); `stale_epoch` is one of the eight revision missing reasons (`0056_reconcile_missing_result`). The V2 stale-epoch fencing contract is not a present-day job lease you can inspect.

### 4.1 The queue lease of one reconcile

```sql
-- Role: evidgate_worker; raw task SELECT: 0104_platform_worker_role.
-- Table, fields, task_active_idx: 0008_platform_task_queue. No tenant RLS.
start transaction read only;
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
 where org_id = :'org_id'
   and task_type = 'orchestrator.run.reconcile'
   and payload ->> 'run_id' = :'run_id'
 order by created_at desc;
commit;
```

How to read it.

- `TaskQueueSql.ClaimBatch` claims active and stamps `locked_until`; `TasksOptions.VisibilityTimeoutSeconds` defaults to 300 seconds. State is queued/active/retry/dead_letter/done (`0008_platform_task_queue`).
- `TaskClaimer.ReclaimAsync` runs `TaskQueueSql.ReclaimExpiredLeases`, which requeues expired active leases below the cap; `TaskQueueSql.DeadLetterExpiredLeases` dead-letters at the cap. Default max attempts is eight (`0008_platform_task_queue`). Expiry alone is not a verdict or evidence of permanent failure.
- `last_error` is untrusted free text capped at 1000 characters (`0008_platform_task_queue`). Put run/task IDs, not raw error content, into tickets.
- `RunReconcilePayload` uses snake_case and is tenant-bound (`RunReconcileTaskHandler` requires tenant context). Filter org here. Only the platform schedule row uses `OrgId = null` (`TaskScheduleRunner.FireAsync`); §4.3 intentionally inspects that cross-tenant platform task type.

### 4.2 The reconcile trigger chain of one run

```sql
-- Role: evidgate_worker; outbox SELECT: 0104_platform_worker_role.
-- Table/fields/outbox_org_idem_uq: 0006_platform_outbox. No tenant RLS.
start transaction read only;
select id,
       idempotency_key,
       task_type,
       dispatched_at,
       created_at
  from public.outbox
 where org_id = :'org_id'
   and idempotency_key like 'orchestrator:run:' || :'run_id' || ':reconcile:deadline%'
 order by idempotency_key;
commit;
```

- `RunReconcileContract.DeadlineIdempotencyKey` uses `…:reconcile:deadline` first and `…:reconcile:deadline:{attempt}` thereafter; `DeadlineMaxAttempts` is three. The run UUID must use the hyphenated lower-case form used in the key.
- `dispatched_at` stays null until dispatch; `OutboxDispatcher.DispatchBatchAsync` queues the task and stamps dispatch in one transaction. The task key is `outbox:{outbox.id}` (`0006_platform_outbox`).
- Ordinary first-finalize keys are `orchestrator:run:{run}:reconcile:{producer}` (`RunReconcileContract.IdempotencyKey`), inserted by `FinalizeShardCommandHandler` in the slot-close transaction. This query filters deadline keys; it does not show those ordinary keys.

### 4.3 Is the sweep running at all?

```sql
-- Role: evidgate_worker; task SELECT: 0104_platform_worker_role.
-- Fields and unique (schedule_name, fire_at): 0008_platform_task_queue.
start transaction read only;
select state,
       count(*) as fires,
       max(created_at) as last_fire
  from public.task
 where task_type = 'orchestrator.run.reconcile.deadline'
   and created_at > now() - interval '2 hours'
 group by state
 order by state;
commit;
```

`ReconcileDeadlineSweepTask` declares cron `*/10 * * * *` on `EvidgateSweeps.MaintenanceQueue` (`maintenance`). ⚠️ Roughly twelve rows in two hours and `last_fire` younger than about twenty minutes are healthy-system expectations, not verified observations; retention, restarts, scheduler lookback and backlog affect them. The unique `(schedule_name, fire_at)` prevents duplicates for one occurrence, **not** overlap with earlier unfinished fires (`0008_platform_task_queue`, `TaskScheduleRunner.FireAsync`). Distinct fires can accumulate.

No rows warrants a platform investigation of registration, scheduler/database access, lookback and retention; it does not identify the failed component. Schedule fires enqueue directly through `TaskQueueWriter`, not through the outbox dispatcher. Actual cadence and component health remain drill-pending.

## 5. Reconcile-or-cancel decision

### 5.0 The command surface this runbook relies on

This is the recovery-relevant surface, not an exhaustive endpoint catalogue. “Internal” means **no dedicated public/operator command route**; an internal writer can still be invoked by a permitted higher-level route, as cancel invokes the guarded state writer.

| Path | Human reachability | Source |
|---|---|---|
| `POST /v1/runs/{r}/cancel` | resolved member holding `run:cancel` | `OrchestratorEndpoints`, `CancelRunCommandHandler`, OpenAPI `cancelRun`, `0069_orchestration_run_cancel` |
| Guarded state writer | no dedicated route; invoked internally by allowed workflows | `TransitionRunStateCommandHandler` |
| `POST /v1/runs/{r}/finalize` | scoped caller with `event:write`, matching producer-slot identity and valid idempotency key | `OrchestratorEndpoints`, `FinalizeShardCommandHandler`, OpenAPI `finalizeRun` |
| `POST /v1/runs/{r}/events` | producer credential satisfying the event contract | `IngestionEndpoints`, `IngestEventBatch` |
| `orchestrator.run.reconcile` | automated tenant-bound task, not a manual HTTP action | `RunReconcileTaskHandler` |
| `orchestrator.run.reconcile.deadline` | scheduled worker task | `ReconcileDeadlineSweepTask` |
| Evidence finisher | internal comparison settlement, not a manual verdict command | `ReconcileFinisher` |
| `GET /internal/operator/tasks/dead-letter`, `POST /internal/operator/tasks/dead-letter/{id}/replay` | registered but blocked by the default gate | `OperatorEndpoints`, `DenyAllOperatorGate`; §7 |
| `POST /v1/runs/{r}/reprocess` | stored-report reprocessing, not a stuck-run recovery instruction | `EvidenceEndpoints`; do not use it to manufacture a verdict |

Ordinary producer event/finalize actions can move a run forward; member cancel records canceled but proves no executor termination (§5.3). Reprocess is a separate surface. There is no human “set verdict” action: verify automation or escalate.

### 5.1 `collecting` past its deadline: reconcile (the sweep owns it)

`evidgate.reconcile_overdue_runs` (`0058_reconcile_overdue_running`) keysets queued/running/collecting with `collection_deadline_at <= now()` using the database clock; created is excluded. `ReconcileDeadlineSweepTask` queues up to three deadline attempts per run, inside that run's tenant scope. `RunReconciler` compares and `ReconcileFinisher` records complete/incomplete; incomplete is the AC3 target when evidence is missing at the deadline.

The operator verifies the chain, not the verdict:

1. **Is the sweep firing?** Use §4.3. Missing rows require platform investigation, not an assumed component diagnosis or run mutation.
2. **Is a durable deadline key visible?** Use §4.2. Absence after a fire is not proof the scan never selected this run: candidates are read before the per-tenant enqueue commits (`ReconcileDeadlineSweepTask`). Recheck state/deadline on the database clock, page-budget warnings and enqueue errors/rollback. ⚠️ Clock skew is one possible explanation, not an established usual cause.
3. **Key present, dispatch null.** Compare its age with the deployed `TasksOptions.DispatchPollMilliseconds`, backlog and dispatcher health. ⚠️ Lag beyond one nominal interval alone does not establish a failure. Use the [dead-letter runbook](dead-letter.md) for the queue path.
4. **Task active with expired lease.** Reclaim is expected below the cap (§4.1). If dead-lettered, inspect bounded deadline re-arm: the sweep can queue the next attempt while fewer than three are spent. Default operator HTTP replay is unavailable (§7); see [dead-letter runbook](dead-letter.md) §7.1, then escalate with IDs.
5. **Task done, run open.** Identify the trigger and timing. An ordinary producer attempt before the deadline can return Pending without a revision (`RunReconciler.ReconcileAsync`). A done deadline attempt counts as spent; the sweep can re-arm on its next fire (`ReconcileDeadlineSweepTask.PlanAttempt`). After all three are spent, warning 2427 means escalate to the Orchestrator owner with run/task IDs and timestamps. A done attempt that settled the run is normal.

```sql
-- Role: evidgate_app; evidence_revision SELECT and tenant policy:
-- 0049_orchestration_evidence_revision.
start transaction read only;
select set_config('evidgate.org_id', :'org_id', true);
select revision_no, completeness, missing, outcome, created_at
  from public.evidence_revision
 where org_id = :'org_id'
   and run_id = :'run_id'
 order by revision_no desc;
commit;
```

Revision rows are append-only for the app (`0049_orchestration_evidence_revision`). `missing` has eight closed reasons, including `missing_result` (`0056_reconcile_missing_result`). Zero rows alone is not proof of no verdict: confirm the tenant/session and run identity with §3.1. An incident can change between the two read transactions; record query times. Never create a revision to fill a gap (§6).

### 5.2 `queued` or `running` past the deadline: the sweep closes them `incomplete`

`0058_reconcile_overdue_running` includes both states. `RunReconciler.ReconcileAsync` and `ReconcileFinisher` close an overdue pre-collection run through the allowed incomplete edges. Forced closure has at least one closed-list reason, such as missing_shard or missing_result, never a fabricated PASS.

Verify the same chain with these distinctions:

- **Deadline-shaped keys are not required by the reconciler.** Any ordinary non-late, non-reprocess trigger can decide an overdue queued/running run. Deadline keys provide the guaranteed scheduling path when finalize never arrived (`RunReconciler.ReconcileAsync`).
- Late/reprocess markers select different branches. Admitted late evidence can append on incomplete or complete without changing terminal state; complete-run late admission uses the window anchored at `run.completed_at` (`0114_run_completed_at`). Reprocess has its own disposition; do not confuse it with deadline recovery.
- Done, dead-lettered or purged-after-dispatch attempts count as spent (`ReconcileDeadlineSweepTask.IsSpent`). Three spent attempts exhaust automatic re-arm; warning 2427 records that bound. Follow [dead-letter runbook](dead-letter.md) §7.1 and escalate rather than fabricating another key.

Record run/org IDs, state, deadline, creation/update times, revision IDs, deadline keys and task IDs. Do not hand-write a transition or outbox row.

`created` remains excluded: no committed shard plan is assumed and created → incomplete is not an allowed edge (`0058_reconcile_overdue_running`, `RunStateMachine`). Escalate abnormal created rows.

### 5.3 `canceled`: recorded now, executor termination not proven

`POST /v1/runs/{r}/cancel` is served by `OrchestratorEndpoints`/`CancelRunCommandHandler`; OpenAPI operation `cancelRun` returns 202 “recorded, not stopped”, requires `run:cancel`, and is generated as SDK `cancelRun` (`0010_identity_permission_catalogue`, `contracts/permissions.json`).

For a resolved authorized member, a nonterminal run transitions immediately to canceled and receives one immutable-window `run_cancel_request` in the same tenant transaction (`RunCancelSql.InsertRequest`, `0069_orchestration_run_cancel`). Unique `(org_id, run_id)` prevents another request; terminal runs return a 202 no-op. Unknown/cross-tenant IDs are not found. Sibling-project runs are also hidden when outside the caller's token/request project scope (`CancelRunCommandHandler.IsRunInScopeAsync` via `OrchestratorScope.Project`, returning `OrchestratorErrors.ProjectScopeMismatch`); an authorized org-wide member is not universally forbidden from sibling projects.

Cancel already computes the windows and queues `orchestrator.run.cancel.kill` at `kill_at` (`CancelKillContract`, `CancelRunCommandHandler`). `CancelKillTaskHandler` consumes the request via `RunCancelSql.MoveToKilled` and records killed when due. **This is bookkeeping, not a physical kill:** the handler explicitly has no job-killing channel. `soft_stop_deadline_at`/`kill_at` are not proof that a producer or agent stopped. Remaining execution integration is listed in §7.

⚠️ **Drill pending:** cancellation is a proposed way to observe AC3's canceled settlement: confirm canceled state, one request row and caller-credited `run.state_changed` audit. Actual drill results remain unverified. Never infer executor termination from those rows.

`OperatorEndpoints` also registers dead-letter list/replay, the healing switch and usage-operator reads (`MapUsageOperatorEndpoints`), not just the first two. `DenyAllOperatorGate` still blocks the composed operator HTTP surface; it is not a usable emergency route (§7).

## 6. Never produce evidence or fabricate a result (AC3)

`RunReconciler` compares planned shards, acknowledged events and attempts; `ReconcileFinisher` records revision/pointer/event and any state/audit change in the same tenant transaction. In this procedure:

- **No hand INSERT into evidence_revision.** `0049_orchestration_evidence_revision` enforces gap-free numbering, not provenance. A fabricated row using the expected next number could pass that sequence guard; it remains fabricated evidence.
- **No hand UPDATE public.run.** The database state guard still applies, but manual writes miss the application's expected-version check and canonical audit/outbox work (`TransitionRunStateCommandHandler`).
- **No settling by editing task rows.** A permitted replay would rerun the handler, not assign a verdict; default HTTP replay is currently blocked.
- **No duplicate finalize to force a verdict.** `FinalizeShardCommandHandler` returns a no-op for an already closed slot, with no second enqueue.
- **A missing verdict stays missing.** Incomplete with a reason is a platform-produced result; no revision is not permission to manufacture one.

Escalate any outcome the platform cannot honestly produce.

## 7. Known gaps

**Fairness remains bounded.** `ReconcileDeadlineSweepTask` scans up to ten pages of 100 and resets its `(org_id, id)` cursor each fire. Persistent overdue/spent rows can starve later orgs. Warning 2426 marks the page bound; 2427 marks exhausted per-run attempts. `docs/status-carried.md` records this under the deadline-sweep fairness row, separately from the closed S152 deadline re-arm row.

**Not possible yet:**

- **Audited on-call run reader / operator HTTP replay.** `DenyAllOperatorGate` blocks operator HTTP; run/evidence still have no operator read policy. `plans/features/02-identity-tenancy/02.8.1-time-boxed-operator-access.md`, **02.8.1-T1** handover requires the real operator gate; **02.8.1-T2.1** supplies the grant-scoped connection/context/audit path. Do not substitute a raw service credential. Until the intended reader is implemented, §3.2 is explicitly an owner-role emergency action, not ordinary operator access.
- **Runner job lease/epoch inspection.** No job/job_lease migration exists. `plans/features/05-runs-orchestration/05.7.2-leasing-with-epoch-fencing.md`, **05.7.2-T1.1–T1.3**, supplies the tables, claims and heartbeat endpoints. Inspect only task leases today (§4).
- **Physical executor stop from cancel.** The request and killed-stage task exist, but no job-killing channel connects them to execution. The remaining execution-side portion of **05.4.1-T2.1** in `plans/features/05-runs-orchestration/05.4.1-durable-cancel.md` and process-group kill/abort integration in **05.7.4-T2.3** of `plans/features/05-runs-orchestration/05.7.4-evidgate-agent-executor.md` are required; bookkeeping is not termination proof.
- **Follow a deployed stuck-run/incomplete-rate alert link.** `infra/alerts/` is absent. `plans/features/10-operations/10.3.1-telemetry-and-alerting.md`, **10.3.1-T5.1/T5.2**, creates rules and runbook links; **10.7.1-T2.1** in `plans/features/10-operations/10.7.1-operable-by-a-second-person.md` remains partial until linked. The existing `evidgate_run_incomplete_total` instrument (`EvidgateTelemetry.RunIncompleteMetricName`) is not an alert rule.
- **Manually force reconcile/state/verdict through a dedicated recovery route, or create a fourth automatic deadline attempt.** No such route exists and the automatic bound is three. The relevant **10.7.1-T2.1** procedure requires escalation, not a new command. No enabling implementation task for a force-reconcile route or larger bound was identified in the checked plans; an owner-defined task is needed before promising either. Do not invent a route, key or task ID.

Other gaps:

- Created is deliberately excluded; escalate instead of pretending it has a shard plan (§5.2).
- Deadline re-arm is bounded, not unlimited. The closed S152 carried row describes the implemented re-arm, not permission to reset or replay spent rows by hand. Recovery steps remain [dead-letter runbook](dead-letter.md) §7.1.
- ⚠️ **Drill pending, not an execution record.** **10.7.1-T2.2** requires a non-author staging drill: kill a worker mid-run, detect overdue collection, inspect the available task lease and observe incomplete/canceled settlement. `[INFERENCE]` proposed fixture recipe: create two producer slots, finalize one into collecting, let the deadline pass without the other finalize, then observe automatic incomplete or an authorized cancel. Record real outputs/audit evidence; use synthetic fixture tenants only. This recipe's outcome, clocks, cadence, dispatcher health and cost are unverified.

## 8. Evidence table

Migration names below are stable names under `migrations/`. Column lists are grouped; the current grants, not superseded initial grants, determine the SQL execution roles.

| Identifier | Kind | Source |
|---|---|---|
| public.run | table | `0031_orchestration_run` |
| run.state, run_state_check; seven states | column + CHECK | `0031_orchestration_run`; `RunStates`, `RunStateMachine` |
| run.collection_deadline_at | NOT NULL column | `0031_orchestration_run` |
| run.version, run.updated_at | columns + shared update trigger | `0031_orchestration_run` |
| run.current_evidence_revision_id | column + app column grant | `0036_orchestration_run_state_grant`; `0049_orchestration_evidence_revision` |
| run_project_created_idx | index | `0031_orchestration_run` |
| run_tenant, run_owner_backfill; FORCE RLS | policies | `0031_orchestration_run` |
| run app SELECT/INSERT, operator SELECT without matching run policy, scheduler no privilege | grants | `0031_orchestration_run` |
| app UPDATE columns: updated_at, state, current_evidence_revision_id; no table UPDATE | column grants | `0034_ingestion_event_and_batch`; `0036_orchestration_run_state_grant`; `0049_orchestration_evidence_revision`; `0031_orchestration_run` |
| run.completed_at; state CHECK; write-once/insert guards | column + guards | `0114_run_completed_at`: no app column UPDATE; unprivileged UPDATE is 42501; an authorized changed-value UPDATE reaches 23514 run_completed_at_immutable; authorized non-null INSERT reaches 23514 run_completed_at_insert_refused |
| guard_run_evidence_pointer, run_evidence_pointer_guard | pointer guard | `0049_orchestration_evidence_revision`; not the only run guard |
| run_guard_transition_trg | state adjacency guard | `0099_run_slot_transition_guards`; function replacement `0114_run_completed_at` |
| run ref_kind/ref_name/pr_number/head_sha/base_sha/trigger_event/is_fork/unverified_ref | added identity fields | `0037_run_identity` |
| evidgate.current_org_id() | tenant context function | `0002_foundation_evidgate_schema` |
| evidgate.reconcile_overdue_runs(uuid, uuid, integer) | stable SECURITY DEFINER, empty search_path; three-state scan | `0058_reconcile_overdue_running` |
| reconcile_overdue_runs: PUBLIC revoked, worker_sched EXECUTE | function grants | `0058_reconcile_overdue_running` |
| run_overdue_idx; replaces run_collecting_idx | partial index | `0058_reconcile_overdue_running` |
| public.evidence_revision | table | `0049_orchestration_evidence_revision` |
| evidence_revision.completeness: complete/incomplete | column + CHECK | `0049_orchestration_evidence_revision` |
| evidence_revision.missing: eight reasons including stale_epoch/missing_result | text[] + final CHECK | `0049_orchestration_evidence_revision`; `0056_reconcile_missing_result` |
| evidence_revision.revision_no | gap-free insert guard, not provenance proof | `0049_orchestration_evidence_revision` |
| evidence_revision_tenant, evidence_revision_owner_backfill; FORCE RLS | policies | `0049_orchestration_evidence_revision` |
| app evidence SELECT/INSERT, no UPDATE/DELETE; operator SELECT | grants | `0049_orchestration_evidence_revision` |
| public.task, task.state: queued/active/retry/dead_letter/done | table + CHECK | `0008_platform_task_queue` |
| task attempts/max_attempts (default 8), available_at/locked_until/last_error/version | columns + constraints | `0008_platform_task_queue` |
| task.org_id nullable | column | `0008_platform_task_queue` |
| task_active_idx/task_dead_idx/task_terminal_idx | indexes | `0008_platform_task_queue` |
| task unique (schedule_name, fire_at) | same-occurrence uniqueness, not non-overlap | `0008_platform_task_queue` |
| task: worker full DML, app INSERT only; public/app/operator raw reads revoked | final grants | `0104_platform_worker_role` supersedes `0008_platform_task_queue` grants |
| public.outbox | table | `0006_platform_outbox` |
| outbox.dispatched_at, outbox_undispatched_idx | column + index | `0006_platform_outbox` |
| outbox_org_idem_uq (org_id, idempotency_key) | unique index | `0006_platform_outbox` |
| outbox: no tenant RLS, worker SELECT/update-dispatched, app INSERT only | policy/grants | `0006_platform_outbox`; `0104_platform_worker_role`; `ReconcileDeadlineSql` |
| task_in_scope/outbox_in_scope/outbox_task_in_scope | restricted tenant projections, not full raw rows | `0104_platform_worker_role` |
| public.event.epoch | column + CHECK | `0034_ingestion_event_and_batch` |
| run:cancel | permission | `0010_identity_permission_catalogue`; `contracts/permissions.json` |
| POST /v1/runs/{r}/cancel, cancelRun, 202 | served route/SDK operation | `OrchestratorEndpoints`, `CancelRunCommandHandler`; `contracts/openapi.yaml`; SDK `cancelRun` |
| public.run_cancel_request; unique (org_id, run_id), no DELETE | table + grants + guards | `0069_orchestration_run_cancel` |
| orchestrator.run.cancel.kill | task type + request-stage consumer | `CancelKillContract`, `CancelKillTaskHandler`, `RunCancelSql.MoveToKilled` |
| run.org_id/project_id/created_at | columns | `0031_orchestration_run` |
| task.task_type/payload/created_at | columns | `0008_platform_task_queue` |
| outbox.org_id/task_type/payload/idempotency_key/created_at | columns | `0006_platform_outbox` |
| evidence_revision.org_id/run_id/outcome/created_at | columns | `0049_orchestration_evidence_revision` |
| set_config(text, text, boolean), evidgate.org_id | built-in + tenant GUC | `plans/00-overview.md`, §5.4.2; `evidgate.current_org_id`, `0002_foundation_evidgate_schema` |
| orchestrator.run.reconcile; orchestrator.run.reconcile.deadline; deadline keys; DeadlineMaxAttempts = 3 | task constants, not schema | `RunReconcileContract` |

Aliases `overdue_by`, `fires` and `last_fire` are output labels, not schema identifiers.

Desk check: 2026-11-21, wave fd69e03b; not checkable without a live environment: deployed release/migrations and credentials, incident query results, query cost, schedule cadence, clock skew, dispatcher lag, non-author staging drill outcome.
