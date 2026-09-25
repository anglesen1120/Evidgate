# 01 - Foundation: repo, contracts, database, CI and dogfooding (M0-M1)

> Contracts: `00-overview.md` §5.1-§5.4, §6-§9, §11, §12. Facts: `../DECISIONS.md` §4 (stack, versions, licence rule), §5.9, §7, §9 R2/R7/R8/R10, F3, F5, F6, O3. Source: design §4 (technology), §6 (data/roles), §9 (deploy, capacity), §12 (two-sprint backlog).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.
> Compatibility evidence read in this task (2026-09-24, `registry.npmjs.org`): `@nestjs/common|core|platform-express|testing` 12.1.0 declare peer `@nestjs/* ^12.0.0`; `@nestjs/config` 12.0.1, `@nestjs/swagger` 12.0.2, `@nestjs/terminus` 12.1.0, `@nestjs/schedule` 12.0.2, `@nestjs/event-emitter` 12.0.1 accept `^11.0.0 || ^12.0.0` ✅; `@nestjs/swagger` 12.0.2 peer is `typescript ^5.5.0 || ^6.0.0` ✅ (TS 7 excluded); `typescript-eslint` 8.70.1 peer `typescript >=4.8.4 <6.1.0` ✅; `vitest` 5.0.1 engines include `>=26.0.0` ✅; `testcontainers` 12.1.0 requires Node `>= 22.22` ✅.

## 1. Purpose and scope

**Goal:** make every later plan buildable without re-deciding the ground. One workspace, one contract pipeline, one migration/tenant-context mechanism, one durable-task mechanism, one integration harness, one CI that gates itself.

**In scope:** npm-workspaces monorepo per 00 §5.2; toolchain pinning and the Node 26 / TS 6.0.x / NestJS 12 compatibility check (DECISIONS §4 ⚠️); lint/format/type gates; OpenAPI 3.1 + JSON Schema contract-first pipeline (generated SDK, runtime validators, contract tests); plain-SQL forward-only expand/contract migrations with owner vs app roles; Kysely with an explicit transaction + `SET LOCAL` tenant helper; transactional outbox + pg-boss skeleton; Testcontainers integration harness; GitHub Actions CI for Evidgate itself; local dev stack including a licence-checked S3 emulator; dependency licence gate; ADR template in `docs/adr`; dogfooding (our CI results ingested into Evidgate from M2); the Sprint-1 skeleton of source §12.

**Out of scope:** product modules and their tables (02-08), web screens (09), deploy topology/observability/runbooks (10), customer CI onboarding (04), provider choice (10/O3).

**V1/V2/V3 split:** V1 = everything above except the fixture-promotion loop (V2, 01.10.1-T4) and the TypeScript 7 upgrade (V3, 01.1.1-T4).

## 2. Requirements

- **FR-01-1** `npm ci` from the committed lockfile installs every workspace; `npm run verify` = typecheck + lint + unit + integration + contract drift, and is what CI runs.
- **FR-01-2** Node 26 LTS and TypeScript 6.0.x are pinned (`engines`, `.nvmrc`, exact versions for 0.x packages); no floating range on a runtime dependency.
- **FR-01-3** `contracts/openapi.yaml` (OpenAPI 3.1) is the only source of wire shapes; the TS SDK and server validators are generated/derived from it; a regeneration that changes output fails CI.
- **FR-01-4** The event envelope v1 (00 §9.4) and adapter protocol v1 (00 §10) exist as versioned JSON Schemas in `contracts/`, and the same validator accepts/rejects requests in api, worker and CLI.
- **FR-01-5** Migrations are plain SQL, forward-only, expand/contract; a ledger stores name + checksum; DDL runs as the owner role, application traffic as a non-owner role without `BYPASSRLS` (F5). A migration that would break the previous app version is rejected in review.
- **FR-01-6** Every request/task runs in one explicit transaction that begins with `SET LOCAL evidgate.org_id` (and `evidgate.project_id` where relevant) per 00 §5.4.2; a query issued outside one throws.
- **FR-01-7** Business foreign keys are composite `(org_id, <id>)` referencing a matching unique key (00 §5.4.1).
- **FR-01-8** An outbox row is written in the same transaction as the business change and dispatched to pg-boss at least once (A10); handlers are idempotent by a natural key, have a retry cap, and land in a dead-letter queue visible to operators.
- **FR-01-9** Integration tests run against real PostgreSQL 18 through Testcontainers; the tenant-isolation suite is a required check (00 §11).
- **FR-01-10** Evidgate CI requires typecheck, lint, unit, integration, contract drift, licence scan and build on every PR; every dependency licence must allow closed-source commercial SaaS (DECISIONS §4).
- **FR-01-11** The local dev stack is one command: PostgreSQL 18 + an S3-compatible emulator + api + worker + web, with no production data.
- **FR-01-12** Every irreversible choice made here has an ADR in `docs/adr`, referenced from the story that made it.
- **FR-01-13** From M2, Evidgate's own CI results are ingested into an Evidgate project, and from M3 they gate Evidgate's own pull requests.
- **NFR-01-1** `npm run verify` completes in under 10 minutes on CI ⚠️ (target to measure in 01.7.1-T1).
- **NFR-01-2** Config is validated at boot and the process exits non-zero on a missing/invalid `EVIDGATE_*` value; no secret is ever logged (00 §11).
- **NFR-01-3** A dependency that fails the licence rule fails the build; exceptions exist only as an ADR row with an owner.

