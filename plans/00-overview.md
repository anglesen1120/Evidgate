# 00 - Program Overview, Review and Shared Contracts

> Owner: Tech Lead + QA Lead. Product decisions and verified facts: `../DECISIONS.md`. Source design: `../docs/source/system-design-detailed.vi.md`.
> Every plan `01`-`12` MUST follow the contracts in this file (names, states, IDs, API conventions, estimate units). A plan that needs a contract change edits this file first and logs it in §15.
> Legend: ✅ verified · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]` (owner).

> **Plan level: HIGH-LEVEL.** Plans `00`-`12` fix scope, contracts, key decisions, risks and order-of-magnitude estimates. Before a story is built, its detailed plan is written in `plans/features/<NN>-<area>/<story-id>-<slug>.md` from the template in §12 and passes the Definition of Ready. Field lists, endpoint shapes and thresholds here are starting points for that step.

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
| G9 | Worker mechanism ambiguous (PostgreSQL job table vs. outbox) | Two ad-hoc queues | §5.3: own outbox + task queue table for internal tasks (CR-D-1), domain lease tables for runner jobs |
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

Permissions are strings `resource:action` (for example `run:create`, `job:claim`, `healing:approve`); roles are sets of permissions defined in 02. `contracts/permissions.json` is the single source of permission strings; built-in roles are seeded from it (02 D2). A member can hold several roles; the permission check is per project. Healing uses four strings: `healing:triage` (`qa_engineer`), `healing:approve` / `healing:revoke` (`test_maintainer`), `healing:configure` (`tenant_admin`, kill switch). `project:read` (every member role) and `project:write` (`tenant_admin`) read/change project settings such as retention (06, TL T21). Further strings (TL T59): `policy:read` (every member role), `policy:write` (`release_approver`), `case:write` and `draft:write` (`qa_engineer`, `test_maintainer`), `ingest_token:read`, `service_identity:read`, `service_identity:write`, `support:read`, `membership:read` (`tenant_admin`), `sso:read`, `sso:write` (`tenant_admin`, V3); `agent_pool:read` (`tenant_admin`, `test_maintainer`), `agent_pool:write` (`tenant_admin`), `job:cleanup` (`qa_engineer`, `test_maintainer`) (V2); `webhook:read`, `webhook:write` (`tenant_admin`, 07.6.2); `billing:write` (`billing_operator`, operator routes only, two-person for adjustments and statements; 12, T69); `project:create` (`tenant_admin`) creates projects in an org. `environment:write` (`tenant_admin`, `test_maintainer`) creates environment revisions (05.1.2-T1); `security_event:read` is `tenant_admin` only (11.5.1-T5). `billing:read` (`billing_operator` only) reads usage ledger and invoice data across tenants, every read audited (12). Catalog strings (`case:*`, `suite:*`, `fixture:write`, `draft:*`, `ai_draft:request`) and their default role mapping are owned by 03 §4.5 (03 §13 CR-5).

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
Browser (Angular SPA) ──HTTPS──► Evidgate.Api (ASP.NET Core 10) ──► PostgreSQL 18 (system of record, RLS)
                                     │                                 ▲
Customer CI / agent ──HTTPS─────────►│                                 │
  evidgate CLI + adapters (Node/TS)  ├──presigned URL──► Object storage (S3 API)
  evidgate-agent (V2, Node/TS)       │                                 ▲
                                     └─ outbox ─► Evidgate.Worker (.NET: outbox dispatcher, task queue,
                                                   reconciler, evaluator, webhooks, retention, healing V1 shadow)
                                                          │
                                         healing sandbox (V2) ─► LLM gateway ─► provider (opt-in)
```

Modules (one solution, `src/Modules/Evidgate.Modules.<Name>`): `Identity`, `Tenancy`, `Catalog`, `Orchestrator`, `Ingestion`, `Evidence`, `Policy`, `Healing`, `Usage`, `Audit`, `Notifications`. A module owns its tables and migrations; other modules call its exported interface in `Evidgate.Modules.<Name>.Contracts` only (enforced by project references, §5.5). Cross-module reads use views owned by the providing module.

### 5.2 Repository layout (target)

```
contracts/                 OpenAPI 3.1 + JSON Schemas (source of truth for wire formats)
Evidgate.slnx              .NET solution; Directory.Build.props, Directory.Packages.props, global.json
src/Evidgate.Api/          ASP.NET Core HTTP entry (minimal APIs; composes module endpoints)
src/Evidgate.Worker/       Generic Host entry: outbox dispatcher, task queue consumers, schedules
src/Evidgate.Migrator/     console: applies migrations/ as the owner role, `--status`
src/BuildingBlocks/        SharedKernel (ids, errors, Result), Persistence (tenant scope, outbox, task queue),
                           Canonicalization (RFC 8785), Contracts.Validation, Observability
src/Modules/Evidgate.Modules.<Name>/            Domain/, Application/Commands|Queries/, Infrastructure/, Endpoints/
src/Modules/Evidgate.Modules.<Name>.Contracts/  the module's public interface + integration events
tests/                     <Project>.UnitTests, <Module>.IntegrationTests (Testcontainers), Architecture.Tests,
                           Contract.Tests, Isolation.Tests
apps/web/                  Angular SPA (npm workspace)
packages/cli/              `evidgate` CLI (npm `@evidgate/cli`, Node/TS)
packages/adapter-*/        Playwright reporter, JUnit mapper, Appium mapper (Node/TS)
packages/agent/            `evidgate-agent` (V2, Node/TS)
packages/sdk/              generated TS client + types from contracts/ (CLI and web)
migrations/                plain SQL, forward-only, expand/contract (run by Evidgate.Migrator)
fixtures/                  golden reports and expected normalised output per producer
docs/                      source design, runbooks, ADRs
scripts/                   repo tooling: codegen drift, licence scan (NuGet + npm), dev seed (01)
infra/                     compose files, Dockerfiles, OTel collector config, dashboards, alert rules (10)
```

