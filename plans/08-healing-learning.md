# 08 - Safe Healing and Learning (G1–G4)

> Contracts: `00-overview.md` §3 (glossary), §6 (IDs, sizes), §7 (hashes, naming), §8.7 (healing states), §9.3 (approve/revoke), §9.5 (`healing.candidate_changed`), §11 (NFRs). Facts: `../DECISIONS.md` §5 rules 1-9 (locked), §6 F1 (Playwright ships planner/generator/healer agents), §6 F8 + §7 (PDPL cross-border), §8 O4 (LLM provider/residency) and O6 (false-heal threshold), §9 R3 (healer reuse).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.
> Source: design §4 "Ranh giới safe healing", "Luồng kiểm chứng và áp dụng healing", "Học từ lỗi và đánh giá chất lượng"; §8 "Quyền của AI"; §9 "Nghiệm thu healing theo giai đoạn".
> Legend: ✅ verified with a link opened in this task · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]` (owner).
> **Phases (CR-D-2):** the Milestone column holds the CR-D-2 phase (G1–G4) from [13 §4.4](13-product-phases.md); M0–M5 in this plan's prose refers to the pre-CR-D-2 sequence. Phase totals (raw): G1 13.5 · G2 19.5 · G3 52.25 · G4 7 person-days.

---

## 1. Purpose and scope

**Goal:** repair test *mechanics* (locator, and proposed wait/infra-retry changes) without changing test *intent*, never hiding a product bug, and prove it with evidence a human can review.

**In scope:** incident capture and fingerprinting; triage labels with provenance; tenant-scoped retrieval; bounded candidate generation; AST allowlist validation, semantic checks and negative control; sandbox replay; shadow measurement; the LLM gateway with redaction and tenant opt-in; the Playwright healer as one sandboxed generator; the PR/approval/canary/revoke flow; knowledge records, feedback and lineage; kill switches and degradation; the evaluation set, metrics and phase gates.

**Out of scope:** changing assertions, expected values, API schema/status, unit expectations or snapshot baselines (§5.1); auto-applying anything in V1-V2 (rule 7); hosted runners; source-code coverage; fixing product bugs (the customer fixes them — Evidgate only hands the failure back to the developer); a vector database before measured need.

**Version split.** **V1 (M3):** capture, fingerprint, triage, retrieval and ranker, candidate generation by rules + retrieval, full validator and sandbox replay, **shadow only — no repo change**, quality and triage-time measurement. **V2 (M4):** LLM gateway, redaction, per-tenant opt-in, healer-as-generator, PR/approval/canary/revoke, knowledge and feedback, kill switches, evaluation set and the V2 gate. **V3 (M5):** narrow-context reuse of *approved* mappings only, severity-weighted false-heal threshold (O6), and mobile locator healing once its prerequisites exist.

**Phases (CR-D-2).** Target phases per story ([13 §4.4](13-product-phases.md)), with dependencies pulled earlier where the §9 cells show them: G1 targets `08.5.1`, `08.5.2`, `08.5.3`, `08.5.5` plus the kill switch `08.6.3-T1`; G2 targets `08.1`; G3 targets `08.2`, `08.3`, `08.4`, `08.6`, `08.7`, `08.8`; G4 targets `08.5.4` and `08.9`. A few task rows inside a G2/G3 story sit one phase earlier because something in that earlier phase depends on them (§9 is authoritative). The gateway, prompt safety, structured output and credits ship in G1 because the authoring generation pipeline (`13.3.1`) needs them; `08.5.1-T3`, `08.5.2-T2` and `08.5.5-T4` stay in G3. Element profiles and per-project memory are owned by `13.6`; drift classification (locator drift vs UI change) and its review evidence (before/after screenshots where the project opted in, accessibility / DOM diff) are `13.8` and feed this plan's labels (`08.2.2`) and candidates (`08.3`). Shadow-only operation and no auto-apply (`08.3.3-T1`, DECISIONS §5 rule 7) are unchanged in every phase.

## 2. Requirements

- **FR-08-1** Every failure of a required case creates exactly one `incident` per `(org_id, run_id, execution_key)` linked to run, attempt, `execution_key`, case and case revision; failed retries attach as `incident_attempt` rows on the same incident (T34); the original failure and its evidence are never deleted or overwritten (rule 3).
- **FR-08-2** An incident stores the error text, a **filtered** DOM/accessibility snapshot, the old locator, a trace reference and revision ids, with checksums and retention; credentials, tokens and input values are masked before storage.
- **FR-08-3** Every incident carries a versioned `fingerprint`, stable across dynamic ids, locales and reordering, and the version is stored with the incident.
- **FR-08-4** One current label per incident from `product_defect | test_drift | environment | data_issue | unknown` with `label_source ∈ {rule, model, human}`; only human labels are ground truth; `unknown` never enables automation (00 §8.7).
- **FR-08-5** Retrieval runs in PostgreSQL, tenant/project/engine filtered, returning prior records with labels and `label_source`. Similarity only *ranks*; it never authorises, approves or applies.
- **FR-08-6** At most **3** candidates per incident and **2** validation rounds per candidate; per-project quota (candidates/day, replay minutes) and a wall-clock deadline stop every loop.
- **FR-08-7** The AST allowlist validator accepts changes only in the mechanics node kinds (locator/wait mechanics of Playwright TS) and rejects every DECISIONS §5.2 pattern (`skip`/`fixme`, removed or weakened assertion, `force: true`, catch-and-ignore, business-result mock, disabled network check, unbounded retry, test-id/build/policy/data change); assertion hash before and after must be equal (rule 2).
- **FR-08-8** Validation also checks role/name/test-id identity, unique match, business region and an independent post-condition, plus a negative control that must still fail (rule 4).
- **FR-08-9** Baseline and candidate replay on the same app digest, fixture and environment; non-comparable or non-reproducible cases reach `insufficient_evidence`. V1 writes nothing to any repository, shadow results never replace an official run or decision, and V1 reports candidate quality and triage time per project.
- **FR-08-10** The V2 LLM gateway constrains output by JSON schema, enforces a per-call timeout and a per-tenant/project budget with hard stop, and runs behind a provider adapter; model and prompt versions are recorded.
- **FR-08-11** No context reaches a provider before redaction runs and the tenant's data owner has approved that provider (PDPL Arts. 20-21, DECISIONS §7/F8); default off, approval revocable.
- **FR-08-12** DOM, logs and retrieved records are untrusted data: no tools, shell, production secret, policy write or merge capability; retrieval is tenant-filtered; prompt injection must not produce a forbidden patch.
- **FR-08-13** The Playwright healer agent (F1 ✅) runs only inside the sandbox as one candidate generator; its output passes the same validator; `skip`/`fixme`/removal output is rejected; agent definitions are regenerated when Playwright updates.
- **FR-08-14** A candidate PR carries the diff, the original failure, the context hash, the validation results, the assertion hash and an expiry; approval binds `candidate_hash` + base commit and any context change or rebase re-enters `proposed` (rule 6); sensitive cases (payment, authorization, deletion, security) are proposal-only (rule 5) and the approver must differ from the candidate's requester/editor and from the member whose human label set `test_drift` on the incident ("author", T35); approval never grants Git merge unless the integration is explicitly permitted (00 §9.3).
- **FR-08-15** After merge a canary run detects regression; on regression or a suspected false heal the rule is disabled, the candidate revoked and a revert PR opened, and old release decisions are never rewritten.
- **FR-08-16** An applied candidate writes a tenant/project-scoped knowledge record; feedback labels are `accepted | rejected | reverted | unknown`, a single passing run never creates a ground-truth label, and lineage stores rule/model/prompt version, retrieved record ids, patch hash and validation run ids; revoking a record marks dependent candidates and stops reuse.
- **FR-08-17** Kill switches exist at system, tenant and project level **before** any apply capability ships; settable modes are `enabled | shadow | disabled` and `unavailable` is a derived resolution outcome, never settable (T33); most restrictive wins; if the LLM or knowledge store fails, tests and gates run normally and healing resolves `unavailable` (rule 9).
- **FR-08-18** The evaluation set covers real locator breaks, real product bugs, business UI changes, duplicate names, locales, network errors and data errors, with a holdout split by time or project; metrics are candidate precision, false-heal rate (adjudicated denominator; `unknown` reported separately), missed-bug rate, review rate, rollback rate and cost per accepted fix; `n` samples with 0 failures are reported as ≈ 3/`n` (rule 8).
- **FR-08-19** **V2 gate:** every forbidden case in the evaluation set is blocked. **V3 gate:** a severity-weighted false-heal threshold agreed by the owner (O6) and an independent evaluation. Mobile locator healing stays off (V3) until hierarchy capture, native/webview context and a separate validator allowlist exist.
- **NFR-08-1** Healing never delays ingestion, evaluation or the release gate; a healing outage is invisible to them.
- **NFR-08-2** Shadow validation p95 keeps a run visible in minutes, not hours; every replay stop reason is recorded.
- **NFR-08-3** Healing cost per project is capped by quota and budget; no unbounded provider or replay spend.
- **NFR-08-4** Every healing row is `org_id`-scoped with composite FKs and RLS (00 §5.4); every state change is audited with actor, reason and hashes.

## 3. Key decisions

| # | Decision | Rationale / alternatives rejected |
|---|---|---|
| D1 | **V1 is rules + fingerprint + PostgreSQL retrieval (same store, same RLS), shadow only** `[DECISION]` | Cheapest way to collect labelled evidence for the V2 gate (source §4 "Công nghệ cho học và healing"); no new component (A6, A7). Rejected: LLM from day one (no data, no consent), a vector DB or embeddings as primary retriever (no measured need). |
| D2 | **Playwright healer = one generator inside the sandbox**, output through our validator (R3) `[DECISION]` | Reuse the engine (source principle) without inheriting its "skipped test" outcome. Rejected: adopting healer output as the patch; forking the healer. |
| D3 | **Apply goes through the customer's Git flow with a canary; approval binds `candidate_hash` + base commit, never a diff text; runtime auto-apply off in V1-V2** `[DECISION]` | Git holds approved test code (A2), rollback is a revert PR, and a rebase or context drift must invalidate approval (rule 6). Rejected: approving a branch or "the fix" by description. |
| D4 | **LLM output is a candidate; its confidence is never a safety probability** `[DECISION]` | Rule 8; safety comes from independent checks, not from the model. |
| D5 | **Provider use is per-tenant opt-in, gated on data-owner approval and redaction** `[DECISION]` | PDPL Art. 20.1 (F8 ✅): sending artifact content abroad is a transfer. |
| D6 | **Kill switches before apply; healing degrades to `unavailable` without touching tests or gates** `[DECISION]` | Rule 9; a broken healing path must never force disabling ingest. |
| D7 | **Metrics use an adjudicated denominator; V3 reuse is narrow-context only** `[DECISION]` | A green shadow run proves nothing about bug detection; keep proposing while samples are thin. |

## 4. Design

### 4.1 Tables owned by the `healing` module (high level)

| Table | Key columns | Rules |
|---|---|---|
| `incident` | `org_id`, `project_id`, `run_id`, `attempt_id`, `case_id`, `case_revision_id`, `execution_key`, `fingerprint`, `fingerprint_version`, `error_text`, `dom_artifact_id`, `a11y_artifact_id`, `old_locator`, `trace_artifact_id`, `content_hash`, `redaction_report_hash`, `context_state`, `state` | unique `(org_id, run_id, execution_key)` (T34); created at evidence freeze with `context_state = pending`, a task settles `complete | partial` at the collection deadline + late window, and generation may use only context artifacts that are `ready` (T31); context kinds `dom_snapshot`/`a11y_snapshot`/`trace` exist in 00 §7 (T20); masked before insert; state per 00 §8.7 |
| `incident_attempt` | `org_id`, `incident_id`, `attempt_id`, `case_attempt` refs, `result` | one row per failed attempt; retries of the same `(org_id, run_id, execution_key)` attach here instead of creating a second incident (T34); composite FK `(org_id, incident_id)` → `incident (org_id, id)`; RLS (NFR-08-4, 00 §5.4.1) |
| `incident_label` | `org_id`, `incident_id`, `label`, `label_source`, `evidence_refs`, `created_by`, `superseded_by` | one current row per incident; human supersedes model/rule; composite FK `(org_id, incident_id)` → `incident (org_id, id)`; RLS (NFR-08-4, 00 §5.4.1) |
| `healing_candidate` | `org_id`, `incident_id`, `candidate_hash`, `base_commit`, `context_hash`, `strategy`, `patch_artifact_id` (artifact kind `patch`, T20), `requested_by`, `edited_by`, `state`, `expires_at`, `rule_version`, `model_version`, `prompt_version` | ≤ 3 live per incident; `requested_by`/`edited_by` define the "author" excluded from approving sensitive cases (T35); states per 00 §8.7 |
| `candidate_validation` | `org_id`, `candidate_id`, `round_no` (≤ 2), `sandbox_run_id`, `app_digest`, `fixture_hash`, `env_hash`, `assertion_hash_before`, `assertion_hash_after`, `checks` (jsonb), `verdict`, `reason_code` | append-only; replays never overwrite; composite FK `(org_id, candidate_id)` → `healing_candidate (org_id, id)`; RLS (NFR-08-4, 00 §5.4.1) |
| `healing_rule` | `org_id`, `project_id`, `fingerprint_pattern`, `mapping`, `state (active | disabled)`, `disabled_reason` | rule disable is the fastest rollback; composite FK `(org_id, project_id)` → `project (org_id, id)`; RLS (NFR-08-4, 00 §5.4.1) |
| `knowledge_record` | `org_id`, `project_id`, `engine`, `test_revision_id`, `fingerprint`, `old_locator`, `new_locator`, `context_hash`, `label`, `label_source`, `state (active | revoked)`, `source_candidate_id` | tenant-scoped; cross-tenant reuse needs written consent |
| `healing_feedback` | `org_id`, `candidate_id`, `label (accepted | rejected | reverted | unknown)`, `at`, `by` | append-only; never rewritten; composite FK `(org_id, candidate_id)` → `healing_candidate (org_id, id)`; RLS (NFR-08-4, 00 §5.4.1) |
| `healing_optin` | `org_id`, `provider`, `approved_by`, `approved_at`, `scope`, `revoked_at` | absent row = off |
| `healing_llm_call` | `org_id`, `project_id`, `incident_id`, `candidate_id`, `purpose`, `model`, `prompt_version`, `tokens`, `cost`, `status`, `redaction_report_hash` | `project_id`/`incident_id`/`candidate_id` make spend attributable per project and per candidate (T30); `purpose` includes `authoring_generation`, where `incident_id`/`candidate_id` are null (13.3.1-T2); cost rows feed plan 12 |
| `healing_switch` | `scope (system | tenant | project)`, `org_id`, `project_id`, `mode (enabled | shadow | disabled)`, `reason`, `set_by`, `at` | settable modes are `enabled | shadow | disabled`; `unavailable` is derived at resolution time from component health, never persisted (T33); resolved system → tenant → project |
| `healing_quota` | `org_id`, `project_id`, `candidates_per_day`, `replay_minutes_per_day`, `deadline_minutes`, counters | consumed atomically inside the candidate-mint transaction: `UPDATE healing_quota SET counters = counters + 1 WHERE … AND counters < cap RETURNING` — zero rows returned stops generation (T32); composite FK `(org_id, project_id)` → `project (org_id, id)`; RLS (NFR-08-4, 00 §5.4.1) |

Retention: incident context follows artifact retention (plan 06); tenant deletion covers knowledge records and healing rows.

### 4.2 Endpoints and permissions

Used from 00 §9.3: `POST /v1/healing/candidates/{c}/approve`, `POST /v1/healing/candidates/{c}/revoke`. Owned (shapes into `contracts/`; see §13): `GET /v1/projects/{p}/incidents`, `GET /v1/incidents/{i}`, `POST /v1/incidents/{i}/label`, `GET /v1/healing/candidates/{c}`, `POST /v1/healing/candidates/{c}/reject`, `GET|PUT /v1/healing/switch`. Emits `healing.candidate_changed` (00 §9.5). Permissions: `healing:triage`, `healing:approve`, `healing:revoke`, `healing:configure`.

### 4.3 Flow

1. **Capture** — a failure in an evidence revision creates an incident + filtered context (08.1). 2. **Triage** — rule triage proposes a label, a human may override (08.2). 3. **Retrieve** — same-project records ordered by fingerprint and similarity; the ranker only orders (08.2). 4. **Generate** — ≤ 3 candidates, using only context artifacts that are `ready` (T31) (V1 rules + retrieval; V2 LLM gateway and Playwright healer, both sandboxed) (08.3, 08.5). 5. **Validate** — AST allowlist, forbidden patterns, assertion hash equality, semantic checks, negative control, then sandbox replay on the same digest/fixture/env, ≤ 2 rounds (08.4). 6. **Propose/apply (V2)** — PR → approval bound to `candidate_hash` + base commit → the customer's Git merge (08.6); the approve route ships behind a feature flag defaulting off until the kill switch is live (T29). 7. **Learn** — knowledge record, feedback label, lineage, revocation cascade (08.7). 8. **Measure and gate** (08.8); then narrow-context reuse and mobile (08.9).

**Where learning sits (CR-D-2).** Between steps 4 and 5: a green run records an element profile per bound step (`13.6`); inside the 08.2.2 label `test_drift`, profile rules decide *locator drift* vs *UI change* (`13.8.1`). Only the locator-drift outcome reaches candidate generation (08.3) and then the validator (08.4); a UI change becomes a test-case review proposal instead. Review evidence (`13.8.2`) adds before/after screenshots where the project opted in and an accessibility / DOM diff to the 08.6.1 fields. The per-project shadow window (`13.8.1-T4`) runs before any repair PR is offered, and nothing is auto-applied.

### 4.4 States

Exactly per 00 §8.7: incident `open → triaged(label) → closed`; incident context `pending → complete | partial`, settled at the collection deadline + late window (T31); candidate `proposed → validated → approved → applied` with exits `rejected`, `insufficient_evidence`, `expired`, `revoked`; knowledge record `active → revoked`; availability resolves to `enabled | shadow | disabled | unavailable` where `unavailable` is derived, never set (T33). Shadow results live on the incident and never on the run's official evaluation ("candidate passes only in shadow" → original decision kept).

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Candidate has an equal assertion hash but points at a different business object (source example: "Pay" disappeared, candidate finds "Save order") | Semantic checks and the negative control reject it; the failure goes back to the developer | No mechanics change may alter intent (rule 1) |
| Assertion hash equal but the assertion is weak or trivially true | Hash equality is explicitly necessary, not sufficient; independent post-condition required | Rule 4 |
| Two elements share the same accessible name | Unique-match check fails → candidate rejected, incident returns to triage | No silent ambiguity |
| Legitimate locale/data change, or a real product bug, looks like drift | Fingerprint and replay separate them: the first is labelled `data_issue`/`environment`; the second fails the negative control and is labelled `product_defect` | Bucket discipline; never hide a bug |
| Baseline not reproducible (flaky), or quota/deadline reached mid-loop | `insufficient_evidence` after ≤ 2 rounds with an explicit stop reason; no further spend | Bounded loops, bounded spend |
| LLM timeout/429/invalid JSON, or the knowledge store is down | Fails closed: no candidate, retry cap, budget stop, `unavailable` when down; ingest and evaluation continue | Tests and gates unaffected (NFR-08-1) |
| Prompt injection in DOM, log or retrieved record | Untrusted-input handling; no tools/secrets; the validator rejects any forbidden patch | FR-08-12 |
| Context change/rebase after approval, or a candidate expires before review | `approved` → `proposed` and re-validate; the expiry task moves it to `expired` and releases quota | Rule 6; no zombie candidates |
| Approver is the candidate's requester/editor, or the member whose human label set `test_drift`, on a sensitive case | Approval blocked (permission + approver ≠ author set, T35) | 00 §2 role limits |
| Canary regression or suspected false heal after merge | Disable rule, revoke candidate, revert PR; old release decisions unchanged | Rollback in one step |

## 6. Security and privacy

- **Untrusted input:** DOM, logs, traces and retrieved records are attacker-influenceable; they are passed as delimited data, never as instructions, and the model has no tools, shell, filesystem, network, secret, policy or merge capability (source §8 "Quyền của AI").
- **Redaction and tenant isolation:** secrets, tokens, credentials and personal-data patterns are removed at source with a redaction report hash per call (a detected leak blocks the call and alerts); retrieval and prompting are tenant-filtered, cross-tenant records need explicit written consent, and RLS + composite FKs apply everywhere (00 §5.4).
- **PDPL (F8 ✅):** sending artifact content to a provider outside Vietnam is a cross-border transfer (Art. 20.1) with a 60-day impact assessment; per-tenant opt-in with a data-owner approval record is required before the first call (O4); tenant deletion covers healing data (plan 11 owns the filings).
- **Sandbox, audit and authorisation:** one-shot identity, network allowlist, no tenant secrets or production credentials (a container alone is not isolation, plan 11); actor, action, object, hashes and `request_id` on every state change; `test_maintainer` approves and agent identities can never approve or revoke.

## 7. Test strategy

- **Unit:** fingerprint normalisation (dynamic ids, locales, reordering); rule triage table; candidate-hash canonicalisation (RFC 8785); allowlist accept/reject per node kind; forbidden-pattern detector per §5.2 item; quota/deadline arithmetic. Server code uses xUnit v3 + Shouldly; `packages/healing-validator` uses Vitest (T36).
- **Contract/integration (Testcontainers.PostgreSQL):** incident lifecycle and label precedence; retry → `incident_attempt` attach (T34); context settle `pending → complete | partial` (T31); retrieval tenant filter; candidate state machine and exits; approval with a stale `If-Match` → 412; revoke blocks reuse; kill-switch resolution system → tenant → project with derived `unavailable` (T33).
- **Named rejection tests (required before the V2 gate, built in 08.8):** `reject-pay-button-save-order` (the source example — the "Pay" button disappears because of a bug and the candidate finds "Save order"; it must be rejected because the business action changed); `reject-assertion-weakened`; `reject-skip-injected` (healer output containing `skip`/`fixme`); `reject-force-click`; `reject-stale-context`; `reject-duplicate-name`; `reject-cross-tenant-record`.
- **Sandbox and negative control:** baseline fails → candidate passes → seeded broken state still fails; non-comparable digest/fixture/env rejected; non-reproducible → `insufficient_evidence`.
- **Evaluation set (08.8):** real locator breaks, real product bugs, business UI changes, duplicate names, locales, network errors, data errors; holdout split by time or project so one incident never appears in retrieval and holdout at once.
- **Fixtures:** golden incident bundles (error + filtered DOM + a11y snapshot + old locator + trace pointer) per strategy (role, label, test-id, text) and a Playwright-TS mini-suite for replay; Evidgate's own CI results (R10) are one source of real locator breaks.
- **Degradation:** with the gateway disabled or the knowledge store stopped, ingest → evaluate still passes and healing reports `unavailable`.

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| False heal hides a real bug | medium | high | Rule 4/5 checks, negative control, shadow-first, canary, revert PR, kill switch; tracked as false-heal rate |
| Evaluation samples too few or correlated | high | medium | Holdout by time/project, adjudicated labels, rule-of-three reporting, V3 gate stays closed (O6) |
| LLM cost or latency blows the pilot budget | medium | medium | Budget with hard stop, ≤ 3 candidates, ≤ 2 rounds, project quota, cost-per-accepted-fix metric |
| Data-owner approval not obtained | medium | high | Default off; opt-in row is part of the V2 PR gate; plan 11 owns the transfer assessment (O4) |
| Prompt injection yields a forbidden patch | medium | high | Untrusted-input handling + independent validator; injection corpus in the evaluation set |
| Playwright healer output contract changes | medium | medium | Regenerate agent definitions on every Playwright bump, gated on the compatibility suite (F1) |
| Retrieval quality too low to beat rules | medium | low | Ranker is advisory in V1; measure precision before investing further |
| Reviewer fatigue → rubber-stamping | medium | high | Review rate is tracked; sensitive cases need a second person; expiry forces a decision |

## 9. Epics, stories and tasks
### Epic 08.1 Incident capture and fingerprinting (V1, M3)
#### Story 08.1.1 Incident record from verified failures — acceptance: each failure of a required case creates one incident per `(org_id, run_id, execution_key)` linked to run, attempt, `execution_key`, case and case revision, with failed retries attached as `incident_attempt` rows (T34); states follow `open → triaged(label) → closed`; the original failure is never modified.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.1.1-T1 | `incident` + `incident_attempt` models, states, RLS, audit, creation from the evidence revision (unique `(org_id, run_id, execution_key)`, retries attach, `context_state = pending` at freeze, T31/T34); runs with `purpose = replay` never create incidents (T67) | L | V1 | G2 | 05.3.2-T5, 05.2.1-T5 |
| 08.1.1-T2 | Incident API + `healing:triage` permission + list contract for the UI | S | V1 | G2 | 08.1.1-T1 |
#### Story 08.1.2 Filtered context capture and versioned fingerprint — acceptance: error text, filtered DOM/accessibility snapshot, old locator, trace reference and revision ids are stored with checksums and retention, secrets and input values are masked, and the same break yields the same fingerprint across runs.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.1.2-T1 | Context capture: error, filtered DOM/a11y snapshot, old locator, trace pointer, revisions | L | V1 | G2 | 08.1.1-T1, 06.1.1-T1 |
| 08.1.2-T2 | Masking (credentials/inputs), size caps, retention hooks | M | V1 | G2 | 08.1.2-T1 |
| 08.1.2-T3 | Fingerprint spec v1 (versioned) + computation + backfill job | L | V1 | G2 | 08.1.2-T1 |
| 08.1.2-T4 | Fingerprint stability tests (dynamic ids, locales, reorder) | M | V1 | G2 | 08.1.2-T3 |
| 08.1.2-T5 | Incident-context settle task: Cronos schedule (unique fire key, 00 §5.3) at collection deadline + late window → `complete \| partial`; generation gated on `ready` context (T31) | S | V1 | G2 | 08.1.2-T1 |
### Epic 08.2 Retrieval, ranker and triage labels (V1, M3)
#### Story 08.2.1 Tenant-scoped retrieval and ranker — acceptance: retrieval is filtered by tenant/project/engine and returns prior records with label and `label_source`; ranking orders by prior acceptance and similarity and never authorises, approves or applies.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.2.1-T1 | Retrieval query and indexes (org/project/engine/fingerprint) | M | V1 | G2 | 08.1.2-T3 |
| 08.2.1-T2 | Retrieval result contract (labels + `label_source`, advisory only) | S | V1 | G2 | 08.2.1-T1 |
| 08.2.1-T3 | Ranker v0 + explanation output + "similarity never grants permission" test | M | V1 | G2 | 08.2.1-T2 |
#### Story 08.2.2 Triage labels with provenance — acceptance: one current label per incident from the five labels with `label_source ∈ {rule, model, human}`; human overrides model/rule; only human labels are ground truth; `unknown` never enables automation; triage time is measured.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.2.2-T1 | Label taxonomy, `label_source` precedence, storage | S | V1 | G3 | 08.1.1-T2 |
| 08.2.2-T2 | Rule triage (product_defect/test_drift/environment/data_issue/unknown) with evidence refs | L | V1 | G3 | 08.2.2-T1 |
| 08.2.2-T3 | Human triage action, audit, ground-truth rule, `unknown` guard, triage-time metric | M | V1 | G3 | 08.2.2-T2 |
| 08.2.2-T4 | Assignee on the label write (`assignee_membership_id`) and `POST /v1/incidents/{i}/close` with reason + `If-Match` (T66) | M | V1 | G3 | 08.2.2-T3 |

#### Story 08.2.3 External issue reference on an incident — acceptance: an incident can carry a link to the team's own tracker so triage results are traceable there.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.2.3-T1 | `incident.external_issue_url` (https only, length cap, audited PATCH, never fetched by the server) | S | V1 | G3 | 08.2.2-T3 |
| 08.2.3-T2 | Show and edit the link in the failure detail and carry it in the incident webhook payload | S | V1 | G3 | 08.2.3-T1, 09.4.1-T3 |

**Story 08.2.3 total: 1** — `[INFERENCE]` sizes; added from the missing-features review of 2026-10-02.

### Epic 08.3 Candidate generation, bounded loop and shadow measurement (V1, M3)
#### Story 08.3.1 Candidate model and rule-based generation — acceptance: at most 3 candidates per incident, each with `candidate_hash`, base commit, old/new locator, context hash and expiry; states follow 00 §8.7.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.3.1-T1 | Candidate model, states, `candidate_hash` canonicalisation (00 §7), rule-based locator generator capped at 3 | XL | V1 | G2 | 08.1.1-T1, 08.2.1-T3, 08.6.3-T1 |
#### Story 08.3.2 Bounded validation loop — acceptance: ≤ 2 rounds per candidate; project quota and deadline stop every loop; non-reproducible candidates reach `insufficient_evidence`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.3.2-T1 | Round cap + attempt ledger | M | V1 | G3 | 08.3.1-T1 |
| 08.3.2-T2 | Quota (candidates/day, replay minutes) consumed atomically in the candidate-mint transaction (`UPDATE … SET counters = counters + 1 WHERE counters < cap RETURNING`, T32) + deadline + expiry/quota-release Cronos schedule (unique fire key, 00 §5.3) | M | V1 | G3 | 08.3.2-T1 |
| 08.3.2-T3 | `insufficient_evidence` rules and transition | M | V1 | G3 | 08.3.2-T1 |
#### Story 08.3.3 Shadow-only operation and measurement — acceptance: no V1 code path writes to a repository; shadow results never replace an official run or decision; candidate quality and triage time are reported per project.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.3.3-T1 | Shadow-only guard + "no repo write path" invariant test | S | V1 | G3 | 08.3.1-T1, 08.6.3-T1 |
| 08.3.3-T2 | Shadow result storage; official evaluation untouched | M | V1 | G3 | 08.3.3-T1, 07.2.2-T1 |
| 08.3.3-T3 | Shadow quality + triage-time reporting | L | V1 | G3 | 08.3.3-T2, 12.5.1-T1 |
### Epic 08.4 Validation: AST allowlist, semantics, negative control, sandbox (V1, M3)
#### Story 08.4.1 AST allowlist validator for Playwright TS — acceptance: only mechanics node kinds may change; every DECISIONS §5.2 pattern is detected and rejected with a reason code; new imports, helpers or config edits are rejected.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.4.1-T1 | `packages/healing-validator` (Node 26 + TypeScript compiler API, TS 6.0.x): allowlist spec (which node kinds may change) + AST parser/writer + patch-scope check, JSON stdin/stdout protocol (T36) | XL | V1 | G2 | 08.3.1-T1 |
| 08.4.1-T2 | Forbidden-pattern detectors in `packages/healing-validator` (assertion removal/weakening, `skip`/`fixme`, `force: true`, catch-and-ignore, business mock, network-check disable, unbounded retry, test-id/build/policy/data change) | L | V1 | G2 | 08.4.1-T1 |
| 08.4.1-T3 | `Evidgate.Worker` host for the validator: spawn `packages/healing-validator` as a child process over JSON stdin/stdout with CPU, memory and wall-time limits; worker image carries a pinned Node runtime; the API process never runs it (T36) | L | V1 | G3 | 08.4.1-T1 |
#### Story 08.4.2 Assertion hash, semantic checks and negative control — acceptance: assertion hash before/after is equal and treated as necessary but not sufficient; identity, unique match, business region and an independent post-condition are checked; a seeded broken state still fails.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.4.2-T1 | Assertion hash computation + equality gate + semantic checks (role/name/test-id identity, unique match, business region, independent post-condition) | XL | V1 | G3 | 08.4.1-T1, 08.1.2-T1 |
| 08.4.2-T2 | Negative-control runner, seeded broken states, fixtures incl. `reject-pay-button-save-order` | L | V1 | G3 | 08.4.2-T1 |
#### Story 08.4.3 Sandbox replay — acceptance: baseline and candidate run on the same app digest, fixture and environment; non-comparable runs are rejected; the replay host chosen in O10 meets the 11.2.1-T6 requirements (one-shot, network allowlist, no tenant secrets).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.4.3-T1 | Replay job spec (app digest, fixture, env, engine pin) + comparability gate | L | V1 | G3 | 08.3.2-T1, 05.2.1-T4, 04.9.1-T1 |
| 08.4.3-T2 | One-shot isolation, replay caps + metering, Playwright version pin | M | V1 | G3 | 08.4.3-T1, 11.2.1-T6 |
| 08.4.3-T3 | Validation report + rejection taxonomy; incident returns to triage | S | V1 | G3 | 08.4.3-T2 |
### Epic 08.5 LLM gateway, privacy and healer generator (G1–G4)

**Phase placement (CR-D-2).** The gateway, prompt safety, structured output and credits are **G1**: `13.3.1` sends requirement text, documents and images through this gateway to produce checklists and then test cases, so the adapter, JSON-schema output, budget hard stop and metering (`08.5.1-T1`), untrusted-context handling (`08.5.2-T1`), redaction with leak tests and per-tenant opt-in (`08.5.3-T1`..`T3`) and credits with key custody (`08.5.5-T1`..`T3`, `T5`) are built before any authoring screen goes live. `08.5.1-T1` therefore depends on `12.3.1-T1` only: its earlier dependency on `08.4.3-T3` assumed sandbox candidate validation that G1 authoring never runs, and `12.3.1-T2` (usage-ledger aggregation) is not needed by the gateway (it is G1 anyway because the pilot measurement `12.2.1-T5` needs it). The rule/model/prompt registry with model triage (`08.5.1-T3`), the injection corpus and prompt-level tenant filter (`08.5.2-T2`) and the provider evaluation harness (`08.5.5-T4`) stay in G3; the Playwright healer (`08.5.4`) stays in G4. Provider egress still needs the tenant's data-owner approval (`08.5.3-T3`) and the plan 11 transfer assessment (`11.8.1-T5`) — in G1 those are a precondition of the first real generation call, not a follow-up to it.

#### Story 08.5.1 LLM gateway — acceptance: provider adapter interface, JSON-schema-constrained output, per-call timeout, per-tenant/project budget with hard stop, model/prompt versions recorded; model triage is a hypothesis (`label_source = model`).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.5.1-T1 | Gateway: typed `HttpClient` provider adapter with resilience (timeout, bounded retry) + JSON-schema output + refuses any request without a redaction report hash from 08.5.3-T1 (redaction itself is 08.5.3-T1) + per-tenant/project budget with hard stop + `healing_llm_call` metering incl. `project_id`/`purpose`/`incident_id`/`candidate_id` (T30; an `authoring_generation` call carries no incident or candidate) | XL | V2 | G1 | 12.3.1-T1 |
| 08.5.1-T3 | Rule/model/prompt version registry + model triage as hypothesis | M | V2 | G3 | 08.5.1-T1, 08.2.2-T1 |
#### Story 08.5.2 Untrusted context, injection containment and tenant filter — acceptance: DOM, logs and retrieved records are delimited data with no tool, shell, secret, policy or merge capability; an injection corpus cannot produce a forbidden patch; retrieval is tenant-filtered.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.5.2-T1 | Untrusted-input handling + capability removal (no tools/shell/secrets/merge) | M | V2 | G1 | 08.5.1-T1 |
| 08.5.2-T2 | Injection corpus tests + tenant filter on retrieval (cross-tenant record never reaches a prompt) | L | V2 | G3 | 08.5.2-T1, 08.2.1-T1 |
#### Story 08.5.3 Redaction and per-tenant opt-in — acceptance: no context leaves the tenant boundary before redaction runs and the data owner has approved that provider; default off; a detected leak blocks the call, is audited, and the cross-border evidence record is written.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.5.3-T1 | Redaction pipeline before the provider + redaction report hash | L | V2 | G1 | 08.5.1-T1 |
| 08.5.3-T2 | Leak/negative tests; call blocked and audited on leak | M | V2 | G1 | 08.5.3-T1 |
| 08.5.3-T3 | Per-tenant opt-in + data-owner approval record + revocation + cross-border evidence for plan 11 (two-person opt-in with reject/withdraw/expiry exits, T64; TL review M→L) | L | V2 | G1 | 08.5.3-T1 |
#### Story 08.5.4 Playwright healer as one sandboxed generator — acceptance: the healer (F1 ✅) runs only in the sandbox; its output passes the same validator; `skip`/`fixme` output is rejected; agent definitions are regenerated on Playwright updates.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.5.4-T1 | Healer run in the sandbox + output mapping + `skip`/`fixme` rejection | M | V2 | G4 | 08.4.3-T2, 08.4.1-T2 |
| 08.5.4-T2 | Agent-definition regeneration on Playwright bumps + compatibility gate | M | V2 | G4 | 08.5.4-T1 |

#### Story 08.5.5 AI credits and spend control — acceptance: a tenant can call the model only while it has credit and has opted in; every call consumes credit exactly once under an idempotent key; the provider key is held only by Evidgate and never reaches a tenant, log or artifact; a per-tenant cap, a global daily cap and the kill switch each stop model calls within one request. [CR from owner decision O4, 2026-10-02]
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.5.5-T1 | `ai_credit_account` (org_id, balance, period cap) + append-only `ai_credit_entry` (grant, consume, refund, expire) with RLS, composite FKs and audit; each entry also writes a usage-ledger row, metric `ai_credit` (12.3.1) | M | V2 | G1 | 08.5.1-T1, 12.3.1-T1 |
| 08.5.5-T2 | Gateway consumption path: reserve → provider call → settle, billing key `ai:<call_id>`; insufficient credit returns `ai_credit_exhausted` with no provider call; failed calls are refunded once | M | V2 | G1 | 08.5.5-T1 |
| 08.5.5-T3 | Provider key custody and spend control: Evidgate-held key in the secret store (10.1.1-T2) with rotation (10.7.1-T4), global daily cap + per-tenant cap, alert at 80 %, kill switch (08.6.3-T1) halts all model calls | M | V2 | G1 | 08.5.5-T2, 08.6.3-T1, 10.7.1-T4 |
| 08.5.5-T4 | Provider evaluation harness on the locator-break corpus (accuracy, cost per accepted candidate, refusal, latency) + decision memo covering the no-retention term and region | L | V2 | G3 | 08.5.1-T1, 08.8.1-T1 |
| 08.5.5-T5 | Credit administration: operator grant/adjust (two-person above a threshold, audited) and tenant read of balance and usage | S | V2 | G1 | 08.5.5-T1 |

**Story 08.5.5 total: 5.5**
### Epic 08.6 Apply: PR, approval, canary, revoke, kill switches (V2, M4)
#### Story 08.6.1 Validation PR and approval binding — acceptance: the PR carries the diff, original failure, context hash, validation results, assertion hash and expiry; approval binds `candidate_hash` + base commit via `If-Match`; sensitive cases are proposal-only and the approver must differ from the candidate's requester/editor and from the member whose human label set `test_drift` (T35); approval never grants Git merge unless the integration is explicitly permitted.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.6.1-T1 | PR builder with the required fields and expiry + approve endpoint bound to `candidate_hash` + base commit (`If-Match`, 412 on stale); approve route behind a feature flag defaulting off (T29) | XL | V2 | G3 | 08.4.3-T3, 03.4.2-T1, 08.6.3-T1 |
| 08.6.1-T2 | Sensitive-case rules: approver ≠ `requested_by`/`edited_by` and ≠ the member whose human label set `test_drift` (T35); permissions (`healing:approve`/`healing:revoke`), no-merge guarantee, approval audit | L | V2 | G3 | 08.6.1-T1 |
| 08.6.1-T3 | Context/rebase invalidation + expiry Cronos schedule (unique fire key, 00 §5.3) + notification | M | V2 | G3 | 08.6.1-T1 |
#### Story 08.6.2 Post-merge canary and rollback — acceptance: a canary run on the selected suite detects regression; on regression or a suspected false heal the rule is disabled, the candidate revoked and a revert PR opened, and old release decisions are untouched.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.6.2-T1 | Canary run on the selected suite + regression detection | L | V2 | G3 | 08.6.1-T2 |
| 08.6.2-T2 | Rule disable + revoke (reason required) + revert PR draft | M | V2 | G3 | 08.6.2-T1 |
#### Story 08.6.3 Kill switches and degradation — acceptance: system/tenant/project switches persist `enabled | shadow | disabled` and resolve to `enabled | shadow | disabled | unavailable` (most restrictive wins; `unavailable` is derived, never set, T33); they exist and take effect before any apply capability ships; with the LLM or knowledge store down, tests and gates run normally and healing resolves `unavailable`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.6.3-T1 | Switch model (`enabled \| shadow \| disabled` settable, `unavailable` derived, T33), resolution order, immediate effect on in-flight work — ships before any apply capability (T29) — V1/M3 so tenants can switch shadow healing off from the pilot on (00 §9.3, DECISIONS §5.9; TL review) | M | V1 | G1 | — |
| 08.6.3-T2 | Degradation path: LLM/knowledge store failure resolves `unavailable` (T33) + alerting | M | V2 | G3 | 08.6.3-T1, 08.5.1-T1 |
| 08.6.3-T3 | Kill-switch and degradation drill + runbook entry | M | V2 | G3 | 08.6.3-T2, 10.7.1-T5 |
### Epic 08.7 Knowledge, feedback and lineage (V2, M4)
#### Story 08.7.1 Knowledge records, ranking and feedback — acceptance: an applied candidate writes one tenant/project-scoped record; feedback labels are `accepted | rejected | reverted | unknown`; a single passing run never creates a ground-truth label; the ranker may weight records but never authorises.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.7.1-T1 | Knowledge record table + write path on `applied` | M | V2 | G3 | 08.6.1-T2 |
| 08.7.1-T2 | Feedback label model + rules + post-merge `reverted` detection | L | V2 | G3 | 08.6.2-T2 |
| 08.7.1-T3 | Ranker feedback weighting (ranking only) | XS | V2 | G3 | 08.7.1-T1, 08.2.1-T3 |
#### Story 08.7.2 Lineage and revocation cascade — acceptance: each candidate/knowledge row stores rule/model/prompt version, retrieved record ids, patch hash and validation run ids; revoking a record marks dependent candidates and stops reuse; tenant deletion covers healing knowledge.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.7.2-T1 | Lineage fields + query endpoint | M | V2 | G3 | 08.5.1-T3, 08.7.1-T1 |
| 08.7.2-T2 | Revocation cascade + tests + retention/tenant-deletion coverage | M | V2 | G3 | 08.7.2-T1 |
### Epic 08.8 Evaluation, metrics and phase gates (V2 M4, V3 M5)
#### Story 08.8.1 Evaluation set and holdout — acceptance: the corpus covers real locator breaks, real product bugs, business UI changes, duplicate names, locales, network errors and data errors; the holdout is split by time or project and adjudicated independently; the named rejection tests are included.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.8.1-T1 | Corpus construction for the seven categories; labelling starts from shadow candidates at M3 so the M4 entry gate (00 §4) can be checked | L | V1 | G3 | 08.4.3-T3 |
| 08.8.1-T2 | Named rejection tests (`reject-pay-button-save-order`, assertion weakening, skip injection, force click, stale context, duplicate name, cross-tenant record) | M | V2 | G3 | 08.8.1-T1, 08.4.2-T2 |
| 08.8.1-T3 | Holdout split (time/project) + adjudication + independent reviewer | M | V2 | G3 | 08.8.1-T1 |
#### Story 08.8.2 Metrics and reporting — acceptance: candidate precision, false-heal rate (adjudicated denominator, `unknown` separate), missed-bug rate, review rate, rollback rate and cost per accepted fix are reported; 0 failures in `n` samples is reported as ≈ 3/`n`, never as LLM confidence.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.8.2-T1 | Metric definitions + pipeline incl. severity weighting | L | V2 | G3 | 08.8.1-T3, 08.7.1-T2 |
| 08.8.2-T2 | Rule-of-three reporting and caveats in the report template | M | V2 | G3 | 08.8.2-T1 |
#### Story 08.8.3 Phase gates — acceptance: the V2 gate requires every forbidden case blocked, reproducible validation, hash-bound review and a successful rollback drill; the V3 gate requires a severity-weighted false-heal threshold agreed by the owner (O6) and an independent evaluation.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.8.3-T1 | V2 gate report (forbidden cases, reproducibility, hash-bound review, rollback drill) | S | V2 | G3 | 08.8.2-T2, 08.6.2-T2 |
| 08.8.3-T2 | V3 gate: severity-weighted false-heal threshold (O6) + independent evaluation | M | V3 | G3 | 08.8.3-T1 |
### Epic 08.9 V3 narrow reuse and mobile healing (V3, M5)
#### Story 08.9.1 Narrow-context reuse of approved mappings — acceptance: only approved mappings are reused, only when context hash, engine and test-revision lineage match; reuse is gated by the kill switch and a canary, is auto-revoked on regression and never crosses tenants.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.9.1-T1 | Reuse matcher + policy for approved mappings in a narrow context | L | V3 | G4 | 08.8.3-T2 |
| 08.9.1-T2 | Reuse guards: kill switch, canary, auto-revoke, no cross-tenant reuse | M | V3 | G4 | 08.9.1-T1 |
#### Story 08.9.2 Mobile locator healing — acceptance: mobile healing stays off until hierarchy capture, native/webview context and a separate validator allowlist exist and pass their own evaluation.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 08.9.2-T1 | Mobile prerequisites: hierarchy capture, native/webview context, separate allowlist | M | V3 | G4 | 08.9.1-T2, 04.5.1-T1 |
| 08.9.2-T2 | Mobile pilot scope + mobile evaluation set | M | V3 | G4 | 08.9.2-T1 |

## 10. Dependencies

- **04 (ingestion/adapters) + 03 (catalog/git flow):** evidence revisions, raw status, trace/artifact refs, test revisions, `evidgate.id`, the PR workflow used by 08.6 (`03.4.2-T1`), and the mobile mapper (`adapter-mobile` — mobilewright / mobile-mcp: `04.5.1-T1`/`04.5.1-T2`; shipping a mobile producer is a G4 decision after the `13.7.1` go / no-go memo).
- **05 (runs/orchestration) + 06 (evidence/artifacts):** sandbox replay as a runner job (lease, epoch, deadline) with no DB lock while a test runs; `ready` artifacts (`06.1.1-T1`), redaction at source, retention and deletion. ⚠️ 05 has no V1 (M3) replay executor — the runner-job path `05.7.2-T1` is V2/M4 and DECISIONS A5 rules out a hosted runner in V1–V2 — so 08.4.3-T1 cites the V1 CI-mode job path `05.2.1-T4` and the execution host is owner decision O10 (DECISIONS §8).
- **07 (policy/decisions) + 02 (identity/tenancy):** official evaluations stay untouched by shadow results (`07.2.2-T1`); incidents come from verified failures (`05.3.2-T5`); `test_maintainer` role and `healing:*` permissions (`02.2.1-T1`/`02.2.1-T2`), audit core (`02.7.1-T1`/`02.7.1-T2`).
- **11 (security/compliance):** sandbox isolation (`11.2.1-T6` for the V1 replay sandbox, G3 with `08.4.3`; `11.2.1-T4` extends it to V2 agents and `11.2.1-T5` evaluates hosted multi-tenant isolation, both G4), provider opt-in, untrusted CI rules, and the PDPL cross-border assessment (O4) that G1 needs **before** the first generation call (`11.8.1-T5`).
- **13 (authoring, element memory and repair proposals):** `13.3.1` is the first consumer of this gateway (G1); `13.6` records the element profile per bound step; `13.8` classifies drift and adds the screenshots (opt-in) and accessibility / DOM diff to the 08.6.1 proposal. `03.7.1-T1`/`T2` are superseded by `13.3.1` (13 §3).
- **09 (web UI):** the shadow review queue (`09.5.1-T1`..`09.5.1-T3`) renders the candidates for inspection; candidate quality and triage-time metrics come from the metric pipeline `12.5.1-T1`.
- **10 (operations) + 12 (pilot/commercial):** kill-switch and degradation runbook (`10.7.1-T5`), alerting, restore drill; healing quota and cost per accepted fix in the usage ledger (`12.3.1-T2`).

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| O4 | LLM provider(s) and whether any tenant data may leave Vietnam | Owner + data owner | before the first real G1 generation call (`13.3.1`, `08.5.1-T1` with `08.5.3-T3`) and before the transfer assessment `11.8.1-T5` is filed; the V2 healing PR follows the same decision |
| O6 | False-heal acceptance threshold, weighted by severity (V3 gate only; V2 gate = every forbidden case blocked, FR-08-19) | Owner + QA lead | before the V3 gate (08.8.3-T2, M5) |
| Q-08-1 | Approve provider use before G1 exit so `13.3.1` generation and this gateway call a real provider, not only the mock adapter? | Owner | before G1 exit `[OPEN]` |
| O10 | V1 replay execution host: (a) customer CI job via the CLI (A5-aligned; conflicts with FR-08-9 only if the patched test must enter the customer's repo, needs a sized customer-side step in 04), (b) pull 05.7.1/05.7.2/05.7.4 + 11.2.1-T4 into V1 (DECISIONS A5 change), or (c) slip sandbox replay to V2/M4 (V1 = capture, triage, candidates). Recommended [INFERENCE]: (a) — keeps A5, no hosted execution of customer code in V1, and still measures real replay quality in the pilot; (b) adds ≈ 12 raw days for 05.7.1/05.7.2/05.7.4 + 11.2.1-T4 (≈ 20 if all of 05.7 moves) | Owner + Tech Lead | before 08.4.3 feature plan (M3) |
| Q-08-2 | Which project supplies the first real locator-break corpus (pilot project vs Evidgate's own CI, R10)? | Tech Lead + pilot | M3 |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 08.1 Incident capture and fingerprinting | 9 | - | - | 9 |
| 08.2 Retrieval, ranker and triage labels | 8 | - | - | 8 |
| 08.3 Candidate generation, bounded loop and shadow | 9.5 | - | - | 9.5 |
| 08.4 Validation: AST, semantics, negative control, sandbox | 15.5 | - | - | 15.5 |
| 08.5 LLM gateway, privacy and healer generator | - | 19.5 | - | 19.5 |
| 08.6 Apply: PR, approval, canary, revoke, kill switches | 1 | 11 | - | 12 |
| 08.7 Knowledge, feedback and lineage | - | 5.25 | - | 5.25 |
| 08.8 Evaluation, metrics and phase gates | 2 | 5.5 | 1 | 8.5 |
| 08.9 V3 narrow reuse and mobile healing | - | - | 5 | 5 |
| **Total** | **45** | **41.25** | **6** | **92.25** |

→ with 20 % buffer **110.7** person-days (V1 54.0 · V2 49.5 · V3 7.2).

## 13. Contract change requests

> **Status:** Applied 2026-09-24: CR-08-1 → 00 §9.3 (candidate list is project-scoped: `GET /v1/projects/{p}/healing/candidates`); CR-08-2 → 00 §2. Applied 2026-09-25: T20 artifact kinds `a11y_snapshot`/`patch`/`ui_hierarchy` → 00 §7 (F-08-3).

| # | Request | Why | Status |
|---|---|---|---|
| CR-08-1 | Add to `00-overview.md` §9.3: `GET /v1/projects/{p}/incidents`, `GET /v1/incidents/{i}`, `POST /v1/incidents/{i}/label`, `GET /v1/healing/candidates/{c}`, `POST /v1/healing/candidates/{c}/reject`, `GET|PUT /v1/healing/switch` (with their permissions) | The flow cannot be operated with only `approve`/`revoke`; triage and the kill switch need endpoints | applied 2026-09-24 |
| CR-08-2 | Add the permission strings `healing:triage` and `healing:configure` next to `healing:approve`/`healing:revoke` in 00 §2 / plan 02 | Triage is a `qa_engineer` action and the kill switch a `tenant_admin` action; neither maps to an existing permission | applied 2026-09-24 |
| CR-08-3 | 2026-09-25 CR-D-1 + TL review: stack migrated (validator → `packages/healing-validator` Node child process of `Evidgate.Worker`, T36; LLM gateway → typed `HttpClient` + resilience + pre-egress redaction; sweepers → Cronos schedules with unique fire keys, 00 §5.3; tests → xUnit v3/Shouldly/Testcontainers.PostgreSQL); rulings applied T20/T29/T30/T31/T32/T33/T34/T35 (F-08-1..9) | Keep plan 08 consistent with the new stack and the TL rulings | applied 2026-09-25 |