**Acceptance criteria (testable):** `rm -rf node_modules && npm ci && npm run verify` passes from a clean clone; a hand-edited generated file fails CI; a hand-edited applied migration fails `migrate --status`; a query outside `withTenant()` throws; two dispatchers running concurrently produce one side effect; the isolation suite fails if `FORCE ROW LEVEL SECURITY` is dropped from a business table; a GPL/AGPL dependency added to any workspace fails the licence job.

## 3. Key decisions

- **One modular monolith, two entrypoints** [DECISION: A1] — `apps/api` and `apps/worker` import the same `src/modules/*`; a module imports another module only through its exported service interface, enforced by lint (01.2.1-T1). Rejected: split services (premature distribution) and a single process (a slow webhook task would block ingest).
- **SDK generator = `@hey-api/openapi-ts` 0.99.0** ✅ MIT, peer `typescript >=5.5.3 || >=6.0.0` (npm). Rejected: `openapi-typescript` 7.13.0 ✅ MIT but peer `typescript ^5.x`, i.e. incompatible with the pinned TS 6.0.x — the same reason DECISIONS §4 pins 6.0.x.
- **Runtime validation = Ajv 8 (2020-12 mode)** ✅ MIT, with `ajv-formats` ✅ MIT. OpenAPI 3.1 uses the 2020-12 dialect, so one validator serves spec, event schema and adapter protocol. Rejected: handwritten validators per endpoint (drift) and Zod-only (no JSON Schema `$ref` story for `contracts/`).
- **Canonical hashing = RFC 8785 over `canonicalize` 5.1.0** ✅ Apache-2.0, hashes written as `sha256:<hex>` per 00 §7. Rejected: `JSON.stringify` with sorted keys (ambiguous numbers/unicode) and JCS-by-hand (untestable).
- **Migration runner = thin in-repo runner over `pg` 8.23.0** ✅ MIT: ordered plain-SQL files, one transaction per file, checksum ledger. Rejected: Kysely `Migrator` (TS migration modules, not plain SQL as DECISIONS §4 requires) and `node-pg-migrate`/`postgrator` (extra runtime dependency for behaviour we need in ~200 lines).
- **Local S3 emulator = SeaweedFS** ✅ Apache-2.0 (LICENSE read 2026-09-24). Rejected: MinIO ✅ AGPL-3.0 — it would need a dev-only exception to the DECISIONS §4 licence rule for no benefit; LocalStack Community — its repository is archived and the free replacement is non-commercial only ✅ (README read 2026-09-24). Fallback if presigned-URL parity fails in 01.8.1-T1: Scality CloudServer ✅ Apache-2.0 (GitHub licence metadata, active 2026-09-24).
- **Licence gate = `license-checker` 25.0.1** ✅ BSD-3-Clause with an allow/deny list in `scripts/licence-policy.json` (deny AGPL/SSPL/BUSL/non-commercial).
- **Logging = `pino` 10.3.1 + `nestjs-pino` 5.2.0** ✅ both MIT; `nestjs-pino` peer `@nestjs/core ^11.0.8 || ^12.0.2` ✅. Redaction list covers every `EVIDGATE_*` secret and `authorization`; no secret ever becomes a metric label or log field (00 §11).
- **Node 26 + TS 6.0.x confirmed** by the matrix above; the TS 7 upgrade is a V3 task (01.1.1-T4) because two independent peers exclude it.

