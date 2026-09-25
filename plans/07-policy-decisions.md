# 07 - Policy and Decisions (M2-M3)

> Contracts: `00-overview.md` (§8.5 completeness/evaluation, §8.6 decision rules and policy fields, §8.8 waiver/quarantine, §9.2 errors, §9.3 endpoints, §9.5 webhooks, §11 NFRs). Facts: `../DECISIONS.md` §3 (A3, A8, A9, A10), §4, §9, F3.
> Source: design §5 "Chính sách đánh giá release" (decision table, policy v1 rules, waiver, late evidence), §5 "Trình tự chuẩn", §6 "Toàn vẹn tenant và đồng thời" (one evaluation per evidence/policy pair), §9 (canary + shadow decisions).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.

## 1. Purpose and scope

**Goal:** turn a frozen evidence revision plus a pinned policy revision into one immutable, explainable, replayable decision — and deliver it to the CI that waits for it, without ever letting a missing-evidence run look green.

**In scope:** policy revisions (immutability, hash, activation, pinning), the evaluator as a pure function, the completeness/outcome/decision split (A8), decision reasons, flaky detection and budget, waivers and quarantine, late evidence creating a new evaluation revision, run- and release-level decision reads, CI delivery (GitHub check run/commit status, CLI `evidgate gate` exit codes), outbound webhooks (00 §9.5), advisory then enforcing modes, and shadow evaluation during our own deploys.

**Out of scope:** healing (08) — shadow results never reach the evaluator; UI screens (09); CI token exchange and CLI plumbing (02/04); billing (12). The evaluator never reads raw events, only the frozen evidence revision (00 §8.5).

**Version split**
- **V1 (M2):** policy revisions + hash, pure evaluator, decision table, reasons, flaky detection/budget, waivers, quarantine, `GET /v1/runs/{r}/decision`, webhooks, advisory mode.
- **V1 (M3):** enforcing mode per project, release-level decision over components, CI delivery.
- **V2 (M4):** cross-run flaky history, releases that include mobile app digests, richer reason payloads for the UI.
- **V3 (M5+):** reusable policy templates across projects/organizations `[OPEN]`.

## 2. Requirements

- **FR-07-1** A policy revision is immutable and content-addressed (`policy_hash = sha256:<hex>` over RFC 8785 canonical JSON, 00 §7), carries exactly the 00 §8.6 fields, and is never edited — revising means a new revision. Unknown fields are rejected at write time.
- **FR-07-2** Every run pins one policy revision at creation (00 §9.3); activating a new revision never changes what an existing run is evaluated against.
- **FR-07-3** The evaluator is a pure function `(evidence revision, policy revision) → evaluation`: no clock, no network, no mutable state. A replay of the same inputs is byte-identical, and live and shadow evaluation use the same code path.
- **FR-07-4** Exactly one evaluation exists per `(org_id, run_id, evidence_revision, policy_revision_id)` (00 §8.5); a concurrent duplicate loses to the unique constraint and returns the existing row.
- **FR-07-5** Completeness (`complete` | `incomplete` + `missing[]`), outcome (`all_passed` | `has_failures` | `no_results`) and decision (`PASS` | `FAIL` | `INCONCLUSIVE`) are separate fields (A8). A confirmed failure is `FAIL` **with** `completeness = incomplete` when evidence is missing — never silently `INCONCLUSIVE`.
- **FR-07-6** Every evaluation stores `manifest_hash`, `evidence_revision`, `policy_hash`, `reasons[]` and the evaluator version, so any decision can be re-derived and explained later.
- **FR-07-7** Flaky detection compares attempts within a run (retry fail → pass). The pilot default blocks critical cases and warns otherwise, per the policy's flaky budget; the flaky fact is always a reason, never a silent pass.
- **FR-07-8** Waivers are human risk acceptance, separate from test results: scope, reason, approver, expiry, `approver ≠ requester`; a waiver never converts a failure into a pass and is shown as "approved by waiver" beside the result (00 §8.8).
- **FR-07-9** Quarantine excludes a case from gating with owner, reason and expiry; its failures stay visible (00 §8.6, §8.8).
- **FR-07-10** Late evidence inside the policy's late window produces a new evidence revision and therefore a new evaluation; a decision already consumed by a release stays immutable and queryable.
- **FR-07-11** `GET /v1/runs/{r}/decision` (`decision:read`) returns the exact evaluation revision with reasons, or `pending` before any evidence revision is frozen (00 §9.3).
- **FR-07-12** A release-level decision aggregates its component runs and never stitches "latest run per component" by time: a component whose digest does not match the release is reported missing (source §5).
- **FR-07-13** CI delivery posts the decision to GitHub and `evidgate gate` exits `0` PASS, `1` FAIL, `2` INCONCLUSIVE, `3` error `[DECISION]`. A delivery failure never changes the stored decision.
- **FR-07-14** Outbound webhooks follow 00 §9.5 exactly: `Evidgate-Event-Id`, `Evidgate-Timestamp`, `Evidgate-Signature: v1=<hmac-sha256(secret, timestamp + "." + body)>`, receivers reject timestamps older than 5 min, at-least-once with exponential retry for 24 h, types `run.state_changed`, `evaluation.created`, `healing.candidate_changed` (V2).
- **FR-07-15** Modes: `advisory` (M2 — computed, stored, shown and delivered, never blocking) and `enforcing` (M3 — the gate blocks). The mode is a per-project setting with audit, reversible at any time.
- **FR-07-16** Our own deployments evaluate a new policy in shadow and compare it with the live decision before rollout (source §9 canary step).