### 5.3 Background work [DECISION]

- **Internal tasks** (outbox dispatch, reconcile, evaluate, webhooks, retention, sweepers, usage roll-up, candidate expiry): transactional outbox row written with the business change → the worker's dispatcher moves it into the **`task` queue table** (owned by 01, `FOR UPDATE SKIP LOCKED` claim, `available_at` backoff) → idempotent handler. Handlers use a natural idempotency key; retries have a cap then the `dead_letter` state visible to operators. Schedules (cron) are parsed with Cronos; any worker may fire a due schedule, and the fire is one transaction that inserts the `task` row with unique `(schedule_name, fire_at)` — the constraint, not a leader, guarantees one task per fire (`ON CONFLICT DO NOTHING`); `pg_try_advisory_xact_lock(hashtext(schedule_name))` inside that transaction only reduces contention. No session-level advisory lock is used (pooled connections recycle sessions) (CR-D-1; pg-boss removed).
- **Runner jobs** (agent mode, V2): our own `job` / `job_lease` tables. Claim with a short transaction `FOR UPDATE SKIP LOCKED` (DECISIONS F6), lease 90 s, heartbeat 15 s with jitter (source values, tune after measurement), `epoch` incremented on every new lease. Events carrying an old epoch are stored as stale evidence and never complete the current job. No DB lock is held while a test runs.
- Delivery is at-least-once everywhere (A10).

### 5.4 Tenancy and data-access rules

1. Every business table has `org_id uuid not null`; business foreign keys are composite `(org_id, <id>)` referencing a matching unique key. Platform-scoped identity tables (`user`, `user_identity`, `session`) hold no customer business data and have no `org_id` (02 D12); access to them goes through membership. The permission catalogue (`permission`, built-in `role` rows with `org_id` null and their `role_permission` rows) is platform catalogue data: readable by every org, written only by the migration owner role, and listed as an explicit exception in the 02.6.1-T2 lint (02 §4).
2. Each request/task runs in one explicit transaction that starts with `SELECT set_config('evidgate.org_id', @org_id, true)` (the parameterised form of `SET LOCAL`; never string-built SQL) and `evidgate.project_id` where relevant. RLS policies read these settings.
3. The application role is not table owner and has no `BYPASSRLS`; tables use `FORCE ROW LEVEL SECURITY` (DECISIONS F5). Migrations run as a separate owner role. Operator/admin paths use a separate audited role (`evidgate_operator`, policies per TL T76). Cross-tenant sweeps call owner `SECURITY DEFINER` functions through a dedicated `evidgate_worker_sched` role (TL T80). Four roles: `evidgate_owner`, `evidgate_app`, `evidgate_operator`, `evidgate_worker_sched` (01.4.1).
4. Times are `timestamptz` in UTC; durations come from runner monotonic clocks (`duration_ms`).
5. Typed columns for anything filtered, joined, authorised or constrained; JSONB only for versioned producer metadata/extensions.
6. Every business table declares `unique (org_id, id)` so it can be the target of a composite FK; the 02.6.1-T2 lint checks it (TL T3).
7. RLS policies compare against `evidgate.current_org_id()` / `evidgate.current_project_id()`, which read `current_setting(…, true)` and return NULL when unset or empty, so a missing tenant context yields zero rows, never an error or all rows (TL T2).
8. **Pre-tenant resolution** (TL T1): entry points that learn the tenant only from the credential — login callback, `POST /v1/ci/token`, ingest-token authentication, `POST /v1/webhooks/github` — call owner-owned `SECURITY DEFINER` functions in schema `evidgate_resolve` that take one lookup key (token prefix/hash, OIDC binding claims, GitHub installation/repository id) and return only `(org_id, project_id, binding_id, status)`. The app role has `EXECUTE` on those functions and nothing else outside RLS; every call is audited; the request then opens the normal tenant scope. No BYPASSRLS role is used by the API. Also pre-tenant: invite activation via `evidgate_resolve.invite_by_secret` (TL T75) and the artifact viewer, whose HMAC viewer token carries `org_id`/`project_id` and opens the tenant scope from them after verification (TL T74). Every `SECURITY DEFINER` function in any schema sets `search_path = ''`, is schema-qualified, audited and reviewed (TL T80).

### 5.5 Backend code contract: .NET 10 + CQRS [DECISION] (CR-D-1)

