# 07 - Policy and Decisions (M2-M3)

> Contracts: `00-overview.md` (§8.5 completeness/evaluation, §8.6 decision rules and policy fields, §8.8 waiver/quarantine, §9.2 errors, §9.3 endpoints, §9.5 webhooks, §11 NFRs). Facts: `../DECISIONS.md` §3 (A3, A8, A9, A10), §4, §9, F3.
> Source: design §5 "Chính sách đánh giá release" (decision table, policy v1 rules, waiver, late evidence), §5 "Trình tự chuẩn", §6 "Toàn vẹn tenant và đồng thời" (one evaluation per evidence/policy pair), §9 (canary + shadow decisions).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.

## 1. Purpose and scope

**Goal:** turn a frozen evidence revision plus a pinned policy revision into one immutable, explainable, replayable decision — and deliver it to the CI that waits for it, without ever letting a missing-evidence run look green.

**In scope:** policy revisions (immutability, hash, activation, pinning), the evaluator as a pure function, the completeness/outcome/decision split (A8), decision reasons, flaky detection and budget, waivers and quarantine, late evidence creating a new evaluation revision, run- and release-level decision reads, CI delivery (GitHub check run/commit status, CLI `evidgate gate` exit codes), outbound webhooks (00 §9.5), advisory then enforcing modes, and shadow evaluation during our own deploys.

**Out of scope:** healing (08) — shadow results never reach the evaluator; UI screens (09); CI token exchange and CLI plumbing (02/04); billing (12). The evaluator never reads raw events, only the frozen evidence revision, the pinned policy revision and the exceptions snapshot (00 §8.5, T24).

**Version split**
- **V1 (M2):** policy revisions + hash, pure evaluator, decision table, reasons, flaky detection/budget, waivers, quarantine, `GET /v1/runs/{r}/decision`, webhooks, advisory mode.
- **V1 (M3):** enforcing mode per project, release-level decision over components, CI delivery.
- **V2 (M4):** cross-run flaky history, releases that include mobile app digests, richer reason payloads for the UI.
- **V3 (M5+):** reusable policy templates across projects/organizations `[OPEN]`.

## 2. Requirements

- **FR-07-1** A policy revision is immutable and content-addressed (`policy_hash = sha256:<hex>` over RFC 8785 canonical JSON, 00 §7), carries exactly the 00 §8.6 fields, and is never edited — revising means a new revision. Unknown fields are rejected at write time.
- **FR-07-2** Every run pins one policy revision at creation (00 §9.3); activating a new revision never changes what an existing run is evaluated against.
- **FR-07-3** The evaluator is a pure function `(evidence revision, policy revision, exceptions snapshot) → evaluation` (T24): no clock, no network, no mutable state. The snapshot is the set of active waiver and quarantine ids resolved at trigger time plus `exceptions_as_of`, stored on the row as `exceptions_hash`. A replay of the same inputs is byte-identical, and live and shadow evaluation use the same code path.
- **FR-07-4** Exactly one evaluation exists per `(org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode)` and at most one `live` row per `(org_id, run_id, evidence_revision)` (00 §8.5, T22); a concurrent duplicate loses to the unique constraint and returns the existing row, and a shadow evaluation never collides with the live one.
- **FR-07-5** Completeness (`complete` | `incomplete` + `missing[]`), outcome (`all_passed` | `has_failures` | `no_results`) and decision (`PASS` | `FAIL` | `INCONCLUSIVE`) are separate fields (A8). A confirmed failure is `FAIL` **with** `completeness = incomplete` when evidence is missing — never silently `INCONCLUSIVE`.
- **FR-07-6** Every evaluation stores `manifest_hash`, `evidence_revision`, `policy_hash`, `reasons[]`, the evaluator version and `exceptions_hash` + `exceptions_as_of` (T24), so any decision can be re-derived and explained later.
- **FR-07-7** Flaky detection compares attempts within a run (retry fail → pass). The pilot default blocks critical cases and warns otherwise, per the policy's flaky budget; a non-critical retry fail → pass beyond the budget yields `INCONCLUSIVE` with reason `flaky_budget_exceeded` (T27). The flaky fact is always a reason, never a silent pass.
- **FR-07-8** Waivers are human risk acceptance, separate from test results: scope, reason, approver, expiry, `approver ≠ requester`; a waiver never converts a failure into a pass and is shown as "approved by waiver" beside the result (00 §8.8).
- **FR-07-9** Quarantine excludes a case from gating with owner, reason and expiry; its failures stay visible (00 §8.6, §8.8). A case on the pinned policy's critical list cannot be quarantined — `422 critical_case_not_quarantinable`; removing it takes a new policy revision by a `release_approver` (T23).
- **FR-07-10** Late evidence inside the policy's late window produces a new evidence revision and therefore a new evaluation, triggered by an outbox task — there is no `/v1` re-evaluation endpoint; manual re-evaluation is an operator route under `/internal/operator/*` (T26, 00 §9.1). A decision already consumed by a release stays immutable and queryable.
- **FR-07-11** `GET /v1/runs/{r}/decision` (`decision:read`) returns the exact evaluation revision with reasons, or `pending` before any evidence revision is frozen (00 §9.3).
- **FR-07-12** A release-level decision aggregates its component runs and never stitches "latest run per component" by time: a component whose digest does not match the release is reported missing (source §5).
- **FR-07-13** CI delivery posts the decision to GitHub and `evidgate gate` exits `0` PASS, `1` FAIL, `2` INCONCLUSIVE, `3` error `[DECISION]`. A delivery failure never changes the stored decision.
- **FR-07-14** Outbound webhooks follow 00 §9.5 exactly: `Evidgate-Event-Id`, `Evidgate-Timestamp`, `Evidgate-Signature: v1=<hmac-sha256(secret, timestamp + "." + body)>`, receivers reject timestamps older than 5 min, at-least-once with exponential retry for 24 h, types `run.state_changed`, `evaluation.created`, `healing.candidate_changed` (V2).
- **FR-07-15** Modes: `advisory` (M2 — computed, stored, shown and delivered, never blocking) and `enforcing` (M3 — the gate blocks). The mode is a per-project setting with audit, reversible at any time.
- **FR-07-16** Our own deployments evaluate a new policy in shadow and compare it with the live decision before rollout (source §9 canary step).

