# 00 - Program Overview, Review and Shared Contracts

> Owner: Tech Lead + QA Lead. Product decisions and verified facts: `../DECISIONS.md`. Source design: `../docs/source/system-design-detailed.vi.md`.
> Every plan `01`-`12` MUST follow the contracts in this file (names, states, IDs, API conventions, estimate units). A plan that needs a contract change edits this file first and logs it in §15.
> Legend: ✅ verified · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]` (owner).

> **Plan level: HIGH-LEVEL.** Plans `00`-`12` fix scope, contracts, key decisions, risks and order-of-magnitude estimates. Before a story is built, its detailed plan is written in `plans/features/<story-id>-<slug>.md` from the template in §12 and passes the Definition of Ready. Field lists, endpoint shapes and thresholds here are starting points for that step.

---

## 1. Review of the source design

### 1.1 What is strong (kept as is)

- Clear separation of **completeness / outcome / decision** (A8) and of the three retry kinds (A9).
- Immutable manifests, evidence revisions and evaluations; late evidence creates a new revision and never rewrites a used decision.
- Customer-operated execution first; no inbound access to customer networks.
- Safe-healing boundaries are concrete (allowlist AST, assertion hash, negative control, shadow first, kill switch) and honest about statistics (rule of three).
- Realistic capacity arithmetic, recomputed ✅ (DECISIONS §9).

### 1.2 Gaps found and how this plan set closes them

| # | Gap in the source | Impact | Closed by |
|---|---|---|---|
| G1 | Working name collides with an existing QA-adjacent company | Trademark and search confusion | DECISIONS §2 (Evidgate) |
| G2 | No versions, libraries or package manager; "Node.js LTS" while Node 24 leaves Active LTS on 2026-10-20 | Inconsistent setups, early upgrade | DECISIONS §4 |
| G3 | Human login and CI authentication not designed | Long-lived secrets in customer CI; no SSO story | 02 (OIDC login, GitHub OIDC exchange, hashed tokens) |
| G4 | Milestones have no sized backlog beyond two sprints | Cannot check M1-M4 against 2-3 engineers | Stories + tasks in 01-12, roll-up §14 |
| G5 | Duplicate section numbers (five §4, four §6); page map for the .docx | Ambiguous references | Area plans 01-12 with unique story ids (§6) |
| G6 | No state for artifacts, incidents, waivers, agents | Implementers invent states | §8 state machines |
| G7 | Playwright ships its own healer agent (DECISIONS F1) - not considered | Reinvent or, worse, adopt a tool that emits `skip` | 08: sandboxed candidate generator only, same validator |
| G8 | Personal data in artifacts treated only as "redact at source" | PDPL obligations (processor contract, cross-border assessment, 72 h breach notice) | 11 + DECISIONS §7 |
| G9 | Worker mechanism ambiguous (PostgreSQL job table vs. outbox) | Two ad-hoc queues | §5.3: pg-boss for internal tasks, domain lease tables for runner jobs |
| G10 | No dogfooding or reference fixture set | Adapter mapping bugs found by customers | 01/04: Evidgate's own CI results are the first fixture |
| G11 | Pricing, usage metering and trademark have no owner or plan | Pilot contract blocked | 12 |

### 1.3 Scope decisions

- [DECISION] Delivery order follows the source milestones (§4). One developer keeps V1 ingest/reporting and shadow healing before any editor or multi-platform agent (source §12).
- [DECISION] V1 supports **GitHub Actions** + **Playwright TypeScript** + **one JUnit XML producer** (pytest `junitxml` assumed; confirmed in M0, O1).
- [DECISION] Web and API tests in agent mode are V2; Android agent mode is V2 *conditional* on a paying need; iOS and hosted runners are V3 (M5).
- [DECISION] Test Studio in V1 = templates + Git flow only; the draft editor and AI drafts are V2.
- Out of scope for V1-V2: hosted runners, device farm, own browser/API engine, fine-tuned models, n8n/Temporal/Kafka/Kubernetes, SAML, on-premise.

## 2. Actors and roles

| Role (code) | Default scope | Limits |
|---|---|---|
| `viewer` | Read granted reports | No secrets; sensitive artifacts need `artifact:read_sensitive` |
| `qa_engineer` | Drafts, runs, triage | Cannot change release policy |
| `test_maintainer` | Approve tests and healing | Cannot approve a sensitive candidate they authored |
| `release_approver` | Policy revisions, waivers | A waiver never changes a test result |
| `tenant_admin` | Membership, integrations, quota | Secret values are never readable |
| `agent` (service identity) | Claimed jobs and uploads in its pool | No approvals, policy or other projects |
| `ci_producer` (service identity) | Create runs, send events/artifacts, finalize for one project | Minted per CI job (02) |
| `platform_operator` (Evidgate staff) | Support access with scope + expiry, audited | No tenant data by default |
| `billing_operator` (Evidgate staff) | Read usage ledger and invoice data across tenants (`billing:read`) | No artifact or test data access; every read audited (12) |

Permissions are strings `resource:action` (for example `run:create`, `job:claim`, `healing:approve`); roles are sets of permissions defined in 02. `contracts/permissions.json` is the single source of permission strings; built-in roles are seeded from it (02 D2). A member can hold several roles; the permission check is per project. Healing uses four strings: `healing:triage` (`qa_engineer`), `healing:approve` / `healing:revoke` (`test_maintainer`), `healing:configure` (`tenant_admin`, kill switch). `environment:write` (`tenant_admin`, `test_maintainer`) creates environment revisions (05.1.2-T1); `security_event:read` is `tenant_admin` only (11.5.1-T5). `billing:read` (`billing_operator` only) reads usage ledger and invoice data across tenants, every read audited (12). Catalog strings (`case:*`, `suite:*`, `fixture:write`, `draft:*`, `ai_draft:request`) and their default role mapping are owned by 03 §4.5 (03 §13 CR-5).

## 3. Glossary (canonical names; code, API and UI use these)

| Term | Meaning |
|---|---|
| organization (`org`) | Tenant. Every business row carries `org_id`. |
| project | A repository/product under test inside an org. |
| test case (`case`) | Stable identity (UUID in annotation `evidgate.id` or sidecar manifest). Rename/move never creates a new case. |
| test revision | Immutable snapshot of a case at a commit (`revision_hash`). |
| suite revision | Immutable list of case revisions × variants, dependency DAG, shard strategy, retry policy, environment needs. |
| variant | Hash of normalised, secret-free parameters (browser, device, locale, data set). |
| planned execution | case × variant × target environment inside one run. Key: `execution_key`. |
| release | A set of `release_component` rows (component → digest) plus the runs bound to it. |
| manifest | Uploaded, verified, immutable plan of a run (planned executions, matrix, shards) with `manifest_hash`. |
| run | One logical test pass over a locked manifest. |
| job | Unit leased to a runner (agent mode) or declared by a CI shard (CI mode). |
| attempt | One execution of one planned execution, stored as `case_attempt` (owner 05); key `(org_id, run_id, execution_key, attempt_no)`, `attempt_no` starts at 1. |
| producer | A sender of events for a run (CI shard or agent job) with a monotonic `sequence`. |
| event | Append-only JSON envelope (§9.4). |
| artifact | Object in storage (trace, video, screenshot, raw report, DOM snapshot) with checksum and sensitivity. |
| evidence revision | Frozen view of a run's accepted events + ready artifacts + completeness verdict. Row owned by 05; 06 supplies the artifact set via `EvidenceRepository.freeze`. |
| environment revision | Immutable, secret-free, hashed description of a target environment (base URLs, result-affecting dependency versions) pinned by manifests, runs and JobSpecs (owner 05). |
| policy revision | Immutable release rules (§8.6). |
| evaluation | Immutable result of policy revision × evidence revision → decision. |
| decision | `PASS` / `FAIL` / `INCONCLUSIVE` (upper case in API and UI). |
| waiver | Human risk acceptance with scope, reason, approver, expiry; displayed next to, never instead of, results. |
| quarantine | Case excluded from gating by policy with owner, reason, expiry; failures still shown. |
| incident | A verified failure record used for triage and healing. |
| healing candidate | A proposed mechanics-only patch bound to `candidate_hash` + base commit. |
| knowledge record | Verified, tenant-scoped mapping learned from an accepted candidate. |
| capability | What an adapter/agent declares (protocol, engine, platform, discovery, step events, retry, cancel). |
| template | Reusable authoring skeleton per test type (precondition, action, assertion, cleanup). |
| fixture / fixture revision | Versioned test data and seed definition referenced by a suite revision. |
| data namespace | Isolated, TTL-bound data scope allocated per run so parallel runs never share mutable data. |
| draft | Unmerged case/suite change produced by the Studio or AI; never gate-eligible. |
| dry run | Sandbox execution of a draft to prove syntax, discovery and a first pass before review. |
| catalog sync | Webhook-driven import of the merged commit into case and suite revisions. |
| case import identity | Provisional identity for an imported result without `evidgate.id`, keyed by producer + suite + name. |

## 4. Versions and milestones

| Milestone | Weeks | Reviewable output | Exit gate |
|---|---|---|---|
| M0 | 1-2 | Interviews, real reports and failures, CI/engine chosen, contracts drafted (`contracts/`) | A team commits to provide data and trial paying (12.1) |
| M1 | 3-5 | Schema, identity, manifest, CLI, first adapter | Mapping fixtures, dedupe and tenant isolation tests pass |
| M2 | 6-8 | Reports, artifacts, advisory policy, audit | A run with missing evidence never yields PASS; a restore drill passes |
| M3 | 9-12 | **V1 pilot**; incident labels; shadow healing | Customer uses it repeatedly; cost and quality measured |
| M4 | 13-18 | **V2**: agent web/API, draft workflow, healing PR | Validation and approval bound to hash; rollback drill passes |
| M5 | after V2 review | Deeper Android; iOS/hosted runners or V3 on demand | Paying demand and safety evidence (08 gate) |

The Weeks column is the source's sequence, not a date commitment (DECISIONS O8, decided 2026-09-25).

Version tags on every task: `V1` (needed for M3), `V2` (M4), `V3` (M5+). Each task also names its milestone.

Stop rule: a milestone ends only when its exit gate passes. The schedule never replaces a quality condition (source §12).

## 5. Architecture contract

### 5.1 Components

```
Browser (React SPA) ──HTTPS──► evidgate-api (NestJS) ──► PostgreSQL 18 (system of record, RLS)
                                     │                          ▲