## 4. Design

**Tables owned (mechanism only; module tables belong to 02-08):**

| Table | Columns (high level) | Constraints |
|---|---|---|
| `schema_migration` | `name`, `checksum`, `applied_at`, `duration_ms` | PK `name`; sequential, never re-run |
| `outbox` | `id`, `org_id`, `aggregate_type`, `aggregate_id`, `task_type`, `payload jsonb`, `idempotency_key`, `available_at`, `attempts`, `state`, `created_at` | unique `(org_id, idempotency_key)`; partial index on undispatched rows |

Schema `evidgate` also holds the helper functions the RLS policies read (`evidgate.current_org_id()`, `evidgate.current_project_id()`), created by migration as the owner role.

**Endpoints:** plan 01 owns no business endpoint. It owns the conventions and the skeleton of `contracts/openapi.yaml` (00 §9) plus operational endpoints outside `/v1`: `GET /internal/health/live`, `GET /internal/health/ready`, `GET /metrics` (see §13 CR-01-4).

**Flows:**
1. *Boot:* validate `EVIDGATE_*` config → build pools (app role) → check migration ledger matches the code's expected head → start handlers. Mismatch = refuse to start.
2. *Request/task:* `withTenant()` opens BEGIN, issues `SET LOCAL`, runs the handler, commits; outbox rows written inside that transaction are dispatched afterwards.
3. *Contracts:* edit `contracts/` → `npm run codegen` → SDK + types + validators → `npm run contracts:test` → CI diff gate.
4. *Schema change:* expand migration (additive) ships with the release; the contract migration ships one release later.

**States:** plan 01 introduces no business state; run/job/artifact/evidence states stay exactly as 00 §8 defines them. The dead-letter queue is an internal task state (queued → active → retry → dead_letter → replayed), not a business state.

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Migration fails midway | One transaction per file (PostgreSQL DDL is transactional ⚠️); ledger row written only on success; runner exits non-zero | No half-applied schema, deploy blocked |
| Applied migration edited | Checksum mismatch → runner refuses to start, `--status` shows the diff | History is immutable |
| Codegen not re-run after a spec edit | CI drift job fails the build | Spec is the single source of truth |
| Commit succeeded, dispatch crashed before enqueue | Dispatcher re-reads undispatched rows on its next tick | At-least-once delivery (A10) |
| Same outbox row dispatched twice | Handler dedupes on `(org_id, idempotency_key)` | One side effect |
| Task exceeds retry cap | Row moves to dead-letter + alert (10.3.1-T5, runbook 10.7.1-T1) | No silent loss |
| App role attempts to bypass RLS | `FORCE ROW LEVEL SECURITY` (F5) + isolation suite | No cross-tenant path (00 §11) |
| Query issued outside the tenant transaction | `withTenant` guard throws in tests and in `apps/api`'s request scope | Tenant context is never implicit |
| Emulator behaves unlike the provider (presigned URL, multipart, checksum headers) | 01.8.1-T1 acceptance hits the same code path as upload/complete; a provider smoke test is required before the pilot (10.6.1-T2) | Local green ≠ production green is made explicit |
| Dependency violates the licence rule | Licence job fails; either replace the dependency or add an ADR exception with an owner | Licence rule (DECISIONS §4) is enforced, not documented |
| Node/TS/module peer conflict after an upgrade | The compatibility matrix job (`npm ls` + matrix ADR) fails CI and names the conflicting peer | Uppgrades are evidence-driven |

## 6. Security and privacy

- Secrets live only in the environment/CI secret store; `.env*` is git-ignored; the redaction list in the logger covers every `EVIDGATE_*` secret and `authorization`.
- The dev stack holds no real customer data (00 §11); dev roles mirror the production separation (owner/app/operator) so an RLS mistake fails locally first.
- CI: GitHub Actions pinned by commit SHA, OIDC where a cloud credential is needed (F3), no long-lived cloud key in the repo; published packages get provenance later (10.2.1-T2).
- Dependency surface: lockfile committed, licence gate (§2 FR-01-10), `npm audit --omit=dev` with a documented exception list, SBOM artifact per build.
- `npm ci` may run package install scripts; the lockfile plus review is the control, and new packages with install scripts need a reviewer note (01.7.1-T4).
- Privacy: this plan stores no personal data; artifact handling and PDPL obligations are 06/11.