**Acceptance criteria (testable):** a run with one confirmed failure and one missing shard yields `FAIL`, `completeness = incomplete`, `missing = [missing_shard]`; replaying the same inputs yields the same evaluation hash and writes no second row; a waiver approved by its requester is refused (403) and no waiver row is written; late evidence inside the window creates evaluation *n+1* while *n* still returns its original decision; the CLI exits 2 (not 0) for an `INCONCLUSIVE` run.

## 3. Key decisions

- `[DECISION]` **The decision is a stored row, never a computed view** (A3): auditability, replayability, one source for UI, CLI and webhooks. Rejected: computing on read (cannot prove what CI saw) and storing only the latest state (loses history when late evidence arrives).
- `[DECISION]` **The evaluator is pure over two immutable inputs.** Rationale: the same function must run live and in shadow and be replayable years later. Rejected: reading events/attempts directly (breaks freeze semantics), calling an external service (nondeterminism, availability coupling).
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
| `policy_revision` | `id`, `org_id`, `project_id`, `revision_no`, `hash`, `body` (00 §8.6 fields incl. `quarantine_ids[]`), `state` (`draft`/`active`/`retired`), `created_by` | `UNIQUE (org_id, project_id, revision_no)`, `UNIQUE (org_id, hash)`; immutable after write |
| `evaluation` | `id`, `org_id`, `run_id`, `evidence_revision`, `policy_revision_id`, `policy_hash`, `manifest_hash`, `completeness`, `missing[]`, `outcome`, `decision`, `reasons[]`, `evaluator_version`, `mode` (`live`/`shadow`) | `UNIQUE (org_id, run_id, evidence_revision, policy_revision_id)` (00 §8.5) |
| `release_decision` | `id`, `org_id`, `release_id`, `policy_revision_id`, `component_evaluations[]`, `completeness`, `decision`, `reasons[]`, `inputs_hash` | `UNIQUE (org_id, release_id, policy_revision_id, inputs_hash)` |
| `waiver` | `id`, `org_id`, `project_id`, `scope`, `target_id`, `reason`, `requested_by`, `approved_by`, `state`, `expires_at` | `CHECK (approved_by <> requested_by)`; states from 00 §8.8 |
| `quarantine_entry` | `id`, `org_id`, `project_id`, `case_id`, `owner`, `reason`, `state`, `expires_at` | states from 00 §8.8 |
| `policy_activation` | `org_id`, `project_id`, `policy_revision_id`, `mode`, `activated_by`, `activated_at` | `UNIQUE (org_id, project_id)`; audit row per change |
| `ci_delivery` | `org_id`, `run_id`, `evaluation_id`, `provider`, `target_ref`, `external_id`, `state`, `attempts`, `last_error` | `UNIQUE (org_id, run_id, evaluation_id, provider)` |
| `webhook_delivery` | `id`, `org_id`, `event_type`, `event_id`, `endpoint_id`, `state`, `attempts`, `next_attempt_at`, `last_error` | `UNIQUE (org_id, endpoint_id, event_id)` (A10 dedupe) |

`release` and `release_component` belong to 05; 07 reads them through 05's exported view (00 §5.1).

