# 05 - Runs and Orchestration (M1-M4)

> Contracts: `00-overview.md` §5.3 (outbox + pg-boss vs lease tables), §7 (hashes, idempotency), §8.1-§8.2 (run and job states), §8.5 (evidence completeness), §9.3 (endpoints), §10 (adapter protocol), §11 (NFR).
> Facts: `../DECISIONS.md` §3 (A5, A8, A9, A10), §4 (stack: Kysely, pg-boss 12.34, engines), §6 F6 (`FOR UPDATE SKIP LOCKED`), §9 (capacity arithmetic).
> Source: design §4 (job/adapter contract, JobSpec, isolation, agent disconnect), §5 (run lifecycle, cancel/rerun, release components), §6 (run creation, data model), §7 (consistency and recovery: outbox, scheduler, deadlines, consistency jobs).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.

## 1. Purpose and scope

**Goal:** turn a locked manifest into a run whose completeness is provable — every planned execution accounted for, evidence bound to an exact build, and every lifecycle transition durable.

**In scope:** manifest schema and verification, run creation (one transaction + outbox), the run state machine and its side exits, finalize/reconciliation with collection deadline and late window, cancel, rerun, `release`/`release_component`, the V2 agent mode (job tables, JobSpec, claiming and leasing with epoch fencing, capability matching, pools, fair scheduling, timeouts, `unschedulable`, cleanup confirmation, disposable workspace), the V2-conditional Android device lease, the V3 placeholder for iOS/hosted runners, and the periodic consistency jobs.

**Out of scope:** the event wire format and adapters (04), the artifact object lifecycle (06) and the evidence freeze result (06 supplies it; 05 persists the revisions), policy evaluation and decisions (07), healing (08), agent UI (09), deployment/observability (10).

**Version split.** V1 = manifest, run creation, lifecycle, finalize/reconcile, cancel, rerun, release, consistency jobs (CI mode only, DECISIONS A5). V2 = agent mode + Android device lease. V3 = iOS/hosted runners placeholder only (sizes rough).

## 2. Requirements

- **FR-05-1 (manifest)** A manifest is uploaded and verified before run creation: it lists planned executions (case × variant × target env), the matrix, the shard plan and the environment needs, and gets a `manifest_hash` (`sha256:` over RFC 8785 canonical JSON, 00 §7). A verified manifest is immutable.
- **FR-05-2 (one transaction)** `POST /v1/projects/{p}/runs` locks the manifest, suite revision and environment revision (and the policy revision when one is active; the run-pinning handshake is 07.1.1-T4), then writes `run` + `planned_execution` rows + the outbox row in a **single** transaction before returning 201; same `Idempotency-Key` + same canonical payload returns the original run, a different payload returns 409 `idempotency_conflict` (00 §9.1/§9.3).
- **FR-05-3 (state machine)** Run transitions follow 00 §8.1 exactly: `created → queued → running → collecting → complete`, with `incomplete` and `canceled` side exits; `complete`, `incomplete` and `canceled` are terminal; a canceled run never becomes complete, even if late evidence arrives.
- **FR-05-4 (reconcile)** `POST /v1/runs/{r}/finalize` (producer/shard, `last_sequence`, `count`, `manifest_hash`) is accepted with 202; the reconciler waits until the expected events are acknowledged, compares declared vs observed counts and ids and the required artifacts, then creates an evidence revision with `completeness ∈ {complete, incomplete}` and `missing[]` reasons (00 §8.5). A `manifest_hash` mismatch is a hard failure.
- **FR-05-5 (deadline and late window)** Each run has a collection deadline and a policy late window. Events after the deadline keep their receive timestamp and never mutate an old snapshot; late evidence may create a new evaluation revision under policy, never a rewritten one (source §7).
- **FR-05-6 (cancel)** Cancel records a durable request and returns 202 — never "stopped". Execution stops softly first, then is killed at a configured timeout; evidence already produced is still accepted.
- **FR-05-7 (rerun)** Rerun creates a **new** run with `parent_run_id` and `rerun_scope ∈ {full, failed_subset}`; a subset rerun is labelled everywhere and never counts as a full regression.
- **FR-05-8 (releases)** A release is a set of `release_component` rows (component → digest) plus the runs bound to it. Runs are bound by matching component digests; joins by "most recent run" are forbidden.
- **FR-05-9 (agent mode)** An agent claims only jobs whose capability matches its pool; the JobSpec it receives is immutable and contains repo/commit, suite revision, app digest, environment revision, engine image digest, timeouts, resource limits, secret references and the artifact policy. Arbitrary shell from a low-privilege user is never part of a JobSpec.
- **FR-05-10 (leasing)** Claims use a short transaction with `FOR UPDATE SKIP LOCKED` (F6 ✅); lease 90 s, heartbeat 15 s with jitter, `epoch` incremented on every new lease; events carrying an old epoch are stored as stale evidence and never complete the job. No database lock is held while a test runs (00 §5.3).
- **FR-05-11 (scheduling)** Queue time and execution time have separate timeouts. Fair scheduling per tenant/pool with concurrency caps; a job waiting for a device never holds a web slot; when no pool has the capability the job is reported `unschedulable` instead of waiting forever.
- **FR-05-12 (cleanup before re-run)** Before a job with side effects is re-run, the platform requires a confirmed cleanup/isolation of the previous attempt; the confirmation is recorded.
- **FR-05-13 (self-checking)** Periodic jobs detect stuck runs and expired leases; every automatic repair is idempotent, audited and never re-runs business logic.