**Module shape.** Each module is two projects: `Evidgate.Modules.<Name>` (internal: `Domain/`, `Application/Commands/`, `Application/Queries/`, `Infrastructure/`, `Endpoints/`) and `Evidgate.Modules.<Name>.Contracts` (public service interfaces, DTOs, integration-event records). A module project may reference other modules' `.Contracts` projects only; `Architecture.Tests` fails the build on any other module-to-module reference, on `Domain` referencing `Infrastructure`, and on `public` types outside `.Contracts`/`Endpoints`.

**CQRS rules.**

| Rule | Detail |
|---|---|
| C1 Commands mutate, queries read | `ICommand<TResult>` handlers are the only code that writes; `IQuery<TResult>` handlers run in a read-only transaction (`SET TRANSACTION READ ONLY`) and never enqueue outbox rows. A command returns an id/ETag/status, never a read model. Query scopes open with `BEGIN READ ONLY` — the mode is set as the transaction's first statement, before the tenant `set_config`, so no statement of the query path runs writable (probe on the pinned `postgres:18.6`: a late `SET TRANSACTION READ ONLY` is accepted and takes effect, while `SET TRANSACTION READ WRITE`/`ISOLATION LEVEL` after the first statement are rejected with SQLSTATE 25001) (TL T95) |
| C2 One pipeline, fixed order | `Mediator` 3 pipeline behaviours: **telemetry → validation (FluentValidation; JSON Schema already applied at the edge) → tenant transaction (org/project taken from the authenticated principal, which is resolved before the pipeline — session lookup or the §5.4.8 pre-tenant resolver) → authorization (permission × project from 02, read through RLS inside that transaction) → idempotency (commands with `Idempotency-Key`, 00 §9.1; `idempotency_record` is tenant-scoped) → handler → outbox flush → commit**. Authorization and idempotency need the tenant scope because membership, role and idempotency rows are tenant rows under RLS. A denied request rolls the transaction back. Order is asserted by an architecture test |
| C3 Tenant scope | `ITenantScope.BeginAsync(orgId, projectId?)` opens one `NpgsqlConnection` from the app-role `NpgsqlDataSource`, `BEGIN`, `set_config(…, true)`; EF Core `DbContext` and Dapper both use **that** connection + transaction (`Database.UseTransaction`). A `DbCommandInterceptor` (EF Core base class) throws when a command runs on a connection without the tenant setting (01 FR-01-6) |
| C4 Write model | EF Core 10 entities per module (`HasDefaultSchema` not used; table names snake_case singular via naming convention). Optimistic concurrency on the `version` column (`IsConcurrencyToken`). No lazy loading, no `Include` chains deeper than one level, no EF migrations |
| C5 Read model | Dapper over module-owned SQL views / read tables; keyset (cursor) pagination on `(created_at, id)` (00 §9.1); every list query has a covering index named in its feature plan. `AsNoTracking` if EF is used for a read |
| C6 Domain | Aggregates enforce invariants and state machines (00 §8) in plain C#; immutability of manifests/revisions/evaluations is enforced both in the domain type (no setters) and in SQL (trigger or `REVOKE UPDATE` for the app role) |
| C7 Integration events | A command that must trigger work elsewhere writes an outbox row in the same transaction (A10); consumers are idempotent by natural key. No in-process event bus that can lose work on crash |
| C8 Errors | Domain/application errors are `Result` values mapped once to the 00 §9.2 envelope by an endpoint filter; exceptions are for bugs and infrastructure failures only. No `ProblemDetails` on `/v1` |
| C9 No event sourcing | Immutable rows + audit log cover the audit need; event sourcing is rejected for V1–V3 |

**Edge.** Minimal-API route groups per module under `/v1`; JSON Schema validation (Corvus) runs before binding for ingest payloads (events, manifests, capability documents); `System.Text.Json` source-generated serializer contexts with `SnakeCaseLower`; request body size limits set per route (00 §9.3); built-in `RateLimiter` per token/tenant; `X-Request-Id` + W3C `traceparent` middleware.

### 5.6 Web code contract: Angular 22 [DECISION] (CR-D-1)

| Rule | Detail |
|---|---|
| W1 Structure | `apps/web/src/app/core` (auth, http interceptors, error mapping, i18n, layout), `shared/ui` (presentational components, CDK-based), `features/<area>/` (lazy route + SignalStore + pages). A feature never imports another feature; lint rule enforces it |
| W2 Components | Standalone only, `ChangeDetectionStrategy.OnPush`, signal inputs/outputs (`input()`, `output()`, `model()`), built-in control flow (`@if`, `@for` with `track`), `@defer` for heavy panels (trace viewer, diff). No `NgModule`, no `any`, strict templates |
| W3 Change detection | Zoneless (`provideZonelessChangeDetection`) ⚠️ stable status confirmed in 09.1.1-T2; state in signals; RxJS only at I/O edges, converted with `toSignal` / `rxResource` |
| W4 State | `@ngrx/signals` SignalStore per feature; server state is never duplicated into a global store; list state keeps the cursor, not the whole history |
| W5 Data access | Generated SDK (`packages/sdk`) behind feature data services; interceptors add `X-Request-Id`, XSRF header, `Idempotency-Key` on mutations, map the 00 §9.2 envelope to typed UI errors; no hand-written DTOs |
| W6 Security | httpOnly `__Host-` session cookie (02 FR-02-5) + XSRF double-submit; strict CSP (no `unsafe-inline`/`unsafe-eval`, nonce for Angular styles), Trusted Types ⚠️ (09.1.1-T2); never `bypassSecurityTrust*` except in the reviewed artifact-viewer bridge (06); artifacts render only in the isolated viewer origin |
| W7 Performance budgets | Initial JS ≤ 300 kB gzip, any lazy chunk ≤ 150 kB gzip (Angular `budgets` fail the build); virtual scroll (CDK) for lists > 200 rows; `@for … track id` everywhere; LCP ≤ 2.5 s on the run list at p75 on the pilot's network ⚠️ target to measure |
| W8 a11y / i18n | WCAG 2.2 AA (00 §11): CDK a11y primitives, axe checks in Playwright E2E; `@angular/localize` vi + en, no string literal in templates without `i18n` |
| W9 Tests | Vitest for stores/services/components; Playwright E2E per core flow on the built app; component harnesses (CDK testing) instead of DOM selectors |