## 7. Test strategy

- **Unit:** canonical-JSON hashing golden vectors (RFC 8785 encoding of numbers/unicode), Ajv dialect behaviour on the event envelope, migration-name ordering, config validation failures.
- **Integration (Testcontainers, PostgreSQL 18):** RLS with the app role; `withTenant` throws outside a transaction; outbox row + business row commit atomically; concurrent dispatchers with `FOR UPDATE SKIP LOCKED` produce one side effect; retry cap → dead-letter; expand/contract rename pair leaves the old code path working during the expand phase.
- **Contract:** every implemented handler's response validates against `contracts/openapi.yaml`; error-envelope and pagination examples match 00 §9.2/§9.1; generated SDK compiles under TS 6.0.x with `strict`.
- **CI failure tests (deliberate):** edited generated file, edited applied migration, AGPL dependency, missing `Idempotency-Key` on a side-effecting route.
- **Fixtures:** `fixtures/` starts with one hand-written event batch, one manifest and the hashing vectors; producer fixtures arrive with 04.

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| A `@nestjs/*` module used later (not in the 9 checked) has no NestJS 12 path | Medium | Medium | Matrix task must be re-run for every new module (ADR-0002 is the register); pin NestJS minor |
| Codegen tooling breaks on TS 6.0.x drift | Medium | Medium | Drift gate + SDK compile check in CI; `openapi-typescript` is not an option (peer `^5.x`) |
| Migration discipline erodes (destructive change in one release) | Medium | High | Expand/contract playbook + a rejected-change example in review; runner has no destructive mode |
| RLS is bypassed by a future admin path | Medium | High | `FORCE` policy on every business table + isolation suite (01.6.1-T4) |
| Licence gate becomes noise and gets ignored | Medium | Medium | Deny list is small and explicit; exceptions require an ADR row |
| Two-process skeleton tempts shared-process shortcuts (in-memory queues) | Medium | Medium | 01.5.1-T5 test asserts survival across a worker restart |
| Dev emulator drifts from the provider | High | Medium | Emulator parity is an acceptance criterion; provider smoke test before the pilot (10.6.1-T2) |
| Dogfooding slips and adapters are only tried by customers | Medium | High | 01.10 stories are V1 and land before the M3 pilot |

## 9. Epics, stories and tasks

### Epic 01.1 Stack compatibility and repository bootstrap (M0-M1)

#### Story 01.1.1 Toolchain and workspace — acceptance: `npm ci` from a clean clone builds every package; the matrix ADR names each `@nestjs/*` module with its peer evidence
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.1.1-T1 | Compatibility matrix for every planned `@nestjs/*` module + Node 26 + TS 6.0.x + Vitest 5 + Testcontainers 12 → ADR-0002 (register for future modules) | S | V1 | M0 | — |
| 01.1.1-T2 | Pin toolchain: `.nvmrc`, `engines`, `typescript@6.0.x`, exact pins for 0.x packages, root workspaces `package.json`, committed `package-lock.json` | M | V1 | M0 | 01.1.1-T1 |
| 01.1.1-T3 | Workspace skeleton exactly per 00 §5.2 (`contracts/ apps/api apps/worker apps/web packages/cli packages/adapter-* packages/agent packages/sdk src/modules migrations/ fixtures/ docs/` + `scripts/`), per-package tsconfig, project references | M | V1 | M0 | 01.1.1-T2 |
| 01.1.1-T4 | TypeScript 7.x upgrade once the toolchain allows it (blocked by `typescript-eslint@8.70.1` peer `<6.1.0` and `@nestjs/swagger@12.0.2` peer `^5.5.0 \|\| ^6.0.0`) | XS | V3 | M5 | 01.1.1-T2 |

#### Story 01.1.2 Two-process skeleton — acceptance: api and worker start from one codebase; a handler change is visible in both; `/internal/health/ready` is green only with database + migrations head
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.1.2-T1 | `apps/api` + `apps/worker` NestJS 12 boot from the same modules: `EVIDGATE_*` config validation, health endpoints, graceful shutdown, structured logging with redaction | L | V1 | M1 | 01.1.1-T3 |
| 01.1.2-T2 | Module boundary scaffolding: the eleven `src/modules/<name>` of 00 §5.1, each exporting one service interface; boundary lint rule wired in 01.2.1-T1 | S | V1 | M1 | 01.1.1-T3 |
| 01.1.2-T3 | Sprint-1 skeleton slice (source §12): manifest → run → planned execution → event batch → completeness stub → decision stub through real module boundaries, fakes only, no business rules | L | V1 | M1 | 01.1.2-T2 |