**Endpoints:** `GET /v1/runs/{r}/decision` (`decision:read`, 00 §9.3); `POST /v1/projects/{p}/policies` + `GET /v1/policies/{id}`/`.../revisions` (`policy:write`/`policy:read`, append-only, `If-Match` on drafts); `POST /v1/projects/{p}/policy-activation` (`release_approver`, sets mode, audited); internal `POST /v1/runs/{r}/evaluations` (idempotent re-evaluation on late evidence); `POST /v1/waivers` + `/approve` + `/revoke` (`release_approver`); `POST /v1/quarantine` + `DELETE /v1/quarantine/{q}` (`test_maintainer`); `GET /v1/releases/{id}/decision` (`decision:read`, see §13).

**Flow (worker):** reconcile freezes an evidence revision (05) → outbox row → worker loads `(evidence revision, pinned policy revision)` → pure evaluator → single INSERT guarded by the unique constraint → `evaluation.created` webhook + CI delivery. Shadow path: same evaluator, `mode = shadow`, stored and compared, never delivered.

**Decision logic** — 00 §8.6 is the contract, implemented in this order: completeness (missing shard, build mismatch, unknown status, setup error, expected count 0 without an empty-suite declaration) → outcome over the required scope → decision (confirmed failure of a required assertion ⇒ `FAIL`; else any missing/invalid evidence ⇒ `INCONCLUSIVE`; else critical skip/blocked ⇒ not `PASS`; else a critical retry fail→pass blocked by the flaky budget ⇒ `FAIL` (M10, pilot default), while a retry fail→pass within budget ⇒ `PASS` with a flaky warning (M11) — `07.4.1-T2`; else `PASS` for the pinned scope, build and policy only).

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Evidence arrives after the decision was used for a release | New evidence revision → new evaluation inside the late window; the consumed decision is untouched and queryable | A3: used decisions are immutable |
| Two workers evaluate the same pair concurrently | Unique constraint; the loser returns the winner's row | One evaluation per evidence × policy |
| Evaluator crashes after computing, before INSERT | Outbox retry recomputes; determinism makes the result identical | Replayability (FR-07-3) |
| Policy revision retired while runs still reference it | Runs keep their pinned revision; only new runs use the active one | Pinning (FR-07-2) |
| CI asks before any evidence revision exists | `pending`; the CLI deadline exits 2, never 0 | Missing evidence never yields PASS |
| Expected count is 0 | `INCONCLUSIVE` unless the suite is declared empty and the policy allows it (00 §8.6) | Empty runs are never green by accident |
| Retry fail → pass on a critical case | Blocks under the pilot default (reason `flaky_critical_retry_pass`); non-critical breaches warn | Flakiness is visible, not laundered |
| Waiver requested and approved by the same person | Refused by the API and by the DB check | Separation of duties (00 §8.8) |
| Waiver expires while a release is open | The release view recomputes with the waiver expired; the stored evaluation shows it expired | Risk acceptance has a deadline |
| Quarantine entry expires mid-run | Evaluated against the policy's quarantine reference at evaluation time | No partial application |
| GitHub API down or App token expired | `ci_delivery` retries with backoff; the CLI path still works | Delivery failure never fabricates a decision |
| Webhook receiver down > 24 h or replaying | Dead-letter set visible and replayable by `event_id`; receivers dedupe and reject > 5 min skew | At-least-once, never silent loss |
| Subset rerun (`rerun_scope = failed_subset`) offered as gate input | Labelled; the release aggregate reports coverage instead of a full regression (00 §8.1) | A subset is never a full pass |
| Shadow healing passes a case that failed officially | Shadow results are excluded from the evaluator; the original decision stands | Healing never overrides evidence |
| Policy JSON carries an unknown field | Write-time rejection (`422`) with a path in `details` (00 §9.2) | Policy semantics never drift silently |

## 6. Security and privacy

- **Decision integrity:** evaluations are written only by the worker's service identity inside an org-scoped transaction; no tenant-facing endpoint writes or mutates an evaluation. Reasons carry ids and counts, never artifact content or secrets (00 §9.2).
- **Policy authority:** `release_approver` owns policy revisions and waivers; `qa_engineer` cannot change release policy (00 §2). Every activation, approval, revocation and quarantine change is audited with actor, scope and hash.
- **CI identity:** delivery uses the GitHub App installation (F3 ✅, repo-scoped); the check run carries the decision and a link, not evidence contents.
- **Webhook secrets:** per-endpoint HMAC secret, never logged; signature over `timestamp + "." + body`; a published reference receiver and a replay/skew conformance fixture.
- **Privacy:** evaluations reference artifact ids but embed no personal data, so evidence deletion (06) does not destroy decision explainability — a deleted artifact reads as `artifact missing (deleted by retention)`, never as present.