### 5.7 Performance and security baseline (all server work) [DECISION] (CR-D-1)

**Performance.** P1 every hot endpoint (events, finalize, upload-url, complete, claim, heartbeat, decision) has a latency budget from 00 §11 and a load-test row in 10; P2 batch writes use one round trip (`unnest` arrays or binary `COPY`), never a per-row loop; P3 no N+1: a query handler issues a bounded number of SQL statements, asserted in its integration test by a command counter; P4 every filter/sort column in a list query is covered by an index in the same migration; P5 transactions are short: no network call (S3, GitHub, LLM) inside a database transaction; P6 `HttpClient` via `IHttpClientFactory` with timeouts + resilience handlers; P7 Server GC, ReadyToRun container images, `InvariantGlobalization` on for Api/Worker (the server never formats or sorts by culture; vi/en formatting happens in the web); P8 payload caps enforced before parsing (Kestrel `MaxRequestBodySize` per route, gzip ratio cap).

**Security.** S1 parameterised SQL only (Dapper parameters, EF LINQ); no string-built SQL, including `set_config`; S2 authorization in the pipeline (C2), not in endpoints; a handler without a permission attribute fails the architecture test; S3 secrets only from environment/secret store, never in logs, metric labels or exception messages (redaction list); S4 outbound requests go through an allow-listed `HttpClient` (no user-supplied URLs except the reviewed webhook targets, which are resolved and blocked for private/link-local ranges — SSRF, 11); S5 untrusted parsers (XML/JUnit, JSON, archives) run with DTD/external entities off, depth/size/entry caps and a timeout (06, 11); S6 NuGet + npm lock files, locked-mode restore, vulnerability and licence scans in CI (01.9.1, 11); S7 security headers (HSTS, CSP, `X-Content-Type-Options`, `Referrer-Policy`, frame-ancestors) from one middleware; S8 data protection keys stored in PostgreSQL and encrypted at rest by the provider; key rotation runbook in 10.

## 6. Plan and ID conventions

- Plans: `plans/NN-<area>.md`. Stories: `NN.E.S` (area.epic.story, e.g. `04.2.3`). Tasks: `NN.E.S-Tn`.
- Estimate units (person-days, one senior full-stack .NET + Angular developer, CR-D-1): **XS 0.25 · S 0.5 · M 1 · L 2 · XL 3**. Anything larger than XL is split. Every area plan ends with an estimate table and **+20 % buffer**.
- Each task row: `| ID | Task | Size | Version | Milestone | Depends on |`.
- Cross-references use story ids, never page numbers.

## 7. Identifier and naming contract

| Item | Rule |
|---|---|
| Entity ids | UUIDv7 generated by the application; `uuid` column; plain UUID string in JSON |
| Test case id | UUID (any version accepted; v4 recommended in annotations) under key `evidgate.id`, in a code annotation or a sidecar manifest; both present and different = conflict, never a silent pick; sidecar filename fixed in 03.1.1-T1 |
| Idempotency keys | Client-supplied, ≤ 128 chars, unique per `(org_id, project_id, operation)` for 24 h minimum. The `idempotency_record` table (owner 01) is the only arbiter; business tables carry no permanent unique constraint on the key (TL T11); `project_id` is NULL for org-scoped operations, uniqueness `unique nulls not distinct (org_id, project_id, operation, key)` (TL T94) |
| `execution_key` | `sha256(case_id ‖ variant_hash ‖ target_env)` hex, computed by the server from the manifest |
| Hashes | `sha256:<hex>` over RFC 8785 canonical JSON (manifest, policy, candidate, evidence) |
| DB naming | snake_case tables (singular), `created_at`, `updated_at`, `version` (optimistic concurrency) |
| API JSON | snake_case fields; enums lower_snake except `decision` values (upper case) |
| Env vars | `EVIDGATE_*` |
| Artifact `kind` | `trace`, `video`, `screenshot`, `raw_report`, `dom_snapshot`, `a11y_snapshot`, `patch` (healing candidate diff), `ui_hierarchy` (V2 mobile) (closed list; a new kind is a contract change; TL T20) |
| Artifact `sensitivity` | `normal`, `sensitive` (`sensitive` needs `artifact:read_sensitive`) |

## 8. State machines (canonical)