### Epic 01.2 Quality gates (M0-M1)

#### Story 01.2.1 Lint, format and types — acceptance: `npm run verify` fails on a boundary violation, an unformatted file, a `Promise` not awaited, and a type error under `strict`
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.2.1-T1 | ESLint 10 flat config + typescript-eslint 8 pinned to TS 6: module-boundary rule, `no-floating-promises`, `no-console`, `--max-warnings 0` | M | V1 | M1 | 01.1.1-T3 |
| 01.2.1-T2 | Prettier 3 + `.editorconfig` + `format:check` in CI | S | V1 | M0 | 01.1.1-T3 |
| 01.2.1-T3 | Strict type check: `tsc -b` across project references with `strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes` | M | V1 | M1 | 01.1.1-T3 |
| 01.2.1-T4 | Pre-commit hooks: husky 9 + lint-staged 17 on staged files only | XS | V1 | M1 | 01.2.1-T1 |
| 01.2.1-T5 | Commit/PR conventions: Conventional Commits, PR template, CODEOWNERS draft | XS | V1 | M1 | — |

### Epic 01.3 Contract-first pipeline (M0-M1)

#### Story 01.3.1 Spec and schemas — acceptance: `contracts/openapi.yaml` lints clean at warn severity and carries every convention of 00 §9.1/§9.2
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.3.1-T1 | `contracts/openapi.yaml` (OpenAPI 3.1) skeleton: `/v1` prefix, error envelope, cursor pagination, `request_id`, `Idempotency-Key`, `ETag`/`If-Match`, the V1 endpoints of 00 §9.3 as documented stubs; spectral (Apache-2.0) lint gate | L | V1 | M0 | 01.1.1-T3 |
| 01.3.1-T2 | JSON Schemas: event envelope v1 (00 §9.4) + adapter protocol v1 (00 §10), versioned files with major/minor rules | M | V1 | M1 | 01.3.1-T1 |
| 01.3.1-T3 | Contract versioning + deprecation policy (breaking = major; incompatible adapter major rejected per 00 §10) + `contracts/README` | S | V1 | M1 | 01.3.1-T1 |

#### Story 01.3.2 Generated SDK, validators and contract tests — acceptance: hand-editing a generated file or a validated response pattern fails CI
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.3.2-T1 | Generate typed client + types into `packages/sdk` with `@hey-api/openapi-ts`, committed, deterministic; `npm run codegen` + `git diff --exit-code` drift gate | L | V1 | M1 | 01.3.1-T1 |
| 01.3.2-T2 | Runtime validators with Ajv 8 (2020-12) + `ajv-formats`, one shared validator module for api/worker/CLI; batch validation returns per-event indices (00 §9.4) | L | V1 | M1 | 01.3.1-T2 |
| 01.3.2-T3 | Contract tests: implemented responses validated against the spec, error-envelope and pagination fixtures, drift fails CI | M | V1 | M1 | 01.3.2-T1 |
| 01.3.2-T4 | Canonical JSON hashing (RFC 8785 via `canonicalize`), `sha256:<hex>` per 00 §7, golden vectors for manifest/policy/candidate/evidence | M | V1 | M1 | 01.1.1-T3 |

### Epic 01.4 Database foundation (M1)

#### Story 01.4.1 Migrations, roles and RLS — acceptance: migrations apply as owner, app role cannot bypass RLS, an edited applied migration stops the runner
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.4.1-T1 | Migration runner over `pg`: ordered plain-SQL files, one transaction each, forward-only, `schema_migration` ledger with checksums, `--status`/`--dry-run`, checksum-mismatch refusal | L | V1 | M1 | 01.1.1-T3 |
| 01.4.1-T2 | Roles: owner (DDL), app (not owner, no `BYPASSRLS`), operator (audited), plus the local bootstrap script | L | V1 | M1 | 01.4.1-T1 |
| 01.4.1-T3 | RLS baseline: `evidgate.current_org_id()`/`current_project_id()` reading the GUCs, `FORCE ROW LEVEL SECURITY` template for every business table, bypass/isolation test | L | V1 | M1 | 01.4.1-T2 |
| 01.4.1-T4 | Expand/contract playbook + a fixture migration pair proving a zero-downtime column rename against the runner | M | V1 | M1 | 01.4.1-T1 |