**Acceptance criteria (testable):** a run with one confirmed failure and one missing shard yields `FAIL`, `completeness = incomplete`, `missing = [missing_shard]`; replaying the same inputs yields the same evaluation hash and writes no second row, and a shadow evaluation writes its own row instead of returning the live one (T22); a waiver approved by its requester is refused (403) and no waiver row is written; quarantining a case on the policy's critical list is refused `422 critical_case_not_quarantinable` and its failure still gates (T23); a non-critical retry fail → pass beyond the flaky budget yields `INCONCLUSIVE` with `flaky_budget_exceeded` (T27); late evidence inside the window creates evaluation *n+1* while *n* still returns its original decision; the CLI exits 2 (not 0) for an `INCONCLUSIVE` run.

## 3. Key decisions

- `[DECISION]` **The decision is a stored row, never a computed view** (A3): auditability, replayability, one source for UI, CLI and webhooks. Rejected: computing on read (cannot prove what CI saw) and storing only the latest state (loses history when late evidence arrives).
- `[DECISION]` **The evaluator is pure over three immutable inputs** (evidence revision, policy revision, exceptions snapshot — T24). Rationale: the same function must run live and in shadow and be replayable years later; resolving waivers/quarantine outside the function and hashing the snapshot onto the row keeps it pure without hiding the mutable input. Rejected: reading events/attempts directly (breaks freeze semantics), reading live waiver/quarantine rows inside the function (hidden clock + mutable state), calling an external service (nondeterminism, availability coupling).
- `[DECISION]` **`FAIL` beats missing evidence; missing evidence alone is `INCONCLUSIVE`, never `PASS`** (00 §8.6, A8). Rejected: "incomplete ⇒ always INCONCLUSIVE" (hides product failures) and "any failure ⇒ complete evidence" (hides the missing-data signal).
- `[DECISION]` **Flaky handling is a policy budget, not a global rule**: retry fail → pass blocks a critical required case in the pilot default and warns otherwise. Rejected: auto-passing retried cases (launders nondeterminism), blocking on every flaky (teams stop trusting the gate).
- `[DECISION]` **Waivers and quarantine are tables of their own**, referenced by the policy revision's quarantine list (00 §8.6). Rejected: exceptions inside the policy JSON (no per-entry owner, reason, expiry or approver separation).
- `[DECISION]` **Late evidence rewrites nothing**: new evidence revision → new evaluation; the consumed decision remains the one a release used (source §5, A3).
- `[DECISION]` **GitHub delivery uses a GitHub App for check runs, with commit statuses as fallback** — check-run *write* is available to GitHub Apps only; OAuth apps and users can read check runs but not create them ✅ ([checks/runs](https://docs.github.com/en/rest/checks/runs), read 2026-09-24); commit statuses are posted by external services with a `context` and states `error|failure|pending|success` ✅ ([commit statuses](https://docs.github.com/en/rest/commits/statuses), read 2026-09-24).
- `[DECISION]` **CLI exit codes** `0` PASS, `1` FAIL, `2` INCONCLUSIVE, `3` error: a transport error must be distinguishable from a real failure and from a pass.
- `[DECISION]` **Advisory first, enforcing later, per project** (M2 → M3): the M2 exit gate is proved on advisory data before anything blocks a customer release.

## 4. Design

**Tables owned by 07**

| Table | Key columns | Constraints |
|---|---|---|
| `policy_revision` | `id`, `org_id`, `project_id`, `revision_no`, `hash`, `body` (00 §8.6 fields incl. `quarantine_ids[]`), `state` (`draft`/`active`/`retired`), `created_by` | `UNIQUE (org_id, project_id, revision_no)`, `UNIQUE (org_id, project_id, hash)` (T25); immutable after write |
| `evaluation` | `id`, `org_id`, `run_id`, `evidence_revision`, `policy_revision_id`, `policy_hash`, `manifest_hash`, `completeness`, `missing[]`, `outcome`, `decision`, `reasons[]`, `evaluator_version`, `mode` (`live`/`shadow`), `exceptions_hash`, `exceptions_as_of` | `UNIQUE (org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode)` + partial unique `… WHERE mode = 'live'` so one live row per `(org_id, run_id, evidence_revision)` (00 §8.5, T22, T24) |
| `release_decision` | `id`, `org_id`, `release_id`, `policy_revision_id`, `component_evaluations[]`, `completeness`, `decision`, `reasons[]`, `inputs_hash` | `UNIQUE (org_id, release_id, policy_revision_id, inputs_hash)` |
| `waiver` | `id`, `org_id`, `project_id`, `scope`, `target_id`, `reason`, `requested_by`, `approved_by`, `state`, `expires_at` | `CHECK (approved_by <> requested_by)`; states from 00 §8.8 |
| `quarantine_entry` | `id`, `org_id`, `project_id`, `case_id`, `owner`, `reason`, `state`, `expires_at` | states from 00 §8.8 |
| `policy_activation` | `org_id`, `project_id`, `policy_revision_id`, `mode`, `activated_by`, `activated_at` | `UNIQUE (org_id, project_id)`; audit row per change |
| `ci_delivery` | `org_id`, `run_id`, `evaluation_id`, `provider`, `target_ref`, `external_id`, `state`, `attempts`, `last_error` | `UNIQUE (org_id, run_id, evaluation_id, provider)` |
| `webhook_delivery` | `id`, `org_id`, `event_type`, `event_id`, `endpoint_id`, `state`, `attempts`, `next_attempt_at`, `last_error` | `UNIQUE (org_id, endpoint_id, event_id)` (A10 dedupe) |

`release` and `release_component` belong to 05; 07 reads them through 05's exported view (00 §5.1).

**Endpoints:** `GET /v1/runs/{r}/decision` (`decision:read`, 00 §9.3); `POST /v1/projects/{p}/policies` + `GET /v1/policies/{id}`/`.../revisions` (`policy:write`/`policy:read`, append-only, `If-Match` on drafts); `POST /v1/projects/{p}/policy-activation` (`release_approver`, sets mode, audited); `POST /v1/waivers` + `/approve` + `/revoke` (`release_approver`); `POST /v1/quarantine` + `DELETE /v1/quarantine/{q}` (`test_maintainer`; `422 critical_case_not_quarantinable` on a critical-list case, T23); `GET /v1/releases/{id}/decision` (`decision:read`, see §13). No `/v1` re-evaluation endpoint (T26): late evidence triggers re-evaluation by outbox task (00 §5.3); manual re-evaluation is `POST /internal/operator/runs/{r}/evaluations` on the operator ingress, `platform_operator` only (00 §9.1).

**Flow (worker):** reconcile freezes an evidence revision (05) → outbox row → the `task` queue delivers it to an `Evidgate.Worker` task handler (00 §5.3) → the handler resolves the exceptions snapshot (active waiver/quarantine ids, `exceptions_as_of`, `exceptions_hash`) and loads the pinned policy revision → the evaluator, a plain C# function over the three immutable inputs (T24), computes the decision → one INSERT guarded by the unique constraint → `evaluation.created` webhook + CI delivery. Shadow path: same evaluator, `mode = shadow` (never collides with the live row, T22), stored and compared, never delivered.

**Decision logic** — 00 §8.6 is the contract, implemented in this order: completeness (missing shard, build mismatch, unknown status, setup error, expected count 0 without an empty-suite declaration) → outcome over the required scope → decision (confirmed failure of a required assertion ⇒ `FAIL`; else any missing/invalid evidence ⇒ `INCONCLUSIVE`; else critical skip/blocked ⇒ not `PASS`; else a critical retry fail→pass blocked by the flaky budget ⇒ `FAIL` (M10, pilot default), a retry fail→pass within budget ⇒ `PASS` with a flaky warning (M11), and a non-critical retry fail→pass beyond the budget ⇒ `INCONCLUSIVE` with `flaky_budget_exceeded` (M17, T27) — `07.4.1-T2`; else `PASS` for the pinned scope, build and policy only). A critical case can never be quarantined (T23), so quarantine never launders a critical failure.

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Evidence arrives after the decision was used for a release | New evidence revision → new evaluation inside the late window; the consumed decision is untouched and queryable | A3: used decisions are immutable |
| Two workers evaluate the same pair concurrently | Unique key incl. `evaluator_version` + `mode` (T22); the loser returns the winner's row | One evaluation per evidence × policy × evaluator × mode |
| Evaluator crashes after computing, before INSERT | Outbox retry recomputes; determinism makes the result identical | Replayability (FR-07-3) |
| Policy revision retired while runs still reference it | Runs keep their pinned revision; only new runs use the active one | Pinning (FR-07-2) |
| CI asks before any evidence revision exists | `pending`; the CLI deadline exits 2, never 0 | Missing evidence never yields PASS |
| Expected count is 0 | `INCONCLUSIVE` unless the suite is declared empty and the policy allows it (00 §8.6) | Empty runs are never green by accident |
| Retry fail → pass on a critical case | Blocks under the pilot default (reason `flaky_critical_retry_pass`); a non-critical breach within budget warns, beyond budget is `INCONCLUSIVE` with `flaky_budget_exceeded` (T27) | Flakiness is visible, not laundered |
| Waiver requested and approved by the same person | Refused by the API and by the DB check | Separation of duties (00 §8.8) |
| Waiver expires while a release is open | The stored evaluation is immutable and still displays the waiver as expired via its `exceptions_hash`/`exceptions_as_of` snapshot; expiry is applied by the *next* evaluation — new evidence revision or operator re-run — which resolves a fresh snapshot without the expired waiver (T24), never a silent recompute | Risk acceptance has a deadline |
| Quarantine entry expires mid-run | Evaluated against the exceptions snapshot resolved at trigger time (T24), never live rows | No partial application |
| GitHub API down or App token expired | `ci_delivery` retries with backoff; the CLI path still works | Delivery failure never fabricates a decision |
| Webhook receiver down > 24 h or replaying | Dead-letter set visible and replayable by `event_id`; receivers dedupe and reject > 5 min skew | At-least-once, never silent loss |
| Subset rerun (`rerun_scope = failed_subset`) offered as gate input | Labelled; the release aggregate reports coverage instead of a full regression (00 §8.1) | A subset is never a full pass |
| Shadow healing passes a case that failed officially | Shadow results are excluded from the evaluator; the original decision stands | Healing never overrides evidence |
| Policy JSON carries an unknown field | Write-time rejection (`422`) with a path in `details` (00 §9.2) | Policy semantics never drift silently |
| Quarantine targets a case on the policy's critical list | `POST /v1/quarantine` refuses `422 critical_case_not_quarantinable` (T23); only a new policy revision by a `release_approver` removes it | Critical gating cannot be laundered into PASS |

## 6. Security and privacy

- **Decision integrity:** evaluations are written only by the worker's service identity inside an org-scoped transaction; no tenant-facing endpoint writes or mutates an evaluation. Reasons carry ids and counts, never artifact content or secrets (00 §9.2).
- **Policy authority:** `release_approver` owns policy revisions and waivers; `qa_engineer` cannot change release policy (00 §2). Every activation, approval, revocation and quarantine change is audited with actor, scope and hash.
- **CI identity:** delivery uses the GitHub App installation (F3 ✅, repo-scoped); the check run carries the decision and a link, not evidence contents.
- **Webhook secrets:** per-endpoint HMAC secret, never logged; signature over `timestamp + "." + body`; a published reference receiver and a replay/skew conformance fixture.
- **Privacy:** evaluations reference artifact ids but embed no personal data, so evidence deletion (06) does not destroy decision explainability — a deleted artifact reads as `artifact missing (deleted by retention)`, never as present.

## 7. Test strategy

**Decision-table matrix (every row of 00 §8.6 plus the fields it depends on)** — golden matrix tests against the pure evaluator (plain C# function, xUnit v3 + Shouldly) with hand-built evidence revisions, policy revisions and exceptions snapshots (no database, no clock), replayed against stored rows to prove determinism.

| # | Evidence situation | Completeness | Outcome | Decision | Key reason |
|---|---|---|---|---|---|
| M1 | All required cases passed, all shards present, required artifacts `ready` | `complete` | `all_passed` | `PASS` | `scope_build_policy_pinned` |
| M2 | Confirmed failure of a required assertion, other evidence missing | `incomplete` | `has_failures` | `FAIL` | `required_assertion_failed` |
| M3 | Missing shard | `incomplete` | per available results | `INCONCLUSIVE` | `missing_shard` |
| M4 | Build/digest mismatch | `incomplete` | per available results | `INCONCLUSIVE` | `build_mismatch` |
| M5 | Unknown producer status | `incomplete` | per available results | `INCONCLUSIVE` | `unknown_status` |
| M6 | Setup error (dependants `blocked`, never `passed`) | `incomplete` | `has_failures` | `INCONCLUSIVE` | `setup_error` |
| M7 | Expected count 0, suite not declared empty | `incomplete` | `no_results` | `INCONCLUSIVE` | `expected_count_zero` |
| M8 | Expected count 0, suite declared empty and allowed | `complete` | `no_results` | `PASS` | `empty_suite_allowed` |
| M9 | Critical case skipped or blocked | `complete`/`incomplete` | `all_passed` | not `PASS` (`INCONCLUSIVE`) | `critical_not_executed` |
| M10 | Retry fail → pass on a critical case | `complete` | `all_passed` | `FAIL` (pilot default) | `flaky_critical_retry_pass` |
| M11 | Retry fail → pass on a non-critical case within budget | `complete` | `all_passed` | `PASS` | `flaky_within_budget` |
| M12 | Healing candidate passes only in shadow | unchanged | unchanged | original decision kept | `shadow_not_evidence` |
| M13 | Required artifact missing or `rejected` | `incomplete` | per available results | `INCONCLUSIVE` | `artifact_missing` |
| M14 | Only failure is a case with an active quarantine entry | `complete` | `has_failures` | `PASS` with `quarantined_cases[]` | `quarantined_case_failed` |
| M15 | Quarantine entry expired before evaluation; waiver approved for a failure | `complete` | `has_failures` | `FAIL` (waiver displayed only) | `quarantine_expired` / `waiver_approved` |
| M16 | Only stale-epoch events received (superseded lease) | `incomplete` | `no_results` | `INCONCLUSIVE` | `stale_epoch_only` |
| M17 | Retry fail → pass on a non-critical case, flaky budget exceeded (T27) | `complete` | `all_passed` | `INCONCLUSIVE` | `flaky_budget_exceeded` |
| M18 | Failure on a critical case the caller tried to quarantine — write refused `422 critical_case_not_quarantinable`; if a snapshot still contains the entry the evaluator ignores it (T23) | `complete` | `has_failures` | `FAIL` | `required_assertion_failed` |

- **Unit:** policy canonicalisation and hashing; each matrix row (M1–M18); reason-code emission; late-window arithmetic; flaky detection from attempts; waiver/quarantine transitions incl. illegal ones; exceptions-snapshot resolution and `exceptions_hash` stability (T24).
- **Integration (real PostgreSQL):** concurrent duplicate evaluation writes one row and a shadow row never collides with the live row (T22); late evidence produces revision *n+1* while *n* stays byte-identical; `GET /v1/runs/{r}/decision` returns `pending` then the exact revision; release aggregation refuses a component whose digest does not match; `ci_delivery` re-post is idempotent under a GitHub 500; quarantining a critical-list case is refused 422 (T23).
- **CLI and webhooks:** `evidgate gate` exit codes 0/1/2/3 against a fixture service including the timeout path (→ 2); a receiver fixture verifies the signature, rejects a 6-minute-old timestamp and dedupes a replayed `Event-Id`.
- **Shadow:** deploy-shaped comparison of live vs shadow decisions over a recorded fixture set; the divergence report must be non-empty when a rule changes.

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| A wrong policy revision blocks a customer release | Medium | High | Advisory default, per-project enforcement, `If-Match` + audited activation, shadow comparison before rollout, fast rollback to advisory |
| `INCONCLUSIVE` treated as a pass by a CI script | Medium | High | Distinct exit code 2; check-run conclusions `neutral`/`action_required`, never `success` ✅ ([checks/runs](https://docs.github.com/en/rest/checks/runs)); documentation + fixture test |
| Evaluator drifts to nondeterministic behaviour | Low | High | Pure inputs incl. the hashed exceptions snapshot (T24), evaluator version stored per row, stored-row replay test in CI |
| Flaky budget tuned to hide real failures | Medium | High | Flaky facts are always reasons; critical cases block by default; budget changes are reviewed policy revisions |
| Waiver culture (everything gets waived) | Medium | Medium | Approver ≠ requester, mandatory expiry, waivers displayed beside results and counted in pilot reporting (12) |
| Receivers implement signature checks wrongly | Medium | Medium | Reference receiver + conformance fixture; 5-min skew and replay tests; at-least-once documented |
| GitHub App permission surface grows beyond checks/statuses | Low | Medium | Minimal install permissions (checks/statuses write only), separate from any future healing-PR app (08) |
| Release aggregation mixes runs from different builds | Medium | High | Digest match enforced inside the aggregate; mismatches reported as missing, never averaged (source §5) |
| Evaluation throughput becomes a bottleneck | Low | Medium | Pure in-process function, one row write, task-queue backpressure (`FOR UPDATE SKIP LOCKED`, 00 §5.3); decision-latency SLI p95 < 60 s measured by 07.6.2-T3 (00 §11, T28) |

## 9. Epics, stories and tasks

### Epic 07.1 Policy revision model

#### Story 07.1.1 Revision record, hash, activation and pinning — acceptance: identical bodies hash equal, a stored revision cannot be updated, and a run keeps the revision it pinned after a new activation.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.1.1-T1 | `policy_revision` table + migration (00 §8.6 fields, immutable, `hash`) | M | V1 | M2 | 01.4.1-T1, 01.4.1-T3, 02.6.1-T1 |
| 07.1.1-T2 | Canonicalisation + `sha256:` hash service (RFC 8785), hash verification on read | S | V1 | M2 | 07.1.1-T1 |
| 07.1.1-T3 | Policy endpoints (append-only revision, list, `If-Match` on drafts, `422` on unknown fields) | M | V1 | M2 | 07.1.1-T2 |
| 07.1.1-T4 | `policy_activation` + mode + audit; run-pinning handshake with 05 | S | V1 | M2 | 07.1.1-T3, 05.2.1-T1 |

### Epic 07.2 Evaluator

#### Story 07.2.1 Pure evaluation: completeness, outcome, decision — acceptance: same inputs give the same output in a fresh process; the three fields are populated independently and `FAIL` with `incomplete` is representable.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.2.1-T1 | Evaluator core as a plain C# function over the three immutable inputs (evidence revision, policy revision, exceptions snapshot — T24) + evaluator version constant (pure, no clock/network) | M | V1 | M2 | 05.3.2-T5, 06.5.1-T1 |
| 07.2.1-T2 | Completeness/outcome/decision computation + `reasons[]` catalogue | M | V1 | M2 | 07.2.1-T1 |

#### Story 07.2.2 Persistence, idempotency and the decision matrix — acceptance: a duplicate write returns the existing row, a shadow evaluation writes its own row (T22), and every row of 00 §8.6 (M1–M18 in §7) is covered by a passing test.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.2.2-T1 | Evaluation task handler in `Evidgate.Worker` (claims the `task` row 00 §5.3, loads inputs, INSERT guarded by the T22 unique key, returns winner on conflict), deterministic replay test | M | V1 | M2 | 07.2.1-T2, 01.5.1-T2, 01.5.1-T3 |
| 07.2.2-T2 | Golden matrix tests M1–M18 (xUnit v3, table-driven, no DB/clock) + stored-row replay test | M | V1 | M2 | 07.2.1-T2 |
| 07.2.2-T3 | Exceptions snapshot resolved at trigger time in the handler (active waiver/quarantine ids + `exceptions_as_of`) and stored as `exceptions_hash` on the evaluation (T24) | S | V1 | M2 | 07.2.2-T1, 07.3.1-T1, 07.3.1-T2 |
| 07.2.2-T4 | Replay runs (`run.purpose = replay`, T67) are evaluated only with `mode = shadow`, never `live`, and never enter release aggregation (07.5.3); golden-matrix row + test | XS | V1 | M2 | 07.2.2-T1, 05.2.1-T5 |

### Epic 07.3 Waivers and quarantine

#### Story 07.3.1 Waiver and quarantine lifecycles — acceptance: a waiver approved by its requester is refused; an active quarantine excludes a case from gating while its failure stays visible; quarantining a case on the policy's critical list is refused `422 critical_case_not_quarantinable` (T23).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.3.1-T1 | `waiver` table, states, endpoints, `approver ≠ requester` (API + DB check) | M | V1 | M2 | 02.2.1-T1, 02.2.1-T2 |
| 07.3.1-T2 | `quarantine_entry` table, states, endpoints, policy reference, critical-list refusal `422 critical_case_not_quarantinable` (T23) | M | V1 | M2 | 07.1.1-T1 |

#### Story 07.3.2 Evaluator effects, expiry and audit — acceptance: M14, M15 and M18 pass; no waiver path produces `PASS` from a failed required assertion; expiry changes the next evaluation via its exceptions snapshot (T24).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.3.2-T1 | Waiver/quarantine effects in the evaluator + reason codes | M | V1 | M2 | 07.2.1-T2, 07.3.1-T1, 07.3.1-T2 |
| 07.3.2-T2 | Expiry sweeper (Cronos schedule, unique `(schedule_name, fire_at)` fire key, 00 §5.3), state transitions, audit rows, tests | S | V1 | M2 | 07.3.2-T1 |

### Epic 07.4 Flaky detection and budget

#### Story 07.4.1 In-run flaky detection and budget — acceptance: retry fail → pass is detected from attempts, critical cases block under the pilot default, non-critical breaches within budget warn and beyond budget yield `INCONCLUSIVE` (T27).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.4.1-T1 | Flaky detection from attempt history within a run (retry policy from the suite revision) | M | V1 | M2 | 07.2.1-T2, 05.3.3-T1 |
| 07.4.1-T2 | Flaky budget from the policy revision applied to the decision (critical block, within-budget warning, over-budget `flaky_budget_exceeded` ⇒ `INCONCLUSIVE`, T27) | M | V1 | M2 | 07.4.1-T1 |

#### Story 07.4.2 Flaky surface and cross-run trend — acceptance: flaky facts appear in reasons, webhooks and CLI output; the V2 trend label changes no V1 gating semantics.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.4.2-T1 | Flaky reasons in evaluation, webhook payload and CLI output | S | V1 | M2 | 07.4.1-T2 |
| 07.4.2-T2 | Cross-run flaky trend (read table + label) | S | V2 | M4 | 07.4.1-T1, 05.3.3-T1 |

### Epic 07.5 Late evidence, immutability and release decisions

#### Story 07.5.1 Late evidence and immutability — acceptance: in-window late evidence creates evaluation *n+1*; revision *n* remains identical and queryable; no code path mutates a used decision.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.5.1-T1 | Late-window handling + re-evaluation via outbox task (no `/v1` endpoint) + `POST /internal/operator/runs/{r}/evaluations` operator route (T26), immutability guard | M | V1 | M2 | 07.2.2-T1, 05.3.2-T3 |
| 07.5.1-T2 | Immutability suite (used decision + late evidence + re-evaluation) | S | V1 | M2 | 07.5.1-T1 |

#### Story 07.5.2 Run decision API — acceptance: returns the exact revision with reasons, or `pending`, with `decision:read` enforced.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.5.2-T1 | `GET /v1/runs/{r}/decision` (exact revision, reasons, `pending` path) | S | V1 | M2 | 07.2.2-T1, 01.3.1-T1, 02.2.1-T1 |

#### Story 07.5.3 Release aggregation and decision endpoint — acceptance: a component whose digest does not match the release is reported missing instead of using the newest run; the endpoint returns per-component decisions.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.5.3-T1 | `release_decision` aggregation over 05's release/components (digest match enforced) | M | V1 | M3 | 07.2.2-T1, 05.6.1-T1 |
| 07.5.3-T2 | `GET /v1/releases/{id}/decision` + status per component (see §13) | S | V1 | M3 | 07.5.3-T1 |

#### Story 07.5.4 Multi-component releases with mobile digests — acceptance (V2): app digests participate in the aggregate under the same mismatch rules.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.5.4-T1 | App-component support in release aggregation | S | V2 | M4 | 07.5.3-T1, 04.5.1-T2 |

### Epic 07.6 CI integration and webhooks

#### Story 07.6.1 CI delivery: GitHub and the CLI gate — acceptance: PASS/FAIL/INCONCLUSIVE map to `success`/`failure`/`neutral`, a re-post is idempotent, and `evidgate gate` exits 0/1/2/3 with a deadline that yields 2, never 0.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.6.1-T1 | GitHub App check-run/commit-status delivery + `ci_delivery` records + retry | M | V1 | M3 | 07.2.2-T1, 02.4.1-T2, 02.4.3-T2 |
| 07.6.1-T2 | `evidgate gate` semantics (exit codes, `--wait` deadline, JSON output) with 04 | M | V1 | M3 | 07.5.2-T1, 04.2.1-T1 |

#### Story 07.6.2 Outbound webhooks and delivery operations — acceptance: 00 §9.5 headers and signature pass the reference receiver, retries last 24 h with backoff, and dead letters are replayable by `event_id`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.6.2-T1 | Webhook transport (signing, timestamp, at-least-once retry) + `evaluation.created` payload | M | V1 | M2 | 07.2.2-T1, 01.5.1-T4 |
| 07.6.2-T2 | Delivery records, dead-letter visibility, replay tool, receiver conformance fixture | S | V1 | M2 | 07.6.2-T1 |
| 07.6.2-T3 | Decision-latency measurement: histogram `evidence_frozen_at → evaluation.created_at` emitted by the handler; this task builds the freeze→evaluate latency fixture (driven by `10.6.1-T1`'s ingest load generator) asserting p95 < 60 s in the M3 load run; SLI registered in `10.3.1-T3` (T28) | S | V1 | M3 | 07.2.2-T1, 10.3.1-T3, 10.6.1-T1 |

#### Story 07.6.3 Webhook endpoint management and status badge — acceptance: a project admin registers, rotates and disables outbound webhook endpoints, sees per-endpoint delivery history, and may opt in to a status badge that exposes only the decision label.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.6.3-T1 | `webhook_endpoint` (https URL, private-address/SSRF guard, event types, state, secret reference) + CRUD API with show-once signing secret, audited | M | V1 | M3 | 07.6.2-T1, 02.2.1-T2, 02.7.1-T2 |
| 07.6.3-T2 | Secret rotation with an overlap window, per-endpoint delivery history filters and manual redeliver on top of 07.6.2-T2 | S | V1 | M3 | 07.6.3-T1, 07.6.2-T2 |
| 07.6.3-T3 | Tests: SSRF cases (private ranges, redirects, DNS rebinding), rotation overlap, disabled endpoint stops delivery, cross-tenant isolation | S | V1 | M3 | 07.6.3-T2 |
| 07.6.3-T4 | Opt-in status badge: per-project unguessable token URL, off by default, returns only decision label and timestamp | S | V2 | M4 | 07.5.2-T1, 07.6.3-T1 |

**Story 07.6.3 total: 2.5** — `[INFERENCE]` sizes; added from the missing-features review of 2026-10-02.

### Epic 07.7 Rollout: advisory, enforcing and shadow

#### Story 07.7.1 Advisory and enforcing modes — acceptance: in M2 decisions are computed, stored, shown and delivered without blocking; flipping a project to `enforcing` blocks on FAIL and INCONCLUSIVE per policy, is audited and is reversible.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.7.1-T1 | Advisory mode end-to-end (no blocking effect, full reasoning and audit) | S | V1 | M2 | 07.2.2-T1 |
| 07.7.1-T2 | Enforcing semantics + flip/rollback with audit and operator runbook notes | S | V1 | M3 | 07.6.1-T2, 07.7.1-T1 |

#### Story 07.7.2 Shadow evaluation, divergence and rollback — acceptance: a policy or evaluator change is run in `shadow` over recent runs, differences are listed per run before rollout, and rollback to advisory loses no evidence or decision.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.7.2-T1 | Shadow evaluation over recorded evidence + comparison inside 10's deploy flow | M | V1 | M2 | 07.2.2-T1, 10.2.2-T3 |
| 07.7.2-T2 | Divergence report + rollback to advisory + pilot readiness checklist input | S | V1 | M3 | 07.7.2-T1, 07.7.1-T2 |

### Epic 07.8 Promotion gate for deployments [PROPOSAL missing-features review 2026-10-02]
#### Story 07.8.1 Promotion gate: release to environment — acceptance: a CD pipeline asks whether a release may be promoted to a named target and gets allowed, blocked or pending with reasons from the release decision and the target's policy; a missing component is never allowed.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.8.1-T1 | `deploy_target` (project-scoped name such as staging/prod, order, optional pinned policy revision) + CRUD with ETag, audited | M | V1 | M3 | 02.1.1-T1, 07.1.1-T1, 02.7.1-T2 |
| 07.8.1-T2 | `POST /v1/releases/{id}/promotions` evaluates the release decision under the target's policy and writes an immutable `promotion_check` (allowed \| blocked \| pending, reasons, decision and policy revision); idempotent per release, target and decision revision | L | V1 | M3 | 07.8.1-T1, 07.5.3-T2 |
| 07.8.1-T3 | `evidgate gate --release <id> --target <name>` with the exit codes 0/1/2/3, `--wait` and JSON output | S | V1 | M3 | 07.8.1-T2, 07.6.1-T2 |
| 07.8.1-T4 | `promotion.checked` webhook event, audit row and `GET /v1/releases/{id}/promotions` history | S | V1 | M3 | 07.8.1-T2, 07.6.2-T1 |
| 07.8.1-T5 | Tests: missing component never allowed, per-target policy differences, a waiver cannot turn FAIL into allowed, late evidence creates a new check row | S | V1 | M3 | 07.8.1-T2 |

**Story 07.8.1 total: 4.5** — `[INFERENCE]` sizes; added from the missing-features review of 2026-10-02.

**Epic 07.8 total: 4.5**

## 10. Dependencies

- **01 foundation** (`01.3.1-T1` OpenAPI skeleton incl. the 00 §9.3 endpoints, `01.4.1-T1`/`01.4.1-T3` migrations + RLS baseline, `01.5.1-T2`/`01.5.1-T3` outbox + dispatcher, `01.5.1-T4` handler contract + dead-letter): contracts pipeline (policy/evaluation payloads), migrations, CI fixtures, dogfooding (our CI results are the first real policy input, R10).
- **02 identity/tenancy** (`02.2.1-T1`/`02.2.1-T2` permission catalogue + guard, `02.6.1-T1` RLS, `02.7.1-T1`/`02.7.1-T2` audit core, `02.4.1-T2` project↔GitHub CI binding): `decision:read`, `policy:write`, `release_approver`/`test_maintainer`, RLS, audit core, GitHub App installation record and tokens ⚠️ (02 has no task that stores an App installation id/token; 07.6.1-T1 cites `02.4.1-T2`, and the App registration lives in `03.4.1-T1` — flagged for the integrator).
- **04 ingestion/adapters** (`04.2.1-T1` CLI skeleton + exit codes, `04.5.1-T2` mobile app digest (V2)): CLI `gate`, exit-code plumbing, check-run target metadata, `ingest_only` semantics (viewable but not gate-eligible unless manifest completeness is proven, 00 §10).
- **05 runs/orchestration** (`05.2.1-T1` run creation, `05.3.2-T2`/`05.3.2-T3` reconcile + late window, `05.3.2-T5` `evidence_revision`, `05.3.3-T1` `case_attempt`, `05.6.1-T1` release/components): manifest, run lifecycle, reconcile, evidence-revision creation (interface in `06.5.1-T1`), late-window events, cancel/rerun, release and release_component views.
- **06 evidence/artifacts** (`06.5.1-T1` freeze interface): required-artifact readiness feeding completeness; deleted artifacts must leave decision reasons truthful.
- **08 healing:** shadow results excluded from the evaluator; `healing.candidate_changed` webhook is V2.
- **09/10/11/12:** decision display and waiver rendering (`09.7.1-T4`, 09); deploy canary + shadow comparison (`10.2.2-T3`), decision-latency SLI and dead letters (`10.3.1-T3`, `01.5.1-T4`) (10); authority model, webhook secrets, audit review (11); pilot acceptance ("missing evidence never yields PASS"), enforcement timing, value measurement (12).

Sibling story ids referenced above are ⚠️ pending confirmation: `plans/` contained only `00-overview.md` when this plan was written; ids follow 00 §6 and are reconciled during integration.

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| 07-O1 | Flaky budget defaults and per-severity thresholds for the pilot (critical blocks is the starting default) | QA lead + Owner | M2 mid |
| 07-O2 | Criteria to flip a project from `advisory` to `enforcing` (weeks of clean data? waiver rate?) | Tech Lead + QA lead | M2 end |
| 07-O3 | Which GitHub surface the first pilot uses (check run, commit status, or both) and its branch-protection interaction | Owner + pilot team (O1) | 07.6.1-T1 |
| 07-O4 | Is `PASS` allowed when the policy's allowed skips cover a skipped required case? | QA lead | 07.2.1-T2 |
| 07-O5 | Default length of the late window (field exists; default undecided) | QA lead | 07.5.1-T1 |
| 07-O6 | Are waivers visible to `viewer`, or restricted to release roles? Retention of delivery rows? | Tech Lead | 07.3.1-T1, 07.6.2-T2 |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 07.1 Policy revision model | 3.0 | 0 | 0 | 3.0 |
| 07.2 Evaluator | 4.75 | 0 | 0 | 4.75 |
| 07.3 Waivers and quarantine | 3.5 | 0 | 0 | 3.5 |
| 07.4 Flaky detection and budget | 2.5 | 0.5 | 0 | 3.0 |
| 07.5 Late evidence, immutability and release decisions | 3.5 | 0.5 | 0 | 4.0 |
| 07.6 CI integration and webhooks | 6.0 | 0.5 | 0 | 6.5 |
| 07.7 Rollout: advisory, enforcing and shadow | 2.5 | 0 | 0 | 2.5 |
| 07.8 Promotion gate for deployments | 4.5 | 0 | 0 | 4.5 |
| **Total** | **30.25** | **1.5** | **0** | **31.75** |

→ with 20 % buffer **38.1** person-days (one senior full-stack .NET + Angular developer, CR-D-1; 00 §6 units).

## 13. Contract change requests

> **Status:** Applied 2026-09-24: 07-CR-1 → 00 §9.3; 07-CR-2 → 00 §9.3 note (`contracts/cli.md`).
> 2026-09-25 CR-D-1 + TL review: stack migrated to .NET 10 (evaluator = plain C# function + xUnit golden matrix tests; `Evidgate.Worker` task handler; Cronos expiry sweeper with unique fire key — F-07-8); evaluation unique key extended with `evaluator_version` + `mode` and a one-live-row partial unique (T22/F-07-1); critical cases cannot be quarantined — `422 critical_case_not_quarantinable` (T23/F-07-2); exceptions snapshot is the evaluator's third input, stored as `exceptions_hash`/`exceptions_as_of` (T24/F-07-3); `policy_revision` unique → `(org_id, project_id, hash)` (T25/F-07-4); `/v1` re-evaluation endpoint removed — outbox task + `/internal/operator/*` route (T26/F-07-5); over-budget non-critical retry ⇒ `INCONCLUSIVE` `flaky_budget_exceeded`, matrix M17 (T27/F-07-6); decision-latency task 07.6.2-T3 (T28/F-07-7); matrix M18 added; estimates recomputed.

| # | Request | Why |
|---|---|---|
| 07-CR-1 | Add `GET /v1/releases/{id}/decision` (permission `decision:read`) to 00 §9.3 next to `GET /v1/runs/{r}/decision`. | A release can hold several components; without a release-level read, the CLI, UI and webhooks would each re-implement aggregation. |
| 07-CR-2 | Record the CLI gate exit codes (0 PASS, 1 FAIL, 2 INCONCLUSIVE, 3 error) in the contracts (00 §9.3 note + `packages/cli` contract, with 04). | CI engineers need one published, testable mapping; the code is a `[DECISION]` here, but implementations are checked against the contract file. |