### 8.1 Run

`created → queued → running → collecting → complete`; side exits `incomplete` (collection deadline passed without full evidence) and `canceled` (cancel accepted). `complete`, `incomplete`, `canceled` are terminal. A canceled run never becomes complete, even if evidence arrives during the race: events after cancel are accepted, stored with `after_cancel = true` and never complete the run (TL T12). In CI mode `created → queued` happens when the producer slots of the manifest's shards are committed and `queued → running` at the first accepted event (TL T13). Events for an `incomplete` run, or a `complete` run within its `late_window`, are accepted with `late = true` and produce a new evidence revision; after the late window → 409 `run_closed` (TL T81). Transitions are idempotent: a request for a state already held or passed is a no-op (TL T82). `run.ci_run_ref` records the creating CI job's OIDC claims (TL T88). `run.purpose ∈ {regular, replay}` (TL T67): replay runs need a server-issued replay grant, never create incidents, are evaluated only in `shadow` mode and never bind to a release. Rerun = new run with `parent_run_id` and `rerun_scope` (`full` | `failed_subset`); a subset rerun is labelled and never counts as a full regression.

### 8.2 Job

`queued → leased → running → finished`; `lost` (lease expired, epoch bumped, job re-queued as a new lease) and `canceled`. History is kept per epoch. `unschedulable` is reported when no pool has the capability (never an infinite wait).

### 8.3 Case attempt status

Raw status is stored as sent. Normalised status ∈ `passed | failed | skipped | timed_out | error | blocked`; normalisation never overwrites the raw value. A failed setup marks dependants `blocked`, never `passed`.

### 8.4 Artifact

`pending_upload → uploaded → ready` (size + checksum verified by `complete`) | `rejected` (mismatch, MIME, size) ; later `pending_delete → deleted` (retention). An object that exists without `ready` is never treated as evidence.

### 8.5 Evidence completeness and evaluation