#### Story 01.4.2 Query layer and conventions — acceptance: a query outside `withTenant()` throws; a business FK without `org_id` is rejected by the test
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.4.2-T1 | Kysely 0.29 setup with `pg`: pooled connections, `kysely-codegen` typed `DB`, no implicit transactions, `withTenant(orgId, projectId?, fn)` issuing `SET LOCAL evidgate.org_id` (00 §5.4.2) with an outside-transaction guard | L | V1 | M1 | 01.4.1-T2 |
| 01.4.2-T2 | Concurrency helpers: `FOR UPDATE SKIP LOCKED` claim helper (F6) + composite `(org_id, <id>)` FK convention with a test that rejects an FK without `org_id` (00 §5.4.1) | M | V1 | M1 | 01.4.2-T1 |
| 01.4.2-T3 | Column conventions: app-generated UUIDv7 ids, `created_at`/`updated_at` (`timestamptz` UTC), `version` optimistic concurrency, `duration_ms` from runner clocks; helper + test | S | V1 | M1 | 01.4.2-T1 |

### Epic 01.5 Outbox and pg-boss skeleton (M1)

#### Story 01.5.1 Durable internal tasks — acceptance: commit-then-crash loses no task; double dispatch yields one side effect; retry cap lands in the dead-letter queue
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.5.1-T1 | pg-boss 12 bootstrap in `apps/worker`: queues, single-owner cron guard, pool sizing, graceful shutdown, `EVIDGATE_*` settings | M | V1 | M1 | 01.4.2-T1 |
| 01.5.1-T2 | `outbox` table + write helper that joins the business transaction (00 §5.3) with `idempotency_key` unique per `(org_id, idempotency_key)` | M | V1 | M1 | 01.4.2-T1 |
| 01.5.1-T3 | Dispatcher: claims undispatched rows with `FOR UPDATE SKIP LOCKED`, enqueues one pg-boss task per row with the natural idempotency key, marks dispatched; concurrency-safe | L | V1 | M1 | 01.5.1-T1, 01.5.1-T2 |
| 01.5.1-T4 | Handler contract + retry cap + dead-letter row + operator CLI (`scripts/ops/dlq.ts list\|retry`) + alert hook for 10.3.1-T5 | M | V1 | M1 | 01.5.1-T3 |
| 01.5.1-T5 | Integration tests on Testcontainers: crash between commit and enqueue, duplicate dispatch, retry-cap → dead-letter | M | V1 | M1 | 01.5.1-T4 |

### Epic 01.6 Test harness (M1)

#### Story 01.6.1 Integration harness and fixtures — acceptance: `npm run test:integration` boots PostgreSQL 18 in a container, applies migrations once, and fails on any cross-tenant access
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.6.1-T1 | Vitest 5 configuration per workspace: node environment, v8 coverage, deterministic order, no network by default | S | V1 | M1 | 01.1.1-T3 |
| 01.6.1-T2 | Testcontainers 12 harness for PostgreSQL 18 (image pinned by digest): migrations applied once, tenant-scoped truncate between tests, RLS enabled, `withTestDb()` helper | L | V1 | M1 | 01.4.1-T2 |
| 01.6.1-T3 | `fixtures/` layout + loader + golden-file assertion helper (raw producer report → expected normalised output), ready for 04's fixtures | M | V1 | M1 | 01.6.1-T2 |
| 01.6.1-T4 | Tenant-isolation suite: cross-tenant read, write and id-guessing attempts over API and SQL paths must fail (00 §11) as a required check | M | V1 | M1 | 01.6.1-T2 |
| 01.6.1-T5 | Playwright 1.63 workspace for `apps/web` E2E: config, one smoke test, CI job wiring (screens are 09) | S | V1 | M1 | 01.1.1-T3 |

### Epic 01.7 CI and licence gate (M1)