Customer CI / agent ──HTTPS─────────►│                          │
  evidgate CLI + adapters            ├──presigned URL──► Object storage (S3 API)
  evidgate-agent (V2)                │                          ▲
                                     └─ outbox ─► evidgate-worker (pg-boss tasks, reconciler,
                                                   evaluator, webhooks, retention, healing V1 shadow)
                                                          │
                                         healing sandbox (V2) ─► LLM gateway ─► provider (opt-in)
```

Modules (one codebase, `src/modules/<name>`): `identity`, `tenancy`, `catalog`, `orchestrator`, `ingestion`, `evidence`, `policy`, `healing`, `usage`, `audit`, `notifications`. A module owns its tables and migrations; other modules call its exported service interface only. Cross-module reads use views owned by the providing module.

### 5.2 Repository layout (target)

```
contracts/            OpenAPI 3.1 + JSON Schemas (source of truth for wire formats)
apps/api/             NestJS HTTP entry
apps/worker/          worker entry (same modules)
apps/web/             React SPA
packages/cli/         `evidgate` CLI (npm `@evidgate/cli`)
packages/adapter-*/   Playwright reporter, JUnit mapper, Appium mapper
packages/agent/       `evidgate-agent` (V2)
packages/sdk/         generated TS client + types from contracts/
src/modules/          backend modules
migrations/           plain SQL, forward-only, expand/contract
fixtures/             golden reports and expected normalised output per producer
docs/                 source design, runbooks, ADRs
scripts/              repo tooling: codegen drift, licence scan, migration CLI, ops CLI, dev seed (01)
infra/                compose files, Dockerfiles, OTel collector config, dashboards, alert rules (10)
```

### 5.3 Background work [DECISION]

- **Internal tasks** (outbox dispatch, reconcile, evaluate, webhooks, retention, sweepers, usage roll-up, candidate expiry): transactional outbox row written with the business change → dispatcher enqueues into **pg-boss** → idempotent handler. Handlers use a natural idempotency key; retries have a cap then a dead-letter queue visible to operators.
- **Runner jobs** (agent mode, V2): our own `job` / `job_lease` tables. Claim with a short transaction `FOR UPDATE SKIP LOCKED` (DECISIONS F6), lease 90 s, heartbeat 15 s with jitter (source values, tune after measurement), `epoch` incremented on every new lease. Events carrying an old epoch are stored as stale evidence and never complete the current job. No DB lock is held while a test runs.
- Delivery is at-least-once everywhere (A10).

### 5.4 Tenancy and data-access rules

1. Every business table has `org_id uuid not null`; business foreign keys are composite `(org_id, <id>)` referencing a matching unique key. Platform-scoped identity tables (`user`, `user_identity`, `session`) hold no customer business data and have no `org_id` (02 D12); access to them goes through membership. The permission catalogue (`permission`, built-in `role` rows with `org_id` null and their `role_permission` rows) is platform catalogue data: readable by every org, written only by the migration owner role, and listed as an explicit exception in the 02.6.1-T2 lint (02 §4).
2. Each request/task runs in one explicit transaction that starts with `SET LOCAL evidgate.org_id = '<uuid>'` (and `evidgate.project_id` where relevant). RLS policies read these settings.
3. The application role is not table owner and has no `BYPASSRLS`; tables use `FORCE ROW LEVEL SECURITY` (DECISIONS F5). Migrations run as a separate owner role. Operator/admin paths use a separate audited role.
4. Times are `timestamptz` in UTC; durations come from runner monotonic clocks (`duration_ms`).
5. Typed columns for anything filtered, joined, authorised or constrained; JSONB only for versioned producer metadata/extensions.

## 6. Plan and ID conventions

- Plans: `plans/NN-<area>.md`. Stories: `NN.E.S` (area.epic.story, e.g. `04.2.3`). Tasks: `NN.E.S-Tn`.
- Estimate units (person-days, one senior full-stack TS developer): **XS 0.25 · S 0.5 · M 1 · L 2 · XL 3**. Anything larger than XL is split. Every area plan ends with an estimate table and **+20 % buffer**.
- Each task row: `| ID | Task | Size | Version | Milestone | Depends on |`.
- Cross-references use story ids, never page numbers.

## 7. Identifier and naming contract

| Item | Rule |
|---|---|
| Entity ids | UUIDv7 generated by the application; `uuid` column; plain UUID string in JSON |
| Test case id | UUID (any version accepted; v4 recommended in annotations) under key `evidgate.id`, in a code annotation or a sidecar manifest; both present and different = conflict, never a silent pick; sidecar filename fixed in 03.1.1-T1 |
| Idempotency keys | Client-supplied, ≤ 128 chars, unique per `(org_id, project_id, operation)` for 24 h minimum |
| `execution_key` | `sha256(case_id ‖ variant_hash ‖ target_env)` hex, computed by the server from the manifest |
| Hashes | `sha256:<hex>` over RFC 8785 canonical JSON (manifest, policy, candidate, evidence) |
| DB naming | snake_case tables (singular), `created_at`, `updated_at`, `version` (optimistic concurrency) |
| API JSON | snake_case fields; enums lower_snake except `decision` values (upper case) |
| Env vars | `EVIDGATE_*` |
| Artifact `kind` | `trace`, `video`, `screenshot`, `raw_report`, `dom_snapshot` (closed list; a new kind is a contract change) |
| Artifact `sensitivity` | `normal`, `sensitive` (`sensitive` needs `artifact:read_sensitive`) |

## 8. State machines (canonical)

### 8.1 Run

`created → queued → running → collecting → complete`; side exits `incomplete` (collection deadline passed without full evidence) and `canceled` (cancel accepted). `complete`, `incomplete`, `canceled` are terminal. A canceled run never becomes complete, even if evidence arrives during the race. Rerun = new run with `parent_run_id` and `rerun_scope` (`full` | `failed_subset`); a subset rerun is labelled and never counts as a full regression.

### 8.2 Job

`queued → leased → running → finished`; `lost` (lease expired, epoch bumped, job re-queued as a new lease) and `canceled`. History is kept per epoch. `unschedulable` is reported when no pool has the capability (never an infinite wait).

### 8.3 Case attempt status

Raw status is stored as sent. Normalised status ∈ `passed | failed | skipped | timed_out | error | blocked`; normalisation never overwrites the raw value. A failed setup marks dependants `blocked`, never `passed`.

### 8.4 Artifact

`pending_upload → uploaded → ready` (size + checksum verified by `complete`) | `rejected` (mismatch, MIME, size) ; later `pending_delete → deleted` (retention). An object that exists without `ready` is never treated as evidence.

### 8.5 Evidence completeness and evaluation

Evidence revision `completeness ∈ {complete, incomplete}` with `missing[]` reasons (missing shard, count mismatch, artifact missing, build mismatch, unknown status, stale epoch only). Evaluation fields: `completeness`, `outcome ∈ {all_passed, has_failures, no_results}`, `decision ∈ {PASS, FAIL, INCONCLUSIVE}`, `reasons[]`, `manifest_hash`, `evidence_revision`, `policy_hash`. Unique on `(org_id, run_id, evidence_revision, policy_revision_id)`.

### 8.6 Decision rules (default policy v1)

| Evidence | Decision |
|---|---|
| All required cases passed, all shards present, required artifacts ready | PASS (only for the pinned scope, build and policy) |
| A confirmed failure of a required assertion | FAIL, even if other evidence is missing (`completeness = incomplete` kept) |
| Missing shard, build mismatch, unknown status, setup error, expected count 0 (unless the suite is declared empty and the policy allows it) | INCONCLUSIVE |
| Critical case skipped or blocked | not PASS |
| Retry fail → pass | flaky policy: pilot default blocks critical cases, warns otherwise |
| Candidate passes only in shadow | original decision kept |

Policy revision fields: required cases/suites, critical cases (DECISIONS O9), allowed skips, flaky budget, required artifacts, collection timeout, late window, freshness, quarantine list reference. Waivers are separate rows and shown as "approved by waiver".

### 8.7 Healing

Incident: `open → triaged(label) → closed`. Labels: `product_defect | test_drift | environment | data_issue | unknown` with `label_source ∈ {rule, model, human}`; only human labels count as ground truth; `unknown` never enables automation.

Candidate: `proposed → validated → approved → applied`; exits `rejected`, `insufficient_evidence`, `expired`, `revoked`. Every transition stores actor, reason and hashes. Approval binds `candidate_hash` + `base_commit`; any change re-enters `proposed`.

Knowledge record: `active → revoked`; revoking marks dependent candidates and stops reuse.

Healing availability: `enabled | shadow | disabled | unavailable`, resolved system → tenant → project (the most restrictive wins).

### 8.8 Waiver and quarantine

Waiver: `requested → approved → expired | revoked` (approver ≠ requester). Quarantine entry: `active → expired | removed`; both need owner, reason, expiry.

### 8.9 Identity and access (02)

Ingest token: `active → rotated → revoked`, `active → expired`. Session: `active → revoked | expired`. Support grant: `requested → approved → expired | revoked` (approver ≠ requester, tenant-visible). CI binding: `active → disabled`.

### 8.10 Catalog and authoring (03)

Draft: `draft → dry_running → dry_passed | dry_failed → pr_open → merged | closed`. Case import identity: `unmapped → mapped | conflicted`. Catalog sync: `queued → running → done | failed`. Data namespace: `active → cleaned | cleanup_failed`.

## 9. API contract (conventions)

### 9.1 General

- REST + JSON under `/v1`, OpenAPI 3.1 in `contracts/openapi.yaml`; the TS SDK and server validators are generated from it (01).
- Cursor pagination: `?cursor=&limit=` (default 50, max 200), response `{ "items": [], "next_cursor": null }`; ordering `(created_at, id)`.
- Every response carries `request_id` (header `X-Request-Id`, echoed in errors) and a W3C `traceparent` is accepted.
- Project in the URL must match the token scope; mismatch → 403.
- Mutations that create resources or side effects require `Idempotency-Key`; same key + same canonical request hash → original response; different payload → 409 `idempotency_conflict`.
- Optimistic concurrency with `ETag`/`If-Match` on drafts, policies, candidates; stale → 412 with the current revision id in `error.details`.
- Operational endpoints live outside `/v1` (`GET /internal/health/live`, `GET /internal/health/ready`, `GET /metrics`), are not in `contracts/openapi.yaml`, carry no tenant scope and are not reachable from the public ingress.
- Usage metering and ledger writes are internal module calls only: no public write endpoint, and quantities are never taken from a client.

### 9.2 Error envelope

```json
{ "error": { "code": "manifest_invalid", "message": "…", "request_id": "…", "retryable": false, "details": [ { "index": 3, "path": "/events/3/sequence", "reason": "duplicate_different_payload" } ] } }
```

401 unauthenticated · 403 forbidden · 404 not found (also for other-tenant ids) · 409 conflict · 412 precondition failed · 413 too large · 422 schema/manifest invalid · 429 with `Retry-After` (`rate_limited` for bursts, `quota_exceeded` for a tenant/project limit; never a silent drop) · 503 with `Retry-After`. Details never contain secrets. Clients use timeout + jittered backoff and never blindly retry side-effecting calls without the same idempotency key.

### 9.3 Core endpoints (V1 unless tagged)

| Endpoint | Permission | Behaviour |
|---|---|---|
| `POST /v1/ci/token` | GitHub OIDC JWT or project ingest token | Exchange for a short-lived `ci_producer` token scoped to one project (02) |
| `POST /v1/projects/{p}/manifests` | `run:create` | Upload + verify manifest → `manifest_id`, `manifest_hash` |
| `POST /v1/projects/{p}/runs` | `run:create` | Lock manifest, policy, suite, environment; `Idempotency-Key` required; writes run + planned executions + outbox in one transaction |
| `GET /v1/runs/{r}` | `run:read` | State + current evidence revision |
| `POST /v1/runs/{r}/cancel` | `run:cancel` | 202; cancel request recorded, not "stopped" |
| `POST /v1/runs/{r}/events` | `event:write` | Batch ≤ 500 events and ≤ 1 MiB before decompression; atomic; per-event ack `accepted`/`duplicate` |
| `POST /v1/runs/{r}/finalize` | `event:write` | producer, shard, `last_sequence`, `count`, `manifest_hash` → 202, reconcile async |
| `POST /v1/artifacts/upload-url` | `artifact:write` | Scope run/attempt, media type, size, checksum → server-chosen key + presigned URL |
| `POST /v1/artifacts/{a}/complete` | `artifact:write` | Verify object/size/checksum → `ready` |
| `GET /v1/runs/{r}/decision` | `decision:read` | Exact evaluation revision + reasons, or `pending` |
| `GET /v1/projects/{p}/runs` | `run:read` | Cursor list with state, decision and rerun/reprocess labels (09) |
| `POST /v1/runs/{r}/rerun` | `run:create` | `rerun_scope` + selection → new run with `parent_run_id` (05) |
| `POST /v1/projects/{p}/environments/{e}/revisions`, `GET /v1/environment-revisions/{id}` | `environment:write` / `run:read` | Immutable, secret-free, hashed; referenced revision → 409 on change (05.1.2-T1) |
| `GET /v1/runs/{r}/reports/{report_id}/comparison` | `run:read` | Parsed events per case vs the counts/ids declared in the stored raw report, with discrepancy reasons; raw report bytes are not returned, only the `raw_report` artifact id for `download-url` (`artifact:read` + sensitivity gate) (04.4.1-T6) |
| `POST /v1/runs/{r}/reprocess` | `run:create` | `raw_report_id` + `parser_version` → 202, new evidence revision labelled `reprocessed`, never `rerun` (04) |
| `POST /v1/projects/{p}/releases`, `GET /v1/releases/{id}` | `run:create` / `run:read` | Components → build digests; runs bind to a release (05) |
| `GET /v1/releases/{id}/decision` | `decision:read` | Per-component status + release aggregate; never PASS with a component missing (07) |
| `GET /v1/artifacts/{a}/download-url` | `artifact:read` (+ `artifact:read_sensitive`) | Short-lived per-view GET URL; sensitive issuance audited (06) |
| `POST /v1/orgs/{o}/projects`, `POST /v1/orgs/{o}/memberships` | `project:create` / `membership:write` | Provisioning; last-admin guard (02) |
| `POST /v1/projects/{p}/ci-bindings`, `POST /v1/projects/{p}/ingest-tokens` | `ci_binding:write` / `ingest_token:write` | Token plaintext returned once (02) |
| `GET /v1/orgs/{o}/audit` | `audit:read` | Cursor list; support-grant actions appear here, visible to the tenant (02, 11) |
| `POST /v1/support-grants`, `POST /v1/support-grants/{g}/approve\|revoke` | `support:grant` / `support:approve` | Operator access with scope + expiry; approver is a `tenant_admin` of the target org ≠ requester (02) |
| `GET /v1/orgs/{o}/security-events` | `security_event:read` (`tenant_admin`) | Filter by project/type/severity/time: auth failures and support access in V1, injection attempts from V2 (11.5.1-T5, 11.4.1-T2; view 09.7.1-T9) |
| `GET /v1/projects/{p}/cases`, `POST /v1/suites/{s}/revisions`, `GET /v1/suite-revisions/{r}/diff` | `case:read` / `suite:write` / `suite:read` | Catalog read and suite versioning (03) |
| `POST /v1/webhooks/github` | `X-Hub-Signature-256`, no session | Replay-safe by `delivery_id`; forged signature → 401 (03) |
| `POST /v1/drafts/{d}/dry-run`, `POST /v1/drafts/{d}/pull-request` (V2) | `draft:dry_run` / `draft:merge_request` | Sandbox dry run; PR idempotent by `(branch, commit)` (03) |
| `GET /v1/projects/{p}/incidents`, `GET /v1/incidents/{i}`, `POST /v1/incidents/{i}/label` | `healing:triage` | Human label is the only ground truth (08) |
| `GET /v1/orgs/{o}/usage/summary`, `GET /v1/orgs/{o}/usage/ledger` | `usage:read` (`tenant_admin`) | Counters vs quota; ledger rows + adjustments for reconciliation (12) |
| `POST /v1/agents/jobs/claim` (V2) | `job:claim` | Pool + capability match → lease, epoch, deadline |
| `POST /v1/jobs/{j}/heartbeat` (V2) | `job:execute` | Extend lease only for the current epoch |
| `POST /v1/healing/candidates/{c}/approve` (V2) | `healing:approve` | `If-Match`, `candidate_hash`, validation set, expiry; re-check permission and context |
| `POST /v1/healing/candidates/{c}/revoke` (V2) | `healing:revoke` | Reason required; blocks future use; audited |
| `GET /v1/projects/{p}/healing/candidates`, `GET /v1/healing/candidates/{c}` | `healing:triage` | Shadow (V1) and proposed (V2) candidates with validation evidence (08) |
| `POST /v1/healing/candidates/{c}/reject` (V2) | `healing:approve` | Reason required; audited (08) |
| `GET\|PUT /v1/healing/switch` | `healing:triage` / `healing:configure` | Kill switch per tenant/project; most restrictive wins (§8.7, 08) |

Auth/session routes (`/v1/auth/*`) and org/role administration are listed in 02 §4 only. Detailed request/response shapes live in `contracts/` and area plans 02-08.

CLI gate exit codes (`evidgate gate`, recorded in `contracts/cli.md`): `0` PASS, `1` FAIL, `2` INCONCLUSIVE, `3` tool or transport error (07, 04).

### 9.4 Event envelope v1

```json
{
  "schema_version": 1,
  "event_id": "uuid", "producer_id": "string", "sequence": 12,
  "job_id": "uuid|null", "epoch": 3,
  "execution_key": "hex", "attempt_no": 1,
  "type": "test.started|test.step|test.finished|run.log|producer.heartbeat",
  "status": "raw producer status (test.finished)", "duration_ms": 1280,
  "occurred_at": "RFC 3339 UTC (runner clock, informative only)",
  "artifact_ids": ["uuid"],
  "ext": { "schema": "adapter-name/1", "…": "…" }
}
```

Rules: the whole batch is validated, canonicalised and committed atomically; a schema error or same `event_id` with a different payload rejects the batch with indices; a duplicate with the same hash is acknowledged `duplicate`. `(run, producer, sequence)` is unique and never reused after restart (new `producer_id` or resume checkpoint). Order is by `(producer, sequence)`, never receive time. An event is acknowledged only after commit.

### 9.5 Webhooks (outbound)

Headers: `Evidgate-Event-Id`, `Evidgate-Timestamp`, `Evidgate-Signature: v1=<hmac-sha256(secret, timestamp + "." + body)>`; receivers reject timestamps older than 5 min; at-least-once with exponential retry for 24 h; event types `run.state_changed`, `evaluation.created`, `healing.candidate_changed` (V2).

## 10. Adapter protocol (summary; detail in 04 and 05)

Operations `discover`, `prepare`, `execute`, `collect`, `cleanup` (source §4 table). Capability document: `protocol: {major, minor}`, `engine: {family, version}`, `platforms[]`, `supports: {discovery, step_events, retry, cancel}`, `mode: full | ingest_only`. The server rejects an incompatible major. The runner engine version is carried in the capability document and, from V2, pinned in the immutable JobSpec together with the engine image digest; runners never resolve `latest` at run time (04, 05). `ingest_only` results are viewable but not gate-eligible unless the manifest is proven complete. Protocol v1 is defined as JSON Schema in `contracts/adapter/`.

## 11. Non-functional requirements (all plans)

| Area | Target (pilot, to be measured; not an SLA) |
|---|---|
| Tenant isolation | No cross-tenant path in API, FK or RLS tests (11 test suite) |
| Durability | Acknowledged events survive ordinary crashes; disaster per RPO |
| Ingest latency | p95 < 1 s per batch within limits |
| Decision latency | p95 < 60 s from complete evidence to stored evaluation |
| Availability | 99.5 %/month for core endpoints (not test pass rate) |
| RPO / RTO | 15 min / 4 h; restore drill before pilot (10) |
| Burst | 50 000 events in 60 s (≈ 833/s) with batching and quota (10 load test) |
| Security | OWASP ASVS L2 as the verification target for web/API, with a published gap list (decision 11.1.1-T3 at M2; evidence walkthrough 11.7.1-T5 at M4); secrets never in logs or metric labels |
| Observability | OpenTelemetry traces/metrics/logs with `request_id`, `run_id`, `job_id`, `trace_id` |
| Operational SLIs | Measured, not SLA: queue/outbox lag, dead-letter depth, lease-loss rate, incomplete rate, parser-error rate, artifact-failure rate, healing false-positive rate (targets and thresholds owned by 10) |
| i18n / a11y | UI vi + en; WCAG 2.2 AA for core flows |
| Supported clients | CLI/adapters: Node 22+ (customer CI), Linux/macOS/Windows runners for the CLI |

## 12. Feature plan process and template

Process: (1) pick a story from an area plan → (2) review the relevant sections of 00/DECISIONS and the area plan (Tech Lead; + security review for identity, tenancy, artifacts, healing) → (3) write `plans/features/<story-id>-<slug>.md` → (4) Definition of Ready review → (5) build.

**Definition of Ready:** contracts touched are updated in 00/`contracts/`; acceptance criteria are testable; dependencies are done or stubbed by contract; data retention/deletion impact is stated; estimate re-checked.

**Definition of Done** (source §12): API/schema version, migration, authorisation, telemetry, failure path, compatibility fixture, operator notes; CI green; unit tests for domain rules; one integration test per endpoint on real PostgreSQL (Testcontainers); OpenAPI updated; vi + en strings. Healing features additionally: validator, validation evidence, approval binding, audit, kill switch.

Feature plan template sections: 1 Goal and non-goals · 2 User stories and acceptance criteria · 3 Contracts touched · 4 Data model and migrations · 5 API shapes · 6 States and transitions · 7 Jobs and integrations · 8 Security and privacy · 9 Test plan (behaviours, boundaries, failures) · 10 Tasks and re-estimate · 11 Rollout (flag, migration order, monitoring, rollback).

## 13. Plan index

| File | Scope | Main milestones |
|---|---|---|
| `01-foundation.md` | Repo, toolchain, contracts pipeline, CI, local stack, licence check, dogfooding | M0-M1 |
| `02-identity-tenancy.md` | Orgs, projects, membership, roles, OIDC login, CI token exchange, RLS, audit core | M1-M2 |
| `03-catalog-authoring.md` | Stable case ids, revisions, suites, variants, templates, draft workflow, AI drafts | M1, M4 |
| `04-ingestion-adapters.md` | Event API, CLI, Playwright reporter, JUnit mapper, Appium mapper, fixtures | M1-M3, M4 |
| `05-runs-orchestration.md` | Manifest, run lifecycle, finalize/reconcile, cancel/rerun, agent + leasing | M1-M2, M4 |
| `06-evidence-artifacts.md` | Upload/complete, retention, isolated viewer, redaction, safe parsing | M2 |
| `07-policy-decisions.md` | Policy revisions, evaluator, waivers, quarantine, late evidence, webhooks | M2-M3 |
| `08-healing-learning.md` | Incidents, labels, shadow healing, candidates, validator, PR flow, eval set, kill switch | M3-M5 |
| `09-web-ui.md` | Screens and UX rules | M2-M4 |
| `10-operations.md` | Environments, deploy, observability, backup/restore, load tests, capacity | M1-M3 |
| `11-security-compliance.md` | Threat model, untrusted CI, secrets, PDPL/DPA, pen test | M1-M4 |
| `12-pilot-commercial.md` | M0 discovery, pilot, usage ledger, pricing, value measurement, trademark | M0-M4 |

## 14. Estimate roll-up

Recomputed by script from every task row of 01-12 (sizes per §6; person-days, one senior full-stack TS developer). Each plan's §12 table matches these sums.

| Plan | Tasks | V1 | V2 | V3 | Total raw | Total buffered |
|---|---|---|---|---|---|---|
| `01-foundation.md` | 50 | 53 | 0.5 | 0.25 | 53.75 | 64.5 |
| `02-identity-tenancy.md` | 34 | 29.5 | 2 | 3 | 34.5 | 41.4 |
| `03-catalog-authoring.md` | 31 | 18 | 13 | 0 | 31 | 37.2 |
| `04-ingestion-adapters.md` | 39 | 35 | 5 | 0 | 40 | 48.0 |
| `05-runs-orchestration.md` | 45 | 30.5 | 22 | 2 | 54.5 | 65.4 |
| `06-evidence-artifacts.md` | 28 | 23 | 1 | 0 | 24 | 28.8 |
| `07-policy-decisions.md` | 30 | 22.5 | 1 | 0 | 23.5 | 28.2 |
| `08-healing-learning.md` | 59 | 37.5 | 37.75 | 6 | 81.25 | 97.5 |
| `09-web-ui.md` | 59 | 52.5 | 18.5 | 0 | 71 | 85.2 |
| `10-operations.md` | 38 | 42.5 | 2 | 1 | 45.5 | 54.6 |
| `11-security-compliance.md` | 44 | 43 | 9 | 1 | 53 | 63.6 |
| `12-pilot-commercial.md` | 30 | 29.5 | 2 | 0 | 31.5 | 37.8 |
| **Total** | **487** | **416.5** | **113.75** | **13.25** | **543.5** | **652.2** |
| Buffered (× 1.2) | | 499.8 | 136.5 | 15.9 | | |

By milestone (raw): M0 10.5 · M1 134 · M2 152.5 · M3 119.5 · M4 113.75 · M5 13.25. Every V1 task sits in M0-M3, every V2 task in M4, every V3 task in M5; no task depends on a task in a later milestone and the task dependency graph has no cycles (script check 2026-09-25).

**Calendar check [INFERENCE] (arithmetic, not a forecast):** buffered V1 is 499.8 person-days. At 5 working days per week and full parallelism, that is ≈ 50.0 weeks for 2 engineers and ≈ 33.3 weeks for 3, against the source's M0-M3 = 12 weeks (§4). V2 buffered (136.5 d) is ≈ 13.7 / 9.1 weeks against M4 = 6 weeks. **The source calendar does not fit this scope with 2-3 engineers.** Options for the owner (O8): (a) cut V1 to the M3 pilot's critical path (ingest → evidence → decision for one engine; UI screens, cost model and ASVS mapping trimmed), (b) keep scope and treat §4 weeks as sequence only (source §1 already says milestones are priority order, not a commitment), or (c) add engineers. **O8 decided 2026-09-25: option (b)** — scope is kept and milestones are ordering and exit gates, not dates; the §4 weeks column is the source's sequence. Calendar dates are set after O2 (team size). A V1 cut was simulated and does not reach 12 weeks (DECISIONS O8).

**Not in these numbers:** hiring, device farm, legal counsel for the DPA/transfer dossiers, pen-test vendor fees, trademark fees, platform/vendor waiting time.

## 15. Contract change log

| Date | Change | Requested by |
|---|---|---|
| 2026-09-24 | Initial contracts from source design + review G1-G11 | Tech Lead |
| 2026-09-24 | Area plans 01-12 written; CRs from 01-12 applied: §2 (`billing_operator`, permission source, healing/catalog strings), §3 (catalog glossary), §5.2 (`scripts/`, `infra/`), §5.4.1 (platform-scoped identity tables), §7 (sidecar id, artifact enums), §8.9-§8.10 (identity/catalog states), §9.1-§9.3 (internal endpoints, 412 details, `quota_exceeded`, 19 endpoint rows, CLI exit codes), §10 (engine version pinning), §11 (ASVS target, operational SLIs); §14 filled | Tech Lead (from 01-12 §13) |
| 2026-09-25 | Review fixes: §2 `environment:write`, `security_event:read`, catalog mapping pointer → 03 §4.5; §3 `attempt`/`evidence revision` owners (05) and `environment revision`; §8.6 critical cases field (DECISIONS O9); §9.3 environment-revision and report-comparison endpoints, security-events owner 11.5.1-T5; §14 recomputed (487 tasks, 543.5 raw / 652.2 buffered) after new tasks in 04, 05, 09, 10, 11 | Tech Lead (from 04-CR-3, 05-CR-3, 09-CR-3, CR-10-2, 11-CR-4) |
| 2026-09-25 | Review round 2: §2 `billing:read` for `billing_operator`, §5.4.1 platform catalogue exemption (`permission`, built-in `role`, `role_permission`), §3 `data_isolation` removed from environment revision (one source: 03 suite declaration), §9.3 security events V1 = auth failures + support access, injection attempts from V2; §9.3 report comparison returns no raw bytes (raw report via `download-url` + `artifact:read`); 11-CR-1 marked partially applied (ASVS level pending 11.1.1-T3); §14 milestones re-split after M1→M2 moves in 02/04 and dependency cycles broken in 04/05/06/07/12 | Tech Lead (ReviewRound F0-F18) |
| 2026-09-25 | O8 decided (option b): §4 weeks marked as sequence only, §14 calendar note updated; no task, size or milestone changed. |
| 2026-09-25 | No contract change: `RECOMMENDATIONS.md` holds working defaults for open items; 06-O5 closed. |
| 2026-09-25 | No contract change: O6 deadline = before the V3 gate (08.8.3-T2, M5), matching 08 FR-08-19. |