Evidence revision `completeness ∈ {complete, incomplete}` with `missing[]` reasons (missing shard, count mismatch, artifact missing, build mismatch, unknown status, setup error (`setup_error`, TL T14), stale epoch only). Evaluation fields: `completeness`, `outcome ∈ {all_passed, has_failures, no_results}`, `decision ∈ {PASS, FAIL, INCONCLUSIVE}`, `reasons[]`, `manifest_hash`, `evidence_revision`, `policy_hash`, `evaluator_version`, `mode ∈ {live, shadow}`, `exceptions_hash` + `exceptions_as_of` (the resolved waiver/quarantine snapshot captured at trigger time — the evaluator's third immutable input, TL T24). Unique on `(org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode)`; at most one `live` row per `(org_id, run_id, evidence_revision)` (partial unique; the run pins one policy revision, other policies are evaluated only as `shadow`, TL T22).

### 8.6 Decision rules (default policy v1)

| Evidence | Decision |
|---|---|
| All required cases passed, all shards present, required artifacts ready | PASS (only for the pinned scope, build and policy) |
| A confirmed failure of a required assertion | FAIL, even if other evidence is missing (`completeness = incomplete` kept) |
| Missing shard, build mismatch, unknown status, setup error, expected count 0 (unless the suite is declared empty and the policy allows it) | INCONCLUSIVE |
| Critical case skipped or blocked | not PASS |
| Retry fail → pass | flaky policy: pilot default blocks critical cases, warns otherwise; a non-critical fail → pass beyond the flaky budget ⇒ INCONCLUSIVE with reason `flaky_budget_exceeded` (TL T27) |
| Case on the policy's critical list | can never be quarantined (422 `critical_case_not_quarantinable`); only a new policy revision by a `release_approver` removes it from the list (TL T23) |
| Candidate passes only in shadow | original decision kept |

Policy revision fields: required cases/suites, critical cases (DECISIONS O9), allowed skips, flaky budget, required artifacts, collection timeout, late window, freshness, quarantine list reference. Waivers are separate rows and shown as "approved by waiver".

### 8.7 Healing

Incident: `open → triaged(label) → closed`. Labels: `product_defect | test_drift | environment | data_issue | unknown` with `label_source ∈ {rule, model, human}`; only human labels count as ground truth; `unknown` never enables automation. One incident per `(org_id, run_id, execution_key)`; failed retries attach as `incident_attempt` rows (TL T34). Incident context: `pending → complete | partial`, settled at the collection deadline + late window (TL T31).

Candidate: `proposed → validated → approved → applied`; exits `rejected`, `insufficient_evidence`, `expired`, `revoked`. Every transition stores actor, reason and hashes. Approval binds `candidate_hash` + `base_commit`; any change re-enters `proposed`.

Knowledge record: `active → revoked`; revoking marks dependent candidates and stops reuse.

Healing availability: `enabled | shadow | disabled | unavailable`, resolved system → tenant → project (the most restrictive wins). Only `enabled | shadow | disabled` are settable; `unavailable` is derived from dependency health at resolution time and never stored by an operator (TL T33).

### 8.8 Waiver and quarantine

Waiver: `requested → approved → expired | revoked` (approver ≠ requester). Quarantine entry: `active → expired | removed`; both need owner, reason, expiry.

### 8.9 Identity and access (02)

Ingest token: `active → rotated → revoked`, `active → expired`. Session: `active → revoked | expired`. Support grant: `requested → approved → expired | revoked` (approver ≠ requester, tenant-visible). CI binding: `active → disabled`. Membership: `pending → active → removed`; invite: `pending → accepted | revoked | expired` (02.1.2).

### 8.10 Catalog and authoring (03)

Draft: `draft → dry_running → dry_passed | dry_failed → pr_open → merged | closed`; `dry_failed → dry_running` (re-run) and `pr_open → draft` (PR withdrawn) are allowed (03.6.1). Case import identity: `unmapped → mapped | conflicted`. Catalog sync: `queued → running → done | failed`. Data namespace: `active → cleaned | cleanup_failed`. Suite revision: `draft → published` (immutable once published). Fixture revision: immutable on create (TL T46).

## 9. API contract (conventions)

### 9.1 General

- REST + JSON under `/v1`, OpenAPI 3.1 in `contracts/openapi.yaml`; the TS SDK (`packages/sdk`, used by CLI and web) is generated from it, server-side JSON Schema validation loads the same files, and C# DTOs are held to it by contract tests plus a diff of the served OpenAPI document (01, DECISIONS §4.1).
- Cursor pagination: `?cursor=&limit=` (default 50, max 200), response `{ "items": [], "next_cursor": null }`; ordering `(created_at, id)`.
- Every response carries `request_id` (header `X-Request-Id`, echoed in errors) and a W3C `traceparent` is accepted.
- Project in the URL must match the token scope; mismatch → 403.
- Browser sessions use the `__Host-` httpOnly, `Secure`, `SameSite=Lax` cookie; every cookie-authenticated mutation must carry `X-XSRF-TOKEN` matching the `XSRF-TOKEN` cookie (ASP.NET Core antiforgery; Angular `withXsrfConfiguration`) or gets 403 `csrf_failed`. Bearer-token clients (CLI, agent) are not cookie-authenticated (TL T38).
- Mutations that create resources or side effects require `Idempotency-Key`; same key + same canonical request hash → original response; different payload → 409 `idempotency_conflict`.
- Optimistic concurrency with `ETag`/`If-Match` on drafts, policies, candidates; stale → 412 with the current revision id in `error.details`.
- Operational endpoints live outside `/v1` (`GET /internal/health/live`, `GET /internal/health/ready`, `GET /metrics`), are not in `contracts/openapi.yaml`, carry no tenant scope and are not reachable from the public ingress.
- Operator actions (tenant deletion, dead-letter replay, re-evaluation, support tooling) live under `/internal/operator/*`, served only on the operator ingress, authenticated as `platform_operator` with an audited support grant; never under `/v1` (TL T16, T26).
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
| `POST /v1/releases/{id}/runs` | `run:create` | Explicit run ↔ release binding (`release_run` is the only binding, TL T15); refused with 409 once the run has a live evaluation (05.6.1-T2) |
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

Rules: the whole batch is validated, canonicalised and committed atomically; a schema error or same `event_id` with a different payload rejects the batch with indices; a duplicate with the same hash is acknowledged `duplicate`. `(run, producer, sequence)` is unique and never reused after restart (new `producer_id` or resume checkpoint). Order is by `(producer, sequence)`, never receive time. An event is acknowledged only after commit. An event whose `execution_key` is not a planned execution of the run rejects the batch (422 `unknown_execution_key` with indices, TL T10). **Producer binding (TL T9):** run create registers one `producer_slot` per manifest shard; the first accepted write for a slot binds it to the caller's token identity (`jti` for `ci_producer` tokens, plus the GitHub `run_id`/`run_attempt` claims when present, which must match the run's declared CI reference); writes or `finalize` for a slot bound to another identity → 403 `producer_slot_taken`, for an unknown producer → 422 `unknown_producer`. A restarted producer keeps its deterministic id (`shard-<index>`, TL T54) and resumes after its checkpointed sequence (TL T84). The first accepted write of any kind — events or `upload-url`, which carries `producer_id` — binds the slot (TL T85).

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
| Supported clients | CLI/adapters: Node 22+ (customer CI), Linux/macOS/Windows runners for the CLI. Web: last two versions of Chrome, Edge, Firefox, Safari |
| Server runtime | .NET 10 LTS on Linux containers (x64 + arm64); PostgreSQL 18 |

## 12. Feature plan process and template

Process: (1) pick a story from an area plan → (2) review the relevant sections of 00/DECISIONS and the area plan (Tech Lead; + security review for identity, tenancy, artifacts, healing) → (3) write `plans/features/<NN>-<area>/<story-id>-<slug>.md` → (4) Definition of Ready review → (5) build. The index, execution order and TL review live in `plans/features/README.md`.

**Definition of Ready:** contracts touched are updated in 00/`contracts/`; acceptance criteria are testable; dependencies are done or stubbed by contract; data retention/deletion impact is stated; estimate re-checked.

**Definition of Done** (source §12): API/schema version, migration, authorisation, telemetry, failure path, compatibility fixture, operator notes; CI green; unit tests for domain rules; one integration test per endpoint on real PostgreSQL (Testcontainers); OpenAPI updated; vi + en strings; the §5.5 CQRS rules, §5.6 Angular rules and the §5.7 performance/security rules that the feature names are met and tested. Healing features additionally: validator, validation evidence, approval binding, audit, kill switch.