## 7. Test strategy

**Decision-table matrix (every row of 00 §8.6 plus the fields it depends on)** — table-driven tests against the pure evaluator with hand-built evidence revisions (no database, no clock), replayed against stored rows to prove determinism.

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

- **Unit:** policy canonicalisation and hashing; each matrix row; reason-code emission; late-window arithmetic; flaky detection from attempts; waiver/quarantine transitions incl. illegal ones.
- **Integration (real PostgreSQL):** concurrent duplicate evaluation writes one row; late evidence produces revision *n+1* while *n* stays byte-identical; `GET /v1/runs/{r}/decision` returns `pending` then the exact revision; release aggregation refuses a component whose digest does not match; `ci_delivery` re-post is idempotent under a GitHub 500.
- **CLI and webhooks:** `evidgate gate` exit codes 0/1/2/3 against a fixture service including the timeout path (→ 2); a receiver fixture verifies the signature, rejects a 6-minute-old timestamp and dedupes a replayed `Event-Id`.
- **Shadow:** deploy-shaped comparison of live vs shadow decisions over a recorded fixture set; the divergence report must be non-empty when a rule changes.

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| A wrong policy revision blocks a customer release | Medium | High | Advisory default, per-project enforcement, `If-Match` + audited activation, shadow comparison before rollout, fast rollback to advisory |
| `INCONCLUSIVE` treated as a pass by a CI script | Medium | High | Distinct exit code 2; check-run conclusions `neutral`/`action_required`, never `success` ✅ ([checks/runs](https://docs.github.com/en/rest/checks/runs)); documentation + fixture test |
| Evaluator drifts to nondeterministic behaviour | Low | High | Pure inputs, evaluator version stored per row, stored-row replay test in CI |
| Flaky budget tuned to hide real failures | Medium | High | Flaky facts are always reasons; critical cases block by default; budget changes are reviewed policy revisions |
| Waiver culture (everything gets waived) | Medium | Medium | Approver ≠ requester, mandatory expiry, waivers displayed beside results and counted in pilot reporting (12) |
| Receivers implement signature checks wrongly | Medium | Medium | Reference receiver + conformance fixture; 5-min skew and replay tests; at-least-once documented |
| GitHub App permission surface grows beyond checks/statuses | Low | Medium | Minimal install permissions (checks/statuses write only), separate from any future healing-PR app (08) |
| Release aggregation mixes runs from different builds | Medium | High | Digest match enforced inside the aggregate; mismatches reported as missing, never averaged (source §5) |
| Evaluation throughput becomes a bottleneck | Low | Medium | Pure in-process function, one row write, pg-boss backpressure; decision-latency SLI p95 < 60 s (00 §11) |

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
| 07.2.1-T1 | Evaluator core + evaluator version constant (pure, no clock/network) | M | V1 | M2 | 05.3.2-T5, 06.5.1-T1 |
| 07.2.1-T2 | Completeness/outcome/decision computation + `reasons[]` catalogue | M | V1 | M2 | 07.2.1-T1 |

#### Story 07.2.2 Persistence, idempotency and the decision matrix — acceptance: a duplicate write returns the existing row, and every row of 00 §8.6 (M1-M16 in §7) is covered by a passing test.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.2.2-T1 | Outbox-driven evaluation task, unique-constraint handling, deterministic replay test | M | V1 | M2 | 07.2.1-T2, 01.5.1-T2, 01.5.1-T3 |
| 07.2.2-T2 | Table-driven matrix tests + stored-row replay test | M | V1 | M2 | 07.2.1-T2 |

### Epic 07.3 Waivers and quarantine

#### Story 07.3.1 Waiver and quarantine lifecycles — acceptance: a waiver approved by its requester is refused; an active quarantine excludes a case from gating while its failure stays visible.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.3.1-T1 | `waiver` table, states, endpoints, `approver ≠ requester` (API + DB check) | M | V1 | M2 | 02.2.1-T1, 02.2.1-T2 |
| 07.3.1-T2 | `quarantine_entry` table, states, endpoints, policy reference | M | V1 | M2 | 07.1.1-T1 |

#### Story 07.3.2 Evaluator effects, expiry and audit — acceptance: M14 and M15 pass; no waiver path produces `PASS` from a failed required assertion; expiry changes the next evaluation.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.3.2-T1 | Waiver/quarantine effects in the evaluator + reason codes | M | V1 | M2 | 07.2.1-T2, 07.3.1-T1, 07.3.1-T2 |
| 07.3.2-T2 | Expiry sweeper (pg-boss), state transitions, audit rows, tests | S | V1 | M2 | 07.3.2-T1 |

### Epic 07.4 Flaky detection and budget

#### Story 07.4.1 In-run flaky detection and budget — acceptance: retry fail → pass is detected from attempts, critical cases block under the pilot default, non-critical breaches warn.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.4.1-T1 | Flaky detection from attempt history within a run (retry policy from the suite revision) | M | V1 | M2 | 07.2.1-T2, 05.3.3-T1 |
| 07.4.1-T2 | Flaky budget from the policy revision applied to the decision | M | V1 | M2 | 07.4.1-T1 |

#### Story 07.4.2 Flaky surface and cross-run trend — acceptance: flaky facts appear in reasons, webhooks and CLI output; the V2 trend label changes no V1 gating semantics.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.4.2-T1 | Flaky reasons in evaluation, webhook payload and CLI output | S | V1 | M2 | 07.4.1-T2 |
| 07.4.2-T2 | Cross-run flaky trend (read table + label) | S | V2 | M4 | 07.4.1-T1, 05.3.3-T1 |

### Epic 07.5 Late evidence, immutability and release decisions

#### Story 07.5.1 Late evidence and immutability — acceptance: in-window late evidence creates evaluation *n+1*; revision *n* remains identical and queryable; no code path mutates a used decision.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.5.1-T1 | Late-window handling + re-evaluation trigger, immutability guard | M | V1 | M2 | 07.2.2-T1, 05.3.2-T3 |
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
| 07.6.1-T1 | GitHub App check-run/commit-status delivery + `ci_delivery` records + retry | M | V1 | M3 | 07.2.2-T1, 02.4.1-T2 |
| 07.6.1-T2 | `evidgate gate` semantics (exit codes, `--wait` deadline, JSON output) with 04 | M | V1 | M3 | 07.5.2-T1, 04.2.1-T1 |

#### Story 07.6.2 Outbound webhooks and delivery operations — acceptance: 00 §9.5 headers and signature pass the reference receiver, retries last 24 h with backoff, and dead letters are replayable by `event_id`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 07.6.2-T1 | Webhook transport (signing, timestamp, at-least-once retry) + `evaluation.created` payload | M | V1 | M2 | 07.2.2-T1, 01.5.1-T4 |
| 07.6.2-T2 | Delivery records, dead-letter visibility, replay tool, receiver conformance fixture | S | V1 | M2 | 07.6.2-T1 |

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
| 07.2 Evaluator | 4.0 | 0 | 0 | 4.0 |
| 07.3 Waivers and quarantine | 3.5 | 0 | 0 | 3.5 |
| 07.4 Flaky detection and budget | 2.5 | 0.5 | 0 | 3.0 |
| 07.5 Late evidence, immutability and release decisions | 3.5 | 0.5 | 0 | 4.0 |
| 07.6 CI integration and webhooks | 3.5 | 0 | 0 | 3.5 |
| 07.7 Rollout: advisory, enforcing and shadow | 2.5 | 0 | 0 | 2.5 |
| **Total** | **22.5** | **1.0** | **0** | **23.5** |

→ with 20 % buffer **28.2** person-days (one senior full-stack TS developer; 00 §6 units).

## 13. Contract change requests

> **Status:** Applied 2026-09-24: 07-CR-1 → 00 §9.3; 07-CR-2 → 00 §9.3 note (`contracts/cli.md`).

| # | Request | Why |
|---|---|---|
| 07-CR-1 | Add `GET /v1/releases/{id}/decision` (permission `decision:read`) to 00 §9.3 next to `GET /v1/runs/{r}/decision`. | A release can hold several components; without a release-level read, the CLI, UI and webhooks would each re-implement aggregation. |
| 07-CR-2 | Record the CLI gate exit codes (0 PASS, 1 FAIL, 2 INCONCLUSIVE, 3 error) in the contracts (00 §9.3 note + `packages/cli` contract, with 04). | CI engineers need one published, testable mapping; the code is a `[DECISION]` here, but implementations are checked against the contract file. |