- **FR-05-14 (environment revision)** A project environment is versioned: `environment_revision` holds only secret-free configuration — base URLs and the dependency versions that affect results — plus secret *references* (never values); it is immutable after creation, carries `revision_hash` (`sha256:` over RFC 8785 canonical JSON, 00 §7), and a run locks exactly one revision (00 §9.3 "lock … environment"; source §5 line 169 "Environment revision và dependency version ảnh hưởng kết quả cũng được lưu").
- **FR-05-15 (case attempts)** Every executed attempt is persisted once per `(org_id, run_id, execution_key, attempt_no)` with the raw status **as sent** plus the normalised status (00 §8.3); 04's event commit writes the attempt row through 05's write interface inside the **same** transaction (05.3.3-T2), and a failed setup marks dependant attempts `blocked`, never `passed` (source §6 line 183 "job có nhiều case_attempt"; source §6 line 204).
- **FR-05-16 (environment lock, V2)** When a suite's environment requirement declares `data_isolation = shared` (03.2.1-T4; the suite revision's declaration is the only source of truth, 03 §4), the platform takes an exclusive lock on that environment for the whole job lease: one holder at a time, fenced by `epoch`, released on terminal state or lease expiry; a waiting job holds no pool slot and the queue timeout applies. CI mode cannot enforce the lock, so the run records "lock not enforced" instead of claiming isolation (source §7 line 274 "Lock môi trường chỉ dùng khi suite không thể cô lập dữ liệu").

**Acceptance criteria (testable):** a run with a missing shard ends `incomplete` with `missing[] = [missing_shard]` and can never be `PASS` (00 §8.6); finalize arriving before the events leaves the run in `collecting` until the deadline; a canceled run with an in-flight shard stores the late events and stays `canceled`; a second run create with the same key returns the same `run_id` and creates no duplicate planned executions; an agent whose heartbeat lapses past the lease loses the job (new epoch) and its late events are marked stale; a subset rerun is visible as `rerun_scope = failed_subset` and is rejected as release evidence for a "full regression" policy.