#### Story 01.7.1 Evidgate's own pipeline — acceptance: a PR with a type error, a drifted contract, an AGPL dependency or an unformatted file is blocked by required checks
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.7.1-T1 | GitHub Actions pipeline: `npm ci`, typecheck, lint, unit, integration (Docker on the runner ⚠️ confirm), contract drift, build; caching, concurrency cancel, Node 26 | L | V1 | M1 | 01.6.1-T1, 01.6.1-T2 |
| 01.7.1-T2 | Dependency licence gate: `license-checker` + `scripts/licence-policy.json` allow/deny (AGPL/SSPL/BUSL/non-commercial denied per DECISIONS §4), runs on every PR and on lockfile change; exception = ADR row | M | V1 | M1 | 01.7.1-T1 |
| 01.7.1-T3 | Branch protection on `main`: required checks, CODEOWNERS, PR template enforcement | S | V1 | M1 | 01.7.1-T1 |
| 01.7.1-T4 | Supply chain: Actions pinned by commit SHA, dependency update automation, `npm audit --omit=dev` gate with a documented exception list, SBOM artifact | M | V1 | M1 | 01.7.1-T1 |

### Epic 01.8 Local dev stack (M1)

#### Story 01.8.1 One-command local stack — acceptance: `npm run dev` on a clean machine starts api, worker, web, PostgreSQL 18 and the S3 emulator, and a presigned PUT/GET round-trip succeeds
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.8.1-T1 | S3 emulator licence check and decision: SeaweedFS (Apache-2.0 ✅) chosen; MinIO rejected (AGPL-3.0 ✅); Scality CloudServer (Apache-2.0 ✅) fallback, LocalStack rejected (archived; free tier non-commercial); acceptance = presigned PUT/GET + SigV4 + checksum headers match the provider path 06 uses; ADR-0004 | S | V1 | M1 | — |
| 01.8.1-T2 | Compose dev stack: PostgreSQL 18 (digest-pinned) + emulator + api + worker + web with hot reload, healthchecks, named volumes | M | V1 | M1 | 01.8.1-T1 |
| 01.8.1-T3 | `scripts/dev/seed.ts` (one org, one project, memberships from the 02 roles catalogue, one sample policy revision) + `npm run dev` + `docs/dev-setup.md` quickstart | M | V1 | M1 | 01.8.1-T2 |

### Epic 01.9 ADRs and engineering docs (M0-M1)

#### Story 01.9.1 Decision records — acceptance: every choice in §3 has an ADR with alternatives and a dated status, referenceable by id
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.9.1-T1 | `docs/adr/` template + index + rule (one ADR per irreversible choice, reviewed before merge) | S | V1 | M0 | — |
| 01.9.1-T2 | ADRs 0001-0005: modular monolith + two processes (A1), compatibility matrix + TS 7 trigger, migration runner + role split, S3 emulator, licence allow/deny policy | M | V1 | M1 | 01.9.1-T1 |
| 01.9.1-T3 | Docs conventions: where runbooks (10), module READMEs, `contracts/README` and feature plans live; link check in CI | S | V1 | M1 | 01.9.1-T1 |

### Epic 01.10 Dogfooding (M2-M3)

#### Story 01.10.1 Evidgate gates Evidgate — acceptance: a real `main` build and a PR run appear in Evidgate with the same fixtures a customer would use, and a FAIL blocks the PR once 03/07 land
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.10.1-T1 | Emit our own CI results in the ingest format: Vitest/Playwright → JUnit XML + artifact set produced by the same adapter code as customers (04) | M | V1 | M2 | 01.7.1-T1 |
| 01.10.1-T2 | Ingest our own runs: CI exchanges the GitHub OIDC credential (F3) for a short-lived ingest token and pushes runs for `main` and PRs | M | V1 | M2 | 01.10.1-T1 |
| 01.10.1-T3 | Gate our own pull requests on the Evidgate decision (required check; "missing evidence never yields PASS" is exercised weekly) | M | V1 | M3 | 01.10.1-T2 |
| 01.10.1-T4 | Fixture promotion loop: parser errors/warnings and mapping gaps found by dogfooding become `fixtures/` cases and 04 issues | S | V2 | M4 | 01.10.1-T3 |

## 10. Dependencies