Feature plan template sections: 1 Goal and non-goals · 2 User stories and acceptance criteria · 3 Contracts touched · 4 Data model and migrations · 5 API shapes (commands, queries, endpoints) · 6 States and transitions · 7 Jobs and integrations · 8 Security, privacy and performance (which §5.7 S/P rules apply and how they are tested) · 9 Test plan (behaviours, boundaries, failures) · 10 Tasks, sub-tasks and re-estimate · 11 Rollout (flag, migration order, monitoring, rollback).

Sub-tasks [DECISION] (CR-D-1): in §10 of a feature plan, every area-plan task `NN.E.S-Tn` is split into sub-tasks `NN.E.S-Tn.k` (k = 1, 2, …), each ≤ 0.5 person-day, each ending in a check a reviewer can run (a test name, a command, a screenshot). Sub-task sizes sum to the task's size ± its share of the re-estimate; a re-estimate that changes the task size is written back to the area plan and 00 §14.

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

Recomputed by `scripts/plan_graph.py` from every task row of 01-12 (sizes per §6; person-days, one senior full-stack .NET + Angular developer, CR-D-1). Each plan's §12 table matches these sums.

| Plan | Tasks | V1 | V2 | V3 | Total raw | Total buffered |
|---|---|---|---|---|---|---|
| `01-foundation.md` | 52 | 56 | 0.5 | 0.25 | 56.75 | 68.1 |
| `02-identity-tenancy.md` | 35 | 30.5 | 2 | 3 | 35.5 | 42.6 |
| `03-catalog-authoring.md` | 31 | 19 | 13 | 0 | 32 | 38.4 |
| `04-ingestion-adapters.md` | 41 | 37 | 5 | 0 | 42 | 50.4 |
| `05-runs-orchestration.md` | 46 | 31.5 | 22 | 2 | 55.5 | 66.6 |
| `06-evidence-artifacts.md` | 30 | 24.5 | 1 | 0 | 25.5 | 30.6 |
| `07-policy-decisions.md` | 33 | 23.75 | 1 | 0 | 24.75 | 29.7 |
| `08-healing-learning.md` | 61 | 41 | 37.75 | 6 | 84.75 | 101.7 |
| `09-web-ui.md` | 60 | 53.5 | 18.5 | 0 | 72 | 86.4 |
| `10-operations.md` | 38 | 42.5 | 2 | 1 | 45.5 | 54.6 |
| `11-security-compliance.md` | 45 | 44.5 | 9 | 1 | 54.5 | 65.4 |
| `12-pilot-commercial.md` | 30 | 29.5 | 2 | 0 | 31.5 | 37.8 |
| **Total** | **502** | **433.25** | **113.75** | **13.25** | **560.25** | **672.3** |
| Buffered (× 1.2) | | 519.9 | 136.5 | 15.9 | | |

By milestone (raw): M0 10.5 · M1 142 · M2 157.25 · M3 123.5 · M4 113.75 · M5 13.25. Every V1 task sits in M0-M3, every V2 task in M4, every V3 task in M5; no task depends on a task in a later milestone and the task dependency graph has no cycles (`scripts/plan_graph.py`, 2026-09-25, after CR-D-1 and the TL review rulings).