## 3. Key decisions

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | Manifest is verified and `manifest_hash`-pinned before any run exists | Completeness is only provable against a locked plan (A8); it also makes replay/reprocess meaningful | Compute the plan at run creation (nothing to reconcile against) |
| D2 | Run + planned executions + outbox in one transaction, then async work | No lost dispatch and no orphan run (00 §5.3, source §6) | Two writes with a retry |
| D3 | Reconciler is a separate, idempotent component keyed by `(run_id, producer_id)` | Finalize order is not guaranteed; at-least-once delivery (A10) | Reconcile inside the finalize request |
| D4 | Completeness lives in the evidence revision (persisted by 05 from 06's freeze result), not on the run | Run state and evidence completeness must not be collapsed into one field (A8) | A `complete` boolean on `run` |
| D5 | Runner job leasing is our own table logic, not a pg-boss queue | Epoch fencing and capability matching are product rules (00 §5.3, R9) | pg-boss as the job queue |
| D6 | Engine (Playwright/Appium) version is pinned in the JobSpec, whose engine image carries the exact version; the capability document declares it and a mismatch is not schedulable | Reproducibility: the same JobSpec must mean the same engine. Consistent with DECISIONS §4 ("Evidgate never pins *latest* inside runners"): we pin an exact version + digest, never a floating tag | Resolve the engine at run time (`latest`/semver range) — an unchanged manifest would produce different evidence |
| D7 | Soft stop then kill, with a durable cancel request row | Agents and CI shards cannot be trusted to stop instantly (source §5) | Mark the run canceled and drop the workers |
| D8 | Releases are bound by digest match, never by recency | The source explicitly forbids joining the "latest" run (source §5) | Pick the newest run per component |
| D9 | Android device lease is exclusive, V2 and **conditional** on a paying need | Device reset/port limits are not isolatable otherwise (source §4); 00 §1.3 keeps it out of V1 | Shared devices with app reset only |
| D10 | iOS and hosted runners stay a placeholder with rough sizes | No macOS pool or hosted infrastructure decision yet (A5) | Design them now |

## 4. Design

**Module.** `src/modules/orchestrator` owns manifests, runs, planned executions, project environments and their immutable revisions, case attempts, evidence revisions (persisted from 06's `freeze(run_id)` result), environment locks (V2), jobs/leases, cancel requests, releases and device leases. Artifact rows stay behind the `evidence` module interface (06); policy reads come from 07.

**Tables owned (high level).**

| Table | Key columns | Constraints |
|---|---|---|
| `manifest` | `id`, `org_id`, `project_id`, `manifest_hash`, `suite_revision_id`, `environment_revision_id`, `shard_plan` (jsonb), `matrix` (jsonb), `planned_count`, `verified_at`, `verified_by` | unique `(org_id, project_id, manifest_hash)`; immutable after `verified_at` |
| `planned_execution` | `id`, `org_id`, `run_id`, `execution_key`, `case_revision_id`, `variant_hash`, `target_env`, `shard_id`, `required` | unique `(org_id, run_id, execution_key)`; composite FK `(org_id, run_id)` |
| `run` | `id`, `org_id`, `project_id`, `release_id`, `manifest_id`, `policy_revision_id`, `suite_revision_id`, `environment_revision_id`, `test_commit`, `components` (jsonb), `parent_run_id`, `rerun_scope`, `state`, `collection_deadline_at`, `late_window_minutes`, `idempotency_key` | unique `(org_id, project_id, idempotency_key)`; `state` checked against 00 §8.1 |
| `job` | `id`, `org_id`, `run_id`, `shard_id`, `pool`, `capability_requirement` (jsonb), `job_spec_hash`, `state`, `queue_timeout_at`, `execution_timeout_at`, `attempt_count` | unique `(org_id, run_id, shard_id, attempt_count)` |
| `job_lease` | `id`, `org_id`, `job_id`, `agent_id`, `epoch`, `leased_at`, `heartbeat_at`, `expires_at`, `outcome` | unique `(org_id, job_id, epoch)`; history kept per epoch |
| `run_cancel_request` | `id`, `org_id`, `run_id`, `requested_by`, `reason`, `requested_at`, `soft_stop_deadline_at`, `kill_at`, `state` | one active row per run |
| `release`, `release_component` | `release.id/org/project/name/state`; component: `release_id`, `component_key`, `digest`, `environment_revision_id` | unique `(org_id, release_id, component_key)`; digest is `sha256:` |
| `release_run` | `id`, `org_id`, `release_id`, `run_id`, `component_key` | unique `(org_id, release_id, run_id, component_key)`; composite FKs `(org_id, release_id)`, `(org_id, run_id)`; binding recorded explicitly; no implicit "latest" |
| `device_lease` | `id`, `org_id`, `pool`, `device_udid_hash`, `job_id`, `epoch`, `app_checksum`, `leased_at`, `expires_at`, `reset_policy` | unique active lease per device |
| `environment` | `id`, `org_id`, `project_id`, `environment_key`, `name`, `current_revision_id` | unique `(org_id, project_id, environment_key)`, unique `(org_id, id)`; composite FK `(org_id, current_revision_id)` |
| `environment_revision` | `id`, `org_id`, `project_id`, `environment_id`, `revision_no`, `base_urls` (jsonb), `dependency_versions` (jsonb), `secret_refs[]`, `revision_hash`, `created_by` | unique `(org_id, environment_id, revision_no)`; composite FK `(org_id, environment_id)`; immutable after insert (no update path); `revision_hash` is `sha256:`; schema rejects secret **values** (references only) |
| `evidence_revision` | `id`, `org_id`, `run_id`, `revision_no`, `completeness` (`complete`/`incomplete`), `missing[]`, `artifact_set_hash`, `event_watermark`, `manifest_hash`, `created_at` | unique `(org_id, run_id, revision_no)` (00 §8.5); composite FK `(org_id, run_id)`; append-only, never updated; `artifact_set_hash` comes from 06's `freeze(run_id)` result |
| `case_attempt` | `id`, `org_id`, `run_id`, `job_id`, `execution_key`, `case_id`, `attempt_no`, `raw_status`, `normalized_status`, `started_at`, `finished_at`, `duration_ms` | unique `(org_id, run_id, execution_key, attempt_no)`; indexes `(run_id, normalized_status)` and `(case_id, started_at)`; composite FKs `(org_id, run_id)`, `(org_id, job_id)`; `normalized_status` checked against 00 §8.3 (source §6 line 204) |
| `environment_lock` (V2) | `id`, `org_id`, `environment_id`, `job_id`, `epoch`, `acquired_at`, `expires_at`, `state` (`held`/`released`/`expired`) | unique active lock per `(org_id, environment_id)`; composite FKs `(org_id, environment_id)`, `(org_id, job_id)`; released on terminal state or lease expiry; fenced by `epoch` |

**Endpoints.**

| Endpoint | Owner | Notes |
|---|---|---|
| `POST /v1/projects/{p}/manifests` | **05** | upload + verify → `manifest_id`, `manifest_hash` |
| `POST /v1/projects/{p}/runs` | **05** | Idempotency-Key required; one transaction |
| `GET /v1/runs/{r}` | **05** | state + current evidence revision id (revision persisted by 05, 05.3.2-T5) |
| `POST /v1/runs/{r}/cancel` | **05** | 202, durable request |
| `POST /v1/runs/{r}/rerun` | **05** (new; §13) | new run with `parent_run_id`, scope |
| `POST /v1/projects/{p}/releases` | **05** (new; §13) | components → digests, bind runs |
| `POST /v1/agents/jobs/claim` (V2) | **05** | pool + capability match → lease, epoch, deadline |
| `POST /v1/jobs/{j}/heartbeat` (V2) | **05** | extends lease only for the current epoch |
| `POST /v1/runs/{r}/finalize` | 04 (semantics **05**) | reconcile trigger |
| `POST /v1/projects/{p}/environments/{e}/revisions` | **05** | permission `environment:write`; append an immutable, secret-free revision → `revision_hash` |
| `GET /v1/environment-revisions/{id}` | **05** | permission `run:read`; `revision_hash` is the `ETag` |

**Run lifecycle (00 §8.1).** `created` (row committed) → `queued` (outbox dispatcher placed shard jobs) → `running` (first event or first claim) → `collecting` (first finalize received) → `complete` (reconciler verdict `complete` **and** no block) | `incomplete` (deadline passed without complete evidence) | `canceled` (cancel request accepted). Every transition is written with the state change, an outbox row and an audit entry; transitions are guarded by optimistic `version` (00 §7).

**Finalize → reconcile flow.** finalize → validate `manifest_hash` and producer/shard → mark the producer closed → wake the reconciler. Reconciler: lock the run row, list closed producers vs the shard plan, read acknowledged events for each `(producer, sequence)` range, compare counts and ids with `count`/`last_sequence`, check required artifacts are `ready` (00 §8.4), then take 06's `freeze(run_id)` result and persist the revision (05.3.2-T5) with `completeness`, `missing[]` and the manifest hash. Missing shard, mismatch, unknown status or setup error keeps `incomplete` (00 §8.6). If the run is already `canceled`, the revision is still created but the run stays `canceled`.

**Cancel.** Durable request → `soft stop` (cancel token visible to agents; CI shards see `cancel_requested` in their next heartbeat/poll) until `soft_stop_deadline_at` → `kill` (agent kills the job process group; CI shards are simply refused further events for new attempts). Race rules: already-produced evidence is accepted; a run in `created`/`queued` can go straight to `canceled`; a run that would have completed during the race stays `canceled`.

**Agent mode (V2).** JobSpec is written once and referenced by `job_spec_hash`: repo + commit, suite revision, app digest, environment revision, **engine family + exact version + engine image digest** (D6), execution and queue timeouts, CPU/memory/time limits, secret *references* (never values), artifact policy (what to collect, size caps, sensitivity), and the run namespace for cleanup. Claim order: tenant/pool fairness (weighted round-robin over eligible pools, oldest-queued first inside a tenant), capability match (protocol major, platform, engine family/version in the image, `supports.cancel`), then concurrency caps per pool and per tenant. A job that no pool can satisfy is set `unschedulable` with the unmet requirement. Leasing: 90 s lease, 15 s heartbeat (jitter), epoch bump on every new lease; an expired lease re-queues as a new lease, the old epoch's events become stale evidence. Executor isolation: a disposable workspace per job under a dedicated unprivileged user, resource limits, no host network secrets, only allow-listed paths collected (`collect` may not read outside the workspace), one idempotent `cleanup` per run namespace, and a recorded confirmation that a side-effecting environment was cleaned or isolated before any re-run.

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Finalize arrives before its events | Producer marked closed; run stays `collecting`; reconciler is retried until deadline | Missing data never completes |
| Duplicate finalize / retry | Idempotent per `(run, producer)`; second call returns 202 with no state change | No double transition |
| Worker crash mid-job | Short claim transaction, lease expiry → new epoch lease, job re-queued | No DB lock held during execution (F6 ✅) |
| Old worker (old epoch) sends events | Stored as stale evidence for the old epoch only | Never completes the current job |
| Agent heartbeat lapses | Lease expires at 90 s; job re-queued with epoch + 1; agent's late `heartbeat` gets 409 | Single owner per epoch |
| Claim storm from many agents | `SKIP LOCKED` short claim + per-pool cap + jittered retry on empty | No thundering herd, no double lease |
| No pool has the capability | Job → `unschedulable` with the unmet requirement, visible to the operator/UI | Never an infinite wait |
| Device unavailable | Job waits in the device pool without consuming a web slot; queue timeout applies | Web capacity untouched |
| Cancel during `collecting` | Request accepted, run → `canceled`; revision may still be created but is labelled on a canceled run | Canceled never `complete` |
| Late event after deadline | Stored with receive timestamp; may create a new evaluation revision under policy only | Old snapshots immutable (source §7) |
| `manifest_hash` mismatch at finalize | 422, run stays `collecting`, missing reason recorded | No evidence against the wrong plan |
| Release component digest mismatch | Run is not bound; the evaluation path treats it as a build mismatch | Never join by recency |
| Side effect remains after a failed job | Re-run blocked until a cleanup/isolation confirmation is recorded | No blind retry of a side-effecting job (A9) |
| Stuck run (no transition) | Consistency job flags it, re-triggers reconcile once, then alerts | Idempotent repair, audited |
| Suite declares `environment_requirements.data_isolation = shared` (03.2.1-T4; the declaration is read from the suite revision) | V2: an exclusive `environment_lock` is taken per environment for the whole job lease (epoch-fenced); other jobs wait holding **no** slot, the queue timeout applies, release happens on terminal state or lease expiry | One job per environment at a time (source §7) |
| Environment lock holder dies while holding the lock | `epoch` fencing + lock expiry release the lock and re-queue the job; the next holder starts only after the release is committed | No double occupant, no permanent lock |
| CI mode cannot enforce the environment lock | The run records "lock not enforced"; the requirement stays visible to the operator and the run is never reported as isolated | No false isolation claim |
| A second event reports the same `(run, execution_key, attempt_no)` with a different payload | `case_attempt` unique key rejects the write → whole batch 422 with the event index (04); an attempt row is never rewritten | Attempt history is immutable and unique (source §6) |

## 6. Security and privacy

- Permissions: `run:create`, `run:read`, `run:cancel`, `job:claim`, `job:execute` (00 §2); the project in the URL must match the token scope (403 otherwise).
- **No arbitrary shell:** a JobSpec is a validated schema; commands come from the declared adapter operations (`prepare`/`execute`/`collect`/`cleanup`) with typed fields. A `qa_engineer` cannot inject a command, an image or a secret reference outside the pool's allow-list.
- Secret references resolve at execution time inside the agent; values are never returned by the API, never in the JobSpec body, and never in logs or metric labels (00 §11).
- Agents are service identities scoped to one tenant + pool: they cannot read other projects, cannot approve anything, and cannot claim jobs of another pool.
- Device leases name devices by hashed identifier; app checksums are recorded so a run's evidence identifies the exact binary.
- Agent-executed tests are untrusted code (11 threat model): workspace disposal, unprivileged user, no ambient cloud credentials, egress limited to the target environment and the Evidgate API.
- Artifacts/reports may contain personal data (DECISIONS §7): artifact policy is per JobSpec; collection outside allow-listed paths fails the job.
- Every lifecycle transition, cancel, rerun, release binding and cleanup confirmation is audited with actor, reason and hashes.

## 7. Test strategy

- **Behaviours:** manifest upload → run create → events → finalize → reconcile produces exactly one evidence revision per (run, producer) set; a rerun of the failed subset never satisfies a full-regression policy; a release binds only digest-matching runs.
- **Boundaries:** manifest with 0 planned executions (declared-empty suite vs invalid), shard plan covering exactly/one-short of the matrix, `collection_deadline_at` exactly at the boundary, late window 0 and > 0, lease at 89 s vs 91 s, `queue_timeout` vs `execution_timeout` order, concurrency cap of 1 in a pool.
- **Failure cases:** finalize→events inversion; duplicate finalize; cancel at each state including during reconcile; agent crash with a lease held; two agents claiming simultaneously (SKIP LOCKED stress, 50+ concurrent claims); old-epoch event after re-lease; side-effect job re-run without cleanup confirmation (must block); `unschedulable` when the only pool declares the wrong engine version; DB transaction rollback leaves no run and no outbox row.
- **Isolation:** integration tests on real PostgreSQL (Testcontainers 12) with RLS + `FORCE ROW LEVEL SECURITY`; explicit tests that run create/list never sees another tenant's rows; RLS bypass by table owner is covered in 11.
- **Fixtures:** the dogfooding run (04.6.1-T2) doubles as the end-to-end run/lifecycle fixture; a fake agent test double (no browser) exercises claiming, heartbeat, lease expiry and cleanup without an engine; the V3 spikes deliver written findings only (no tests).

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Reconciler becomes the correctness bottleneck | Medium | Wrong `complete`/`incomplete` verdicts | Deterministic comparison (manifest vs acked events vs artifacts) with fixture-driven tests; verdicts are revisions, so a bad verdict is correctable by a new revision, never by an update |
| Lease/heartbeat values wrong for slow CI or long suites | Medium | Duplicate job execution | Values are source defaults and explicitly "tune after measurement" (00 §5.3); execution timeout is separate from lease, and cleanup confirmation blocks re-runs of side-effecting jobs |
| Scheduler fairness starves a small tenant | Medium | Pilot dissatisfaction | Weighted round-robin per tenant/pool + visible queue metrics; caps are configuration, tested at the boundary |
| Device leasing (V2) assumed simpler than it is | Medium | V2 slip | V2-conditional (00 §1.3); the device epic is isolated and can be dropped without touching V1 |
| iOS/hosted expectations grow before V3 | Medium | Scope pressure | Placeholder with rough sizes and no commitments (D10); decision gate at M5 |
| `manifest_hash` canonicalisation mismatch between CLI and server | Low | Runs refused | Hash computed from the same generated schema (01) with a cross-language fixture test |

## 9. Epics, stories and tasks

### Epic 05.1 Manifest
#### Story 05.1.1 Verified manifest — acceptance: a manifest that does not cover the matrix, names an unknown case revision, or leaks a secret in variant params is rejected with 422 and indices; a valid one is immutable with a stable `manifest_hash`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.1.1-T1 | Manifest JSON Schema (planned executions, matrix, shard plan, env needs) in `contracts/` | L | V1 | M1 | 01.3.1 |
| 05.1.1-T2 | Verification pipeline: schema + semantic checks (case revisions exist, variants normalised secret-free, shard plan covers matrix) → 422 with indices | L | V1 | M1 | 05.1.1-T1, 03.1.3, 03.2.1-T1, 03.2.1-T2 |
| 05.1.1-T3 | `manifest_hash` (RFC 8785) + immutability + `ETag`/`If-Match` | M | V1 | M1 | 05.1.1-T1 |
| 05.1.1-T4 | `POST /v1/projects/{p}/manifests` + OpenAPI + integration test | M | V1 | M1 | 05.1.1-T2 |

#### Story 05.1.2 Environment revisions — acceptance: a revision is created with a stable `revision_hash` and no secret value (references only); a revision that a run/manifest has locked can never be edited (409) and a new revision is appended instead; a run naming an unknown revision is rejected 422.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.1.2-T1 | `environment` + `environment_revision` tables (base URLs, dependency versions, `secret_refs`, `revision_hash` — no local isolation flag; the lock reads the suite's declaration, 03 §4) + `POST /v1/projects/{p}/environments/{e}/revisions` and `GET /v1/environment-revisions/{id}` (permissions `environment:write` / `run:read`, 00 §9.3) + the migration that adds 02's `project.default_environment_id` composite FK `(org_id, default_environment_id)` → `environment` + OpenAPI | M | V1 | M1 | 05.1.1-T4, 01.4.2-T2 |
| 05.1.2-T2 | Secret-free validation + referenced-revision immutability (409) + `revision_hash` (RFC 8785) tests | S | V1 | M1 | 05.1.2-T1 |

### Epic 05.2 Run creation
#### Story 05.2.1 One-transaction run creation — acceptance: run, planned executions and the outbox row are all present or all absent; the same idempotency key returns the original run; a different payload returns 409.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.2.1-T1 | `POST /v1/projects/{p}/runs`: lock manifest/suite/env revisions and the policy revision when one is active (the run-pinning handshake is 07.1.1-T4, M2), validate components, single transaction writing run + planned executions + outbox | L | V1 | M1 | 05.1.1-T4, 05.1.2-T1, 01.5.1 |
| 05.2.1-T2 | Idempotency-Key store (canonical request hash, 24 h+) with 409 `idempotency_conflict` | M | V1 | M1 | 05.2.1-T1 |
| 05.2.1-T3 | Server-side `execution_key = sha256(case_id ‖ variant_hash ‖ target_env)` + uniqueness | M | V1 | M1 | 05.2.1-T1 |
| 05.2.1-T4 | CI-mode shard jobs derived from the shard plan (producer per shard, expectations recorded) | M | V1 | M2 | 05.2.1-T1 |

### Epic 05.3 Lifecycle, finalize and reconcile
#### Story 05.3.1 Run state machine — acceptance: only the transitions of 00 §8.1 are accepted, each writes audit + outbox, and a stale `version` write fails 412.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.3.1-T1 | State machine with guarded transitions, audit and outbox emission; `GET /v1/runs/{r}` | L | V1 | M1 | 05.2.1-T1 |
#### Story 05.3.2 Finalize and reconciliation — acceptance: a fully-reported run becomes `complete` with `missing[] = []`; any missing shard, count mismatch, unknown status or missing required artifact yields `incomplete` with reasons.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.3.2-T1 | `finalize` handling: producer/shard close, `manifest_hash` check, 202 | M | V1 | M1 | 05.2.1-T1 |
| 05.3.2-T2 | Reconciler: wait for acked events, compare declared vs observed count/ids, check required artifacts, persist the revision through 05's `evidence_revision` (05.3.2-T5) from 06's freeze result with `completeness` + `missing[]` | L | V1 | M2 | 05.3.2-T1, 05.3.2-T5, 06.5.1-T1, 06.5.1-T2 |
| 05.3.2-T3 | Collection deadline + late window (receive timestamp kept; late evidence → new revision under policy) | M | V1 | M2 | 05.3.2-T2, 07.1.1-T1 |
| 05.3.2-T4 | Reconciliation idempotency: duplicate finalize, out-of-order finalize, late producer, retry-safe runs | M | V1 | M2 | 05.3.2-T2 |
| 05.3.2-T5 | `evidence_revision` table owned by 05: append-only, `revision_no`, `completeness`, `missing[]`, `artifact_set_hash` from 06's freeze result, event watermark; unique `(org_id, run_id, revision_no)` | M | V1 | M1 | 05.3.2-T1 |

#### Story 05.3.3 Case attempts — acceptance: every executed attempt exists exactly once per `(run, execution_key, attempt_no)` with raw status as sent plus normalised status; an attempt row becomes visible only when 04's event batch commits; a failed setup marks dependant attempts `blocked`, never `passed`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.3.3-T1 | `case_attempt` table (source key `UNIQUE org_id, run_id, execution_key, attempt_no`; indexes `(run_id, normalized_status)`, `(case_id, started_at)`) with raw + normalised status per 00 §8.3 | M | V1 | M1 | 05.2.1-T1 |
| 05.3.3-T2 | Attempt write interface used by 04's event commit in the **same** transaction + `blocked` propagation to dependants of a failed setup (suite DAG) | M | V1 | M1 | 05.3.3-T1 |

### Epic 05.4 Cancel
#### Story 05.4.1 Durable cancel — acceptance: cancel returns 202, the request is durable across restarts, execution stops softly then is killed, and a canceled run never becomes complete.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.4.1-T1 | Cancel request row + `POST /v1/runs/{r}/cancel` + permission `run:cancel` | S | V1 | M2 | 05.3.1-T1 |
| 05.4.1-T2 | Soft stop then kill: cancel token for agents, refusal of new attempts for CI shards, kill at `kill_at` | M | V1 | M2 | 05.4.1-T1 |
| 05.4.1-T3 | Race rules: accept already-produced evidence, block completion after cancel, mark post-cancel evidence | M | V1 | M2 | 05.4.1-T2 |

### Epic 05.5 Rerun
#### Story 05.5.1 Rerun with parent and scope — acceptance: a rerun creates a new run with `parent_run_id`; a `failed_subset` rerun is labelled and is never accepted as full-regression evidence.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.5.1-T1 | Rerun flow: selection (failed subset from the current revision) → new run + `parent_run_id` + `rerun_scope` | M | V1 | M2 | 05.3.2-T2 |
| 05.5.1-T2 | Subset labelling: exposure in API/UI/report and the policy input that blocks subset runs from satisfying a full-regression policy | M | V1 | M2 | 05.5.1-T1, 07.4.1 |

### Epic 05.6 Release and components
#### Story 05.6.1 Digest-bound release — acceptance: a release lists components with digests; only runs whose manifest components match are bound; no run is ever bound by recency.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.6.1-T1 | `release`/`release_component` tables + create API (components → digests, environment revision) | M | V1 | M2 | 05.2.1-T1 |
| 05.6.1-T2 | Binding by digest match (`release_run`), refusal of time-based joining, mismatch surfaced to 07 as a build-mismatch reason (07.2.1-T2 catalogue; aggregated by 07.5.3-T1 at M3) | M | V1 | M2 | 05.6.1-T1, 07.2.1-T2 |

### Epic 05.7 Agent mode (V2)
#### Story 05.7.1 Immutable JobSpec — acceptance: a JobSpec is written once with hash; changing repo/commit, suite revision, app digest, env revision, engine version/image, timeouts, limits, secret refs or artifact policy produces a different hash and is never edited in place; no field accepts a free-form command.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.7.1-T1 | JobSpec schema + immutability + `job_spec_hash`; exact engine version pinned (D6) alongside the image digest; schema rejects free-form commands | L | V2 | M4 | 04.7.1-T1 |
#### Story 05.7.2 Leasing with epoch fencing — acceptance: simultaneous claims never double-lease; an expired lease re-queues the job under a new epoch; old-epoch events are stale evidence.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.7.2-T1 | `job`/`job_lease` tables; short `FOR UPDATE SKIP LOCKED` claim; 90 s lease, 15 s heartbeat + jitter; epoch bump; heartbeat/claim endpoints | L | V2 | M4 | 05.7.1-T1 |
| 05.7.2-T2 | Heartbeat extension rules (current epoch only), late-heartbeat 409, lease history per epoch | M | V2 | M4 | 05.7.2-T1 |
#### Story 05.7.3 Scheduling: capability, pools, fairness, timeouts — acceptance: a job is claimed only by a capability-matching pool, fairness rotates across tenants, caps are respected, queue and execution timeouts are distinct, and an unmatchable job is reported `unschedulable`; a suite declaring `data_isolation = shared` (03.2.1-T4) is serialised against its environment by an exclusive lock held for the job lease and, in CI mode, the run records that the lock is not enforced.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.7.3-T1 | Capability matching (protocol major, platform, engine family/version, `supports.cancel`) + pool model | M | V2 | M4 | 04.7.1-T2 |
| 05.7.3-T2 | Fair scheduler per tenant/pool: weighted round-robin, concurrency caps, oldest-first within a tenant | L | V2 | M4 | 05.7.3-T1 |
| 05.7.3-T3 | Queue timeout vs execution timeout, `unschedulable` outcome with the unmet requirement | M | V2 | M4 | 05.7.3-T2 |
| 05.7.3-T4 | Environment lock for suites whose `environment_requirements.data_isolation` is `shared` (03.2.1-T4): exclusive claim per environment held for the job lease, fenced by `epoch`, released on terminal state/lease expiry, waiting jobs hold no slot, queue timeout applies; CI mode records "lock not enforced" | M | V2 | M4 | 05.7.3-T2, 05.1.2-T1 |
#### Story 05.7.4 `evidgate-agent` executor — acceptance: the agent executes exactly the JobSpec in a disposable workspace under an unprivileged user, collects only allow-listed artifacts, and reports setup failure separately.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.7.4-T1 | Agent identity per tenant + pool, secret reference resolution (values never logged/returned), pull loop with backoff | M | V2 | M4 | 05.7.2-T1, 02.5.1 |
| 05.7.4-T2 | Executor: disposable workspace, unprivileged user, resource limits, `prepare`/`execute`/`collect` only from the JobSpec, no ambient credentials | XL | V2 | M4 | 05.7.4-T1 |
| 05.7.4-T3 | Artifact policy enforcement (allow-listed paths, size caps, sensitivity) + `collect` boundary test | M | V2 | M4 | 05.7.4-T2, 06.1.1, 06.1.3 |
#### Story 05.7.5 Cleanup and re-run safety — acceptance: a job with side effects cannot be re-run until a cleanup/isolation confirmation is recorded; cleanup is idempotent and its failure is reported separately.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.7.5-T1 | `cleanup` operation per run namespace, idempotent, separate failure state | M | V2 | M4 | 05.7.4-T2 |
| 05.7.5-T2 | Re-run gate: block/require confirmation, record actor + evidence of isolation for side-effecting jobs | M | V2 | M4 | 05.7.5-T1 |
#### Story 05.7.6 Agent end-to-end — acceptance: a web/API suite claimed, executed and finalized by the agent produces the same evidence shape as CI mode.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.7.6-T1 | End-to-end agent test with the Playwright engine image, claim storm + lease-expiry scenarios | M | V2 | M4 | 05.7.4-T3, 04.3.1-T1 |

### Epic 05.8 Android device lease (V2, conditional)
#### Story 05.8.1 Exclusive device lease — acceptance: one device has at most one active lease, app and data are reset per policy before a job starts, and the app checksum is recorded with the run.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.8.1-T1 | `device_lease` table + exclusivity + reset policy + app checksum + Appium port limits | L | V2 | M4 | 05.7.3-T2 |
| 05.8.1-T2 | Device pool scheduling: device waits never consume web slots; separate deadline and `unschedulable` when all devices are leased | M | V2 | M4 | 05.8.1-T1 |

### Epic 05.9 iOS and hosted runners (V3 placeholder — sizes rough)
#### Story 05.9.1 macOS/iOS pool spike — acceptance: a written findings note covering signing, pool sizing and cost, with a go/no-go recommendation. (rough)
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.9.1-T1 | Spike: macOS runner pool, XCUITest signing, device procurement note (rough) | M | V3 | M5 | 05.8.1-T1 |
#### Story 05.9.2 Hosted runner spike — acceptance: a written options note (isolation, inbound networking, cost) with a decision proposal. (rough)
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.9.2-T1 | Spike: hosted-runner options and the security implications of inbound execution (rough) | M | V3 | M5 | 11.2.1 |

### Epic 05.10 Periodic consistency jobs
#### Story 05.10.1 Detect and repair drift — acceptance: a stuck run and an expired lease are each detected within one sweep interval, repaired idempotently, and audited; no business logic is re-run by a repair.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 05.10.1-T1 | Stuck-run detector (no transition before deadline) → single reconcile retry, then alert | M | V1 | M2 | 05.3.2-T2 |
| 05.10.1-T2 | Expired-lease sweeper → re-queue as a new lease with epoch bump, stale evidence preserved (leases exist in agent mode, so this is V2) | M | V2 | M4 | 05.7.2-T1 |
| 05.10.1-T3 | Hook interface for orphan artifacts / missing usage sweeps (implemented in 06 and 12) + operator runbook note | S | V1 | M2 | 06.4.3, 12.3.1-T1 |

## 10. Dependencies

| Plan | Needed for | Story ids (checked against the sibling plan files 2026-09-24) |
|---|---|---|
| 01 | contracts pipeline, migrations + RLS roles, outbox skeleton, harness | 01.3.1, 01.4.1, 01.4.2, 01.5.1, 01.6.1 |
| 02 | project scoping, permissions, producer/agent identity, isolation suite | 02.1.1, 02.2.1, 02.4.1, 02.5.1, 02.6.1, 02.9.1 |
| 03 | case revisions, suite revisions, variants, run data namespaces | 03.1.3, 03.2.1, 03.3.1 |
| 04 | event + attempt commit path, adapter capability, finalize endpoint surface, engine version declaration | 04.1.1-T1, 04.3.1-T1, 04.7.1-T1, 04.7.1-T2 |
| 06 | artifact readiness, evidence freeze interface (05 persists the revision), orphan sweeper | 06.1.3, 06.4.3, 06.5.1 |
| 07 | policy revision pinning, late evidence, release aggregation, flaky/subset rules | 07.1.1, 07.2.1, 07.4.1, 07.5.3 |
| 09 | run detail as the truth surface (cancel/rerun, stale evidence, unschedulable) | 09.3.1 |
| 10 | worker deployment, sweeper schedules, load test for claims | 10.2.2-T1, 10.3.1-T2, 10.6.1-T1, 10.6.1-T3 |
| 11 | untrusted CI/agent threat model, secret references, isolation proof | 11.2.1, 11.3.1, 11.7.1 |
| 12 | usage of claimed jobs and released runs | 12.3.1 |

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| Q1 | Are 90 s lease / 15 s heartbeat right for the pilot's slowest suite? (tune after measurement) | Tech Lead | M2 |
| Q2 | Default `collection_deadline_at` and late window per project (policy defaults) | QA Lead | M2 |
| Q3 | Should a subset rerun ever satisfy an exemption-based policy path, or always fail full-regression gates? | QA Lead | M3 |
| Q4 | Is agent mode (and, if yes, Android agent mode keeping 05.8 in V2) wanted for the pilot, or CI mode only until a demand appears? | Product owner | M4 start |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 05.1 Manifest | 7.5 | — | — | 7.5 |
| 05.2 Run creation | 5.0 | — | — | 5.0 |
| 05.3 Lifecycle, finalize, reconcile | 10.0 | — | — | 10.0 |
| 05.4 Cancel | 2.5 | — | — | 2.5 |
| 05.5 Rerun | 2.0 | — | — | 2.0 |
| 05.6 Release and components | 2.0 | — | — | 2.0 |
| 05.7 Agent mode | — | 18.0 | — | 18.0 |
| 05.8 Android device lease | — | 3.0 | — | 3.0 |
| 05.9 iOS and hosted runners (placeholder, rough) | — | — | 2.0 | 2.0 |
| 05.10 Consistency jobs | 1.5 | 1.0 | — | 2.5 |
| **Total** | **30.5** | **22.0** | **2.0** | **54.5** |

→ with 20 % buffer **65.4** person-days (V1 30.5, V2 22.0, V3 2.0 rough).

## 13. Contract change requests

> **Status:** Applied 2026-09-24: CR-1, CR-2 → 00 §9.3. Applied 2026-09-25 by integrator: CR-3 → 00 §2/§9.3.

| # | Request | Why | Affects |
|---|---|---|---|
| CR-1 | Add `POST /v1/runs/{r}/rerun` (permission `run:create`; body: `rerun_scope`, selection) to 00 §9.3 | Rerun as a new run with `parent_run_id` + scope is a first-class operation in 00 §8.1 and the source §5, but has no endpoint | 00 §9.3, 04 (CLI), 09 (UI) |
| CR-2 | Add `POST /v1/projects/{p}/releases` and `GET /v1/releases/{id}` to 00 §9.3 | A release (component → digest) must be created by API, not only by UI, to let CI declare build digests with the run | 00 §9.3, 07 (evaluation inputs), 09 (UI) |
| CR-3 | 00 §2/§9.3: add `environment:write` (default `tenant_admin` + `test_maintainer`) and the environment endpoints — Applied 2026-09-25 by integrator | Environment revisions are pinned by manifests, runs and JobSpecs, but they had no owner, permission or endpoint; 00 §9.3 now carries `POST /v1/projects/{p}/environments/{e}/revisions` + `GET /v1/environment-revisions/{id}` and 00 §5.4 the owner (05) | 00 §2, 00 §9.3, 00 §5.4, 02 (`project.default_environment_id` composite FK, added by 05.1.2-T1), 03 §4 (`data_isolation` suite declaration read by the lock) |