- **02-identity-tenancy:** the roles/permissions catalogue consumed by the dev seed (01.8.1-T3); RLS helpers are shared with 02's tenancy epic.
- **04-ingestion-adapters:** consumes `contracts/` event schema + validator (01.3.1-T2, 01.3.2-T2), the fixture loader (01.6.1-T3) and produces the dogfooding reports (01.10.1-T1).
- **05-runs-orchestration:** uses the outbox/dispatcher (01.5) and the claim helper (01.4.2-T2); its runner-job leasing tables stay domain logic, not pg-boss (00 §5.3).
- **10-operations:** consumes the CI pipeline (01.7.1-T1), the migration runner and role split (01.4.1), the dev stack (01.8.1-T2) and the alert hook (01.5.1-T4); needs the layout addition of §13 CR-01-3.
- **11-security-compliance:** inherits the tenant-isolation suite (01.6.1-T4) as evidence and the secret handling rules of §6.

## 11. Open questions

- **[OPEN]** Cloud provider/region (O3, owner) — blocks nothing in M0-M1 but decides how the emulator parity check (01.8.1-T1) and the provider smoke test (10.6.1-T2) are run. Needed by M2.
- **[OPEN]** Does the licence gate also run in the published CLI/adapter release path (shipped to customer CI), or only in the repo? Owner: Tech Lead, needed by M4 (CLI publish).
- **[OPEN]** ⚠️ GitHub-hosted runners' Docker availability for Testcontainers is assumed, not verified; 01.7.1-T1 confirms it or moves integration tests to a self-hosted runner. Needed by M1.
- **[OPEN]** Whether `scripts/` in §13 CR-01-3 is the final home for repo tooling, or each tool becomes a `packages/*` workspace. Owner: Tech Lead, needed by M1.

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 01.1 Stack compatibility and repository bootstrap | 7.0 | 0 | 0.25 | 7.25 |
| 01.2 Quality gates | 3.0 | 0 | 0 | 3.0 |
| 01.3 Contract-first pipeline | 9.5 | 0 | 0 | 9.5 |
| 01.4 Database foundation | 10.5 | 0 | 0 | 10.5 |
| 01.5 Outbox and pg-boss skeleton | 6.0 | 0 | 0 | 6.0 |
| 01.6 Test harness | 5.0 | 0 | 0 | 5.0 |
| 01.7 CI and licence gate | 4.5 | 0 | 0 | 4.5 |
| 01.8 Local dev stack | 2.5 | 0 | 0 | 2.5 |
| 01.9 ADRs and engineering docs | 2.0 | 0 | 0 | 2.0 |
| 01.10 Dogfooding | 3.0 | 0.5 | 0 | 3.5 |
| **Total** | **53.0** | **0.5** | **0.25** | **53.75** |

→ with 20 % buffer **64.5** person-days

## 13. Contract change requests

> **Status:** Applied 2026-09-24: CR-01-1/2 → DECISIONS §4; CR-01-3/4 → 00 §5.2, §9.1.

| # | Target | Request (verbatim) | Owner |
|---|---|---|---|
| CR-01-1 | DECISIONS §4, object storage row | Record the chosen local emulator: **SeaweedFS** (Apache-2.0 ✅, LICENSE read 2026-09-24); no dev-only licence exception is needed because MinIO (AGPL-3.0 ✅) is rejected; Scality CloudServer (Apache-2.0 ✅) is the documented fallback if presigned-URL parity fails (01.8.1-T1); LocalStack is rejected (Community repository archived, free tier non-commercial ✅). | Tech Lead |
| CR-01-2 | DECISIONS §4, language row | Replace "Move to 7.x when the lint toolchain supports it (CR in plan 01)" with an explicit trigger: TypeScript 7.x only after `typescript-eslint` peer range includes 7 **and** `@nestjs/swagger` accepts it (today: `typescript-eslint@8.70.1` peer `typescript >=4.8.4 <6.1.0` ✅; `@nestjs/swagger@12.0.2` peer `typescript ^5.5.0 \|\| ^6.0.0` ✅); tracked as 01.1.1-T4. | Tech Lead |
| CR-01-3 | 00 §5.2 repository layout | Add two entries: `scripts/` (repo tooling: codegen drift, licence scan, migration runner CLI, ops CLI, dev seed) and `infra/` (compose files, Dockerfiles, OTel collector config, dashboards, alert rules — owned by plan 10). | Tech Lead |
| CR-01-4 | 00 §9.1 general API conventions | Add: operational endpoints live outside `/v1` and are not part of the public contract (`GET /internal/health/live`, `GET /internal/health/ready`, `GET /metrics`), are never included in `contracts/openapi.yaml`, and require no tenant scope. | Tech Lead |