**Calendar check [INFERENCE] (arithmetic, not a forecast):** buffered V1 is 519.9 person-days (499.8 before CR-D-1 and the TL review). At 5 working days per week and full parallelism, that is ≈ 52.0 weeks for 2 engineers and ≈ 34.7 weeks for 3, against the source's M0-M3 = 12 weeks (§4). V2 buffered (136.5 d) is ≈ 13.7 / 9.1 weeks against M4 = 6 weeks. **The source calendar does not fit this scope with 2-3 engineers.** Options for the owner (O8): (a) cut V1 to the M3 pilot's critical path (ingest → evidence → decision for one engine; UI screens, cost model and ASVS mapping trimmed), (b) keep scope and treat §4 weeks as sequence only (source §1 already says milestones are priority order, not a commitment), or (c) add engineers. **O8 decided 2026-09-25: option (b)** — scope is kept and milestones are ordering and exit gates, not dates; the §4 weeks column is the source's sequence. Calendar dates are set after O2 (team size). A V1 cut was simulated and does not reach 12 weeks (DECISIONS O8).

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
| 2026-09-25 | **CR-D-1 (owner): stack change to .NET 10 + CQRS + Angular 22 + PostgreSQL 18.** §5.1 components, §5.2 layout (solution + module projects), §5.3 pg-boss → own outbox + task queue, §5.4.2 parameterised `set_config`, new §5.5 (.NET/CQRS rules C1–C9), §5.6 (Angular rules W1–W9), §5.7 (performance P1–P8, security S1–S8), §6 estimate unit, §9.1 SDK/validation, §11 clients + server runtime, §12 feature-plan path `plans/features/<NN>-<area>/`, template §8 adds performance, sub-task ids `NN.E.S-Tn.k` | Owner (via Tech Lead) |
| 2026-09-25 | §14 recomputed by `scripts/regen_docs.py` after CR-D-1, the TL review area edits and feature detailing: 502 tasks, 560.25 raw / 672.3 buffered (was 487 / 543.5 / 652.2) | Tech Lead |
| 2026-09-25 | **TL review rulings (plans/features/README.md §3):** §2 `project:read`/`project:write` (T21); §5.4 rules 6–8: `unique (org_id, id)` everywhere (T3), NULL-safe RLS helpers (T2), pre-tenant `SECURITY DEFINER` resolvers (T1); §7 idempotency store is the arbiter (T11), artifact kinds + `a11y_snapshot`, `patch`, `ui_hierarchy` (T20); §8.1 accept-and-mark after cancel (T12), CI-mode `queued` trigger (T13); §8.5 `setup_error` (T14), evaluation key + `evaluator_version`/`mode` and exceptions snapshot input (T22, T24); §8.6 flaky over budget ⇒ INCONCLUSIVE (T27), critical cases not quarantinable (T23); §8.7 incident key/context (T31, T34), settable switch modes (T33); §8.10 suite/fixture revision states (T46); §9.1 CSRF (T38), operator routes (T16, T26); §9.4 unknown execution key (T10), producer-slot binding (T9); §9.3 `POST /v1/releases/{id}/runs` (T15, 05-CR-4) | Tech Lead (review 2026-09-25) |
| 2026-09-25 | §5.3 scheduler: session advisory-lock leader replaced by idempotent fires (unique `(schedule_name, fire_at)`, transaction-scoped advisory lock only against contention); §5.5 C2 reordered: tenant transaction before authorization and idempotency (membership/idempotency rows are tenant rows under RLS); §8.5 live-row partial unique = one `live` evaluation per `(org_id, run_id, evidence_revision)`; §8.10 draft re-run and withdraw transitions (03.6.1); §8.9 membership/invite states (02.1.2); §2 permission strings T59; §8.1 run purpose T67 | Tech Lead (advisor review) |
| 2026-09-25 | **Final review rulings T70–T96** (README §3.5): §5.4.8 invite and viewer pre-tenant paths, `search_path = ''` for every SECURITY DEFINER function (T74, T75, T80); §5.5 C1 `BEGIN READ ONLY` (T95); §7 nullable idempotency project (T94); §8.1 late evidence accepted, idempotent transitions, `ci_run_ref` (T81, T82, T88); §9.4 producer restart and upload-url binding (T84, T85) | Tech Lead (final security + performance + consistency review) |
| 2026-09-26 | **Wording correction, no contract change (01.4.2-T1 QA):** §5.5 C1 said "PostgreSQL rejects `SET TRANSACTION READ ONLY` after the first statement"; that premise is false. Probe on the pinned `postgres:18.6@sha256:5a5a84b1…`: a late `SET TRANSACTION READ ONLY` is accepted and takes effect (an `INSERT` after it fails with 25006), while a late `SET TRANSACTION READ WRITE` ("transaction read-write mode must be set before any query") and a late `SET TRANSACTION ISOLATION LEVEL` both fail with SQLSTATE 25001. The requirement is unchanged — the read-only mode is set as the transaction's first statement so it is in force before the tenant `set_config`, and nothing of the query path runs writable; only the stated reason changes. Same wording corrected in `plans/features/README.md` T95, `plans/features/01-foundation/01.4.2-tenant-scope-data-access-and-pipeline.md` AC6 and the `TenantScope` comment | QA (01.4.2-T1) |
| 2026-09-26 | **Policy ownership, no contract change (02.1.1-T1 review):** `organization`/`project` (migrations 0004/0005) ship the tenant `USING`/`WITH CHECK` policy plus a permissive `evidgate_owner` policy — `FORCE ROW LEVEL SECURITY` applies to the owner as well, so without it an expand/contract backfill and every owner `SECURITY DEFINER` sweep would silently change zero rows (explicit policy, not a bypass; no role gets `BYPASSRLS`, DECISIONS F5). No `TO evidgate_operator` policy is created: a permissive org-scoped read policy OR-combines with T76's grant-gated policy and would defeat the support-grant gate, so that policy is 02.8.1-T1.1's `evidgate.operator_grant_ok` form. Endpoints of §9.3 unchanged; 00 §5.4.3 still needs the owner-policy sentence | QA + review (02.1.1-T1, M1 wave 3) |
| 2026-09-26 | **Concurrency and least privilege, DB-owned (01.4.2-T1 / 02.1.1-T1 review, M1W3-01/-05):** `public.set_updated_at_and_version()` (created by 0004, `EXECUTE` granted to `evidgate_app` because 0001 revokes the PUBLIC grant) sets `updated_at = now()` and `version = old.version + 1`; it is attached as a `before update` trigger on `organization` (0004) and `project` (0005), and the write model maps `version` as store-generated on update, so §5.5 C4's "optimistic concurrency on the `version` column (`IsConcurrencyToken`)" and §7's `version` row now mean a database-owned increment — a `where version = @original` write cannot be won by two concurrent writers. `delete` is revoked from `evidgate_app` on both tables (0001's default privileges grant DELETE on every table the owner creates in `public`), so the app role can tombstone but not hard-delete. §5.5 C4 / §7 wording update pending | QA + review (M1W3-01/-05, M1 wave 3) |
