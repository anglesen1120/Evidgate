# 01 - Foundation: repo, contracts, database, CI and dogfooding (M0-M1)

> Contracts: `00-overview.md` §5.1-§5.7 (incl. §5.5 .NET/CQRS rules C1–C9, §5.7 performance/security P/S rules), §6-§9, §11, §12. Facts: `../DECISIONS.md` §4 (stack, versions, licence rule; CR-D-1), §5.9, §7, §9 R2/R7/R8/R9/R10, F3, F5, F6, O3. Rulings: `features/README.md` §3 T11, T18, T40, T42, T47, T48, T49. Source: design §4 (technology), §6 (data/roles), §9 (deploy, capacity), §12 (two-sprint backlog).
> Level: high-level plan; each story gets a detailed plan in `plans/features/01-foundation/` (00 §12) before build.
> Compatibility evidence (2026-09-25, NuGet nuspec + npm registry; full table in DECISIONS §4.1–§4.3): .NET 10.0.12 LTS (EOL 2028-11-14) ✅; `Mediator.Abstractions`/`Mediator.SourceGenerator` 3.0.2 MIT ✅; `Microsoft.EntityFrameworkCore` 10.0.12 MIT ✅; `Npgsql` / `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 PostgreSQL licence ✅; Dapper 2.1.89 Apache-2.0 ✅; `dbup-postgresql` 7.0.1 MIT ✅; `Corvus.Json.Validator` 5.6.1 Apache-2.0 ✅; FluentValidation 12.1.1 Apache-2.0 ✅; Cronos 0.13.0 MIT ✅; `AWSSDK.S3` 4.0.103.4 Apache-2.0 ✅; xunit.v3 4.0.1 Apache-2.0 ✅; Shouldly 4.3.0 BSD-3-Clause ✅; Testcontainers.PostgreSQL 4.15.0 MIT ✅; Respawn 7.0.0 Apache-2.0 ✅; `@angular/build` 22.2.0 peer `typescript >=6.0 <6.1` ✅; `typescript-eslint` 8.70.1 peer `typescript >=4.8.4 <6.1.0` ✅; `vitest` 5.0.1 MIT ✅. Rejected on licence: MediatR 14.2.0 (RPL-1.5), JsonSchema.Net 9.4.0 (maintenance-fee EULA), FluentAssertions 8.11.0 (non-commercial) ✅.

## 1. Purpose and scope

**Goal:** make every later plan buildable without re-deciding the ground. One solution and one npm workspace, one contract pipeline, one migration/tenant-context mechanism, one CQRS pipeline, one durable-task mechanism, one integration harness, one CI that gates itself.

**In scope:** the .NET solution (`Evidgate.slnx`, `src/`, `tests/`) plus npm workspaces for `apps/web` and `packages/*` per 00 §5.2; toolchain pinning (.NET 10 SDK via `global.json`, NuGet Central Package Management + lock files, Node 26 / TS 6.0.x for the TypeScript side); analyzers, format, type and architecture gates; OpenAPI 3.1 + JSON Schema contract-first pipeline (generated TS SDK, Corvus server validation, Ajv CLI validation with a shared parity corpus, served-document diff, contract tests); plain-SQL forward-only expand/contract migrations run by `Evidgate.Migrator` (DbUp 7) with owner vs app roles; the tenant scope (`ITenantScope`, parameterised `set_config`) shared by EF Core 10 and Dapper; the Mediator 3 CQRS pipeline (00 §5.5 C2); transactional outbox + own `task` queue + Cronos schedules; the `idempotency_record` store; Testcontainers.PostgreSQL integration harness; GitHub Actions CI for Evidgate itself; local dev stack including a licence-checked S3 emulator; NuGet + npm licence and vulnerability gates; ADR template in `docs/adr`; dogfooding (our CI results ingested into Evidgate from M2); the Sprint-1 skeleton of source §12.

**Out of scope:** product modules and their tables (02-08), web screens (09), deploy topology/observability/runbooks (10), customer CI onboarding (04), provider choice (10/O3).

**V1/V2/V3 split:** V1 = everything above except the fixture-promotion loop (V2, 01.10.1-T4) and the .NET 12 LTS / TypeScript 7 upgrade (V3, 01.1.1-T4).

## 2. Requirements

- **FR-01-1** A clean clone restores with `dotnet restore --locked-mode` and `npm ci`; `scripts/verify.sh` = build + analyzers + format check + unit + integration + architecture + contract drift for both ecosystems, and is what CI runs.
- **FR-01-2** .NET SDK 10.0.x is pinned by `global.json`; NuGet versions live only in `Directory.Packages.props` with committed `packages.lock.json`; Node 26 LTS and TypeScript 6.0.x are pinned for `apps/web` and `packages/*`; no floating range on a runtime dependency in either ecosystem.
- **FR-01-3** `contracts/openapi.yaml` (OpenAPI 3.1) is the only source of wire shapes; the TS SDK is generated from it, the server validates payloads against the same schema files, and the document served by `Evidgate.Api` is diffed against it; any drift fails CI.
- **FR-01-4** The event envelope v1 (00 §9.4) and adapter protocol v1 (00 §10) exist as versioned JSON Schemas in `contracts/`; the server (Corvus.Json.Validator) and the CLI (Ajv 8) are held to identical accept/reject behaviour by the shared corpus `fixtures/contracts/` run in both test suites (T42).
- **FR-01-5** Migrations are plain SQL, forward-only, expand/contract, applied by `Evidgate.Migrator` (DbUp 7) with a custom journal that stores name + checksum; DDL runs as the owner role, application traffic as a non-owner role without `BYPASSRLS` (F5). A migration that would break the previous app version is rejected in review.
- **FR-01-6** Every request/task runs in one explicit transaction opened by `ITenantScope.BeginAsync`, which issues the parameterised `set_config('evidgate.org_id', @org_id, true)` (and `evidgate.project_id` where relevant) per 00 §5.4.2; EF Core and Dapper use that connection and transaction; a command issued outside one throws (`DbCommandInterceptor`).
- **FR-01-7** Business foreign keys are composite `(org_id, <id>)` referencing `unique (org_id, id)`, which every business table declares (00 §5.4.1, §5.4.6, T3).
- **FR-01-8** An outbox row is written in the same transaction as the business change and moved into the `task` queue table at least once (A10); handlers are idempotent by a natural key, have a retry cap, and land in the `dead_letter` state visible to operators. Cron schedules fire idempotently through unique `(schedule_name, fire_at)` (00 §5.3).
- **FR-01-8a** Every command goes through the Mediator 3 pipeline in the fixed 00 §5.5 C2 order; the order, the "every handler declares a permission" rule and the module-boundary rules are asserted by `Architecture.Tests` (T47, T49).
- **FR-01-8b** `Idempotency-Key` handling (00 §9.1) is served by one `idempotency_record` store: key per `(org_id, project_id, operation)`, canonical request hash, stored response, ≥ 24 h expiry; same key + same hash → stored response, different hash → 409 `idempotency_conflict` (T11).
- **FR-01-9** Integration tests run against real PostgreSQL 18 through Testcontainers; the tenant-isolation suite is a required check (00 §11).
- **FR-01-10** Evidgate CI requires build, analyzers, format, unit, integration, architecture, contract drift, licence scan and vulnerability scan for **both** NuGet and npm on every PR; every dependency licence must allow closed-source commercial SaaS (DECISIONS §4, T40).
- **FR-01-11** The local dev stack is one command: PostgreSQL 18 + an S3-compatible emulator + api + worker + web, with no production data.
- **FR-01-12** Every irreversible choice made here has an ADR in `docs/adr`, referenced from the story that made it.
- **FR-01-13** From M2, Evidgate's own CI results are ingested into an Evidgate project, and from M3 they gate Evidgate's own pull requests.
- **NFR-01-1** `scripts/verify.sh` completes in under 10 minutes on CI ⚠️ (target to measure in 01.7.1-T1).
- **NFR-01-2** Options are validated at boot (`ValidateOnStart`) and the process exits non-zero on a missing/invalid `EVIDGATE__*` value; no secret is ever logged (00 §11, §5.7 S3).
- **NFR-01-3** A dependency that fails the licence rule fails the build; exceptions exist only as an ADR row with an owner.

**Acceptance criteria (testable):** a clean clone passes `dotnet restore --locked-mode && npm ci && scripts/verify.sh`; a hand-edited generated file fails CI; a hand-edited applied migration makes `Evidgate.Migrator --status` fail; a command outside `ITenantScope` throws; a handler without a permission attribute or a module referencing another module's non-`.Contracts` project fails `Architecture.Tests`; two dispatchers running concurrently produce one side effect; two workers firing the same schedule produce one task; the isolation suite fails if `FORCE ROW LEVEL SECURITY` is dropped from a business table; a GPL/AGPL/RPL dependency added to any NuGet or npm project fails the licence job; an event batch the CLI accepts is accepted by the server and vice versa for every corpus vector.

## 3. Key decisions

- **One modular monolith, two entrypoints** [DECISION: A1] — `Evidgate.Api` and `Evidgate.Worker` reference the same `src/Modules/*` projects; a module references another module only through its `.Contracts` project, enforced by project references and `Architecture.Tests` (01.2.1-T1, T47). Rejected: split services (premature distribution) and a single process (a slow webhook task would block ingest).
- **CQRS dispatch = `Mediator` 3.0.2** ✅ MIT, source-generated (compile-time registration, no reflection on the hot path). Rejected: MediatR 14.2.0 ✅ RPL-1.5/commercial (fails the licence rule); WolverineFx 6.40.0 ✅ MIT (runtime codegen + own envelope tables; more machinery than needed).
- **Data access = EF Core 10 (write model) + Dapper 2.1 (read model and hot paths) on one tenant-scoped connection** — EF gives change tracking and optimistic concurrency for aggregates; Dapper gives single-round-trip `unnest` inserts, keyset lists and `SKIP LOCKED` claims. Rejected: EF-only (hot-path SQL through `FromSql` loses clarity), Dapper-only (hand-written change tracking for aggregates).
- **SDK generator = `@hey-api/openapi-ts` 0.99.0** ✅ MIT, peer `typescript >=5.5.3 || >=6.0.0`; one generated package serves the CLI and `apps/web`. Rejected: `openapi-typescript` 7.13.0 ✅ MIT but peer `typescript ^5.x`. No C# client is generated: the server is the implementation, held to the contract by tests and the served-document diff.
- **Validation = Corvus.Json.Validator 5.6 on the server, Ajv 8 (2020-12) in the CLI, one parity corpus** (T42). Two engines cannot be "one shared module" across runtimes; identical behaviour is proven, not assumed. FluentValidation 12 handles command rules that JSON Schema cannot express. Rejected: JsonSchema.Net 9 (maintenance-fee EULA ✅), NJsonSchema 11 (partial 2020-12 ⚠️).
- **Canonical hashing = RFC 8785 in `Evidgate.Canonicalization`** (server) and `canonicalize` 5.1.0 ✅ Apache-2.0 (CLI), both run the same RFC 8785 test vectors (licence of the vector set checked in 01.3.2-T4 ⚠️); hashes written as `sha256:<hex>` per 00 §7. Rejected: sorted-key serialisation (ambiguous numbers/unicode).
- **Migration runner = `Evidgate.Migrator` on DbUp 7** ✅ MIT with a custom checksum journal (`IJournal`; DbUp's default journal stores names only ⚠️ — confirmed in 01.4.1-T1): ordered plain-SQL files, one transaction per file, `--status`/`--dry-run`. Rejected: EF Core migrations (C# migration classes, not the plain SQL DECISIONS §4 requires).
- **Local S3 emulator = SeaweedFS** ✅ Apache-2.0 (LICENSE read 2026-09-24). Rejected: MinIO ✅ AGPL-3.0; LocalStack Community — archived, free replacement non-commercial ✅. Fallback if presigned-URL parity (incl. signed `Content-Length`, T18) fails in 01.8.1-T1: Scality CloudServer ✅ Apache-2.0.
- **Licence gate = npm `license-checker` 25.0.1** ✅ BSD-3-Clause + a NuGet licence scanner (tool and its licence chosen in 01.7.1-T2 ⚠️) sharing one allow/deny list in `scripts/licence-policy.json` (deny AGPL/SSPL/BUSL/RPL/non-commercial/maintenance-fee EULAs; LGPL needs an ADR).
- **Logging = `Microsoft.Extensions.Logging` + OpenTelemetry .NET 1.19** ✅ Apache-2.0 with a redaction list covering every `EVIDGATE__*` secret and `authorization`; no secret ever becomes a metric label or log field (00 §11, §5.7 S3). No Serilog: one pipeline.
- **.NET 10 LTS + TS 6.0.x confirmed** by the matrix above; the .NET 12 LTS / TS 7 upgrade is a V3 task (01.1.1-T4); TS 7 waits for `@angular/build` and `typescript-eslint` peer ranges.

## 4. Design

**Tables owned (mechanism only; module tables belong to 02-08):**

| Table | Columns (high level) | Constraints |
|---|---|---|
| `schema_migration` | `name`, `checksum`, `applied_at`, `duration_ms` | PK `name`; sequential, never re-run |
| `outbox` | `id`, `org_id`, `aggregate_type`, `aggregate_id`, `task_type`, `payload jsonb`, `idempotency_key`, `created_at`, `dispatched_at` | `unique (org_id, id)`, `unique (org_id, idempotency_key)`; partial index on undispatched rows |
| `task` | `id`, `org_id` (null for platform tasks), `queue`, `task_type`, `payload jsonb`, `idempotency_key`, `schedule_name`, `fire_at`, `state` (`queued`/`active`/`retry`/`dead_letter`/`done`), `attempts`, `max_attempts`, `available_at`, `locked_until`, `last_error`, `created_at` | `unique (idempotency_key)`; `unique (schedule_name, fire_at)` where `schedule_name` is not null (00 §5.3); partial index `(queue, available_at) WHERE state IN ('queued','retry')`; platform table (no RLS; rows carry `org_id` and handlers open the tenant scope from it) |
| `idempotency_record` | `org_id`, `project_id`, `operation`, `key`, `request_hash`, `response_status`, `response_body jsonb`, `created_at`, `expires_at` | PK `(org_id, project_id, operation, key)`; RLS by `org_id`; purge by schedule after `expires_at` (T11) |

Schema `evidgate` also holds the NULL-safe helper functions the RLS policies read (`evidgate.current_org_id()`, `evidgate.current_project_id()`, T2), and the empty schema `evidgate_resolve` for pre-tenant `SECURITY DEFINER` resolvers (functions added by 02.6.1 and 03.4.1, T1), both created by migration as the owner role.

**Endpoints:** plan 01 owns no business endpoint. It owns the conventions and the skeleton of `contracts/openapi.yaml` (00 §9) plus operational endpoints outside `/v1`: `GET /internal/health/live`, `GET /internal/health/ready`, `GET /metrics` (see §13 CR-01-4).

**Flows:**
1. *Boot:* validate `EVIDGATE__*` options → build the app-role `NpgsqlDataSource` → check the migration journal head equals the build's expected head → start endpoints/consumers. Mismatch = refuse to start.
2. *Request/task:* the endpoint resolves the principal → `Mediator` pipeline (00 §5.5 C2): telemetry → validation → `ITenantScope.BeginAsync` (`BEGIN`, `set_config`) → authorization → idempotency → handler (EF Core / Dapper on the same connection) → outbox flush → `COMMIT`. The worker dispatcher later moves committed outbox rows into `task`; consumers claim `task` rows with `FOR UPDATE SKIP LOCKED` and open the tenant scope from the row's `org_id`.
3. *Contracts:* edit `contracts/` → `npm run codegen` (TS SDK) → both suites run the parity corpus → server contract tests + served-document diff → CI drift gate.

**States:** plan 01 introduces no business state; run/job/artifact/evidence states stay exactly as 00 §8 defines them. The `task` row state (`queued → active → retry → dead_letter → queued (replayed) | done`) is an internal state, not a business state.

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Migration fails midway | One transaction per file (PostgreSQL DDL is transactional ⚠️); ledger row written only on success; runner exits non-zero | No half-applied schema, deploy blocked |
| Applied migration edited | Checksum mismatch → runner refuses to start, `--status` shows the diff | History is immutable |
| Codegen not re-run after a spec edit | CI drift job fails the build | Spec is the single source of truth |
| Commit succeeded, dispatcher crashed before moving the row | Dispatcher re-reads undispatched rows on its next tick | At-least-once delivery (A10) |
| Same outbox row dispatched twice | `task.idempotency_key` unique + handler dedupe on its natural key | One side effect |
| Two workers fire the same schedule | Unique `(schedule_name, fire_at)` → second insert is `ON CONFLICT DO NOTHING` | One task per fire |
| Task exceeds retry cap | Row moves to `dead_letter` + alert (10.3.1-T5, runbook 10.7.1-T1) | No silent loss |
| App role attempts to bypass RLS | `FORCE ROW LEVEL SECURITY` (F5) + isolation suite | No cross-tenant path (00 §11) |
| Command issued outside the tenant transaction | `DbCommandInterceptor` throws in tests and in production | Tenant context is never implicit |
| Same `Idempotency-Key`, different body | `idempotency_record` hash mismatch → 409 `idempotency_conflict` | No double side effect (T11) |
| Server and CLI validators disagree on a vector | Parity corpus fails both builds | One wire contract (T42) |
| Emulator behaves unlike the provider (presigned URL, multipart, checksum headers) | 01.8.1-T1 acceptance hits the same code path as upload/complete; a provider smoke test is required before the pilot (10.6.1-T2) | Local green ≠ production green is made explicit |
| Dependency violates the licence rule | Licence job fails; either replace the dependency or add an ADR exception with an owner | Licence rule (DECISIONS §4) is enforced, not documented |
| Package/peer conflict after an upgrade (NuGet or npm) | Locked-mode restore + the compatibility matrix ADR fail CI and name the conflicting package | Upgrades are evidence-driven |

## 6. Security and privacy

- Secrets live only in the environment/CI secret store; `.env*` is git-ignored; the redaction list in the logger covers every `EVIDGATE_*` secret and `authorization`.
- The dev stack holds no real customer data (00 §11); dev roles mirror the production separation (owner/app/operator) so an RLS mistake fails locally first.
- CI: GitHub Actions pinned by commit SHA, OIDC where a cloud credential is needed (F3), no long-lived cloud key in the repo; published packages get provenance later (10.2.1-T2).
- Dependency surface: NuGet `packages.lock.json` + `--locked-mode` and npm `package-lock.json` + `npm ci`; licence gate for both (FR-01-10); `dotnet list package --vulnerable --include-transitive` and `npm audit --omit=dev` with a documented exception list; SBOM per build (T40).
- `npm ci` may run package install scripts and NuGet packages may ship MSBuild targets; the lock files plus review are the control, and a new package with install scripts or build targets needs a reviewer note (01.7.1-T4).
- SQL is parameterised everywhere, including `set_config` (00 §5.7 S1).
- Privacy: this plan stores no personal data; artifact handling and PDPL obligations are 06/11.

## 7. Test strategy

- **Unit (xUnit v3 + Shouldly; Vitest for TS):** canonical-JSON hashing golden vectors in both runtimes, Corvus/Ajv parity corpus, migration ordering and checksum journal, options validation failures, pipeline-behaviour order.
- **Integration (Testcontainers.PostgreSQL 4.15, PostgreSQL 18, Respawn between tests):** RLS with the app role; the interceptor throws outside a tenant scope; outbox row + business row commit atomically; concurrent dispatchers with `FOR UPDATE SKIP LOCKED` produce one side effect; two schedule fires → one task; retry cap → `dead_letter`; idempotency replay/conflict; expand/contract rename pair leaves the old code path working during the expand phase.
- **Architecture (`Architecture.Tests`):** module references only through `.Contracts`, `Domain` ↛ `Infrastructure`, every handler declares a permission, pipeline order C2 (T47, T49).
- **Contract:** every implemented handler's response validates against `contracts/openapi.yaml`; the served OpenAPI document equals the contract (normalised diff ⚠️ 3.1 output of `Microsoft.AspNetCore.OpenApi` confirmed in 01.3.2-T3); error-envelope and pagination examples match 00 §9.2/§9.1; the generated TS SDK compiles under TS 6.0.x `strict`.
- **CI failure tests (deliberate):** edited generated file, edited applied migration, AGPL/RPL dependency (NuGet and npm), missing `Idempotency-Key` on a side-effecting route, handler without a permission.
- **Fixtures:** `fixtures/` starts with one hand-written event batch, one manifest and the hashing vectors; producer fixtures arrive with 04.

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| A NuGet package used later has no .NET 10 build or fails the licence rule (e.g. commercial relicensing like MediatR) | Medium | Medium | Matrix task re-run for every new package (ADR-0002 is the register); CPM pins exact versions; the licence gate runs on every restore |
| Corvus and Ajv disagree on a 2020-12 edge case | Medium | Medium | Parity corpus in both suites (T42); schemas avoid exotic keywords |
| The own task queue grows into a second pg-boss | Medium | Medium | Scope fixed to 00 §5.3: claim, backoff, cap, dead letter, unique fire key; no priorities or fan-out until a plan needs them |
| Migration discipline erodes (destructive change in one release) | Medium | High | Expand/contract playbook + a rejected-change example in review; runner has no destructive mode |
| RLS is bypassed by a future admin path | Medium | High | `FORCE` policy on every business table + isolation suite (01.6.1-T4) |
| Licence gate becomes noise and gets ignored | Medium | Medium | Deny list is small and explicit; exceptions require an ADR row |
| Two-process skeleton tempts shared-process shortcuts (in-memory queues) | Medium | Medium | 01.5.1-T5 test asserts survival across a worker restart |
| Dev emulator drifts from the provider | High | Medium | Emulator parity is an acceptance criterion; provider smoke test before the pilot (10.6.1-T2) |
| Dogfooding slips and adapters are only tried by customers | Medium | High | 01.10 stories are V1 and land before the M3 pilot |

## 9. Epics, stories and tasks

### Epic 01.1 Stack compatibility and repository bootstrap (M0-M1)

#### Story 01.1.1 Toolchain and workspace — acceptance: `dotnet restore --locked-mode && npm ci` from a clean clone builds every project; the matrix ADR names each NuGet/npm package with its licence and compatibility evidence
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.1.1-T1 | Compatibility + licence matrix for every planned NuGet package (Mediator, EF Core/Npgsql, Dapper, DbUp, Corvus, FluentValidation, Cronos, AWSSDK.S3, OpenTelemetry, xUnit v3, Shouldly, NSubstitute, Testcontainers.PostgreSQL, Respawn) on .NET 10 and every npm package of `apps/web`/`packages/*` on Node 26 + TS 6.0.x → ADR-0002 (register for future packages) | S | V1 | M0 | — |
| 01.1.1-T2 | Pin toolchain: `global.json` (SDK 10.0.x, `rollForward: latestFeature` + `allowPrerelease: false`, ADR-0002), `Directory.Build.props` (nullable, `TreatWarningsAsErrors`, `AnalysisLevel`, `InvariantGlobalization` for Api/Worker), `Directory.Packages.props` (Central Package Management, exact versions), `RestorePackagesWithLockFile` + committed `packages.lock.json`; `.nvmrc`, `engines`, `typescript@6.0.x`, root npm workspaces `package.json`, committed `package-lock.json` | M | V1 | M0 | 01.1.1-T1 |
| 01.1.1-T3 | Skeleton exactly per 00 §5.2: `Evidgate.slnx`, `src/Evidgate.Api`, `src/Evidgate.Worker`, `src/Evidgate.Migrator`, `src/BuildingBlocks/*`, `src/Modules/*` + `.Contracts` pairs, `tests/*`, `apps/web`, `packages/*`, `contracts/`, `migrations/`, `fixtures/`, `docs/`, `scripts/`, `infra/` | M | V1 | M0 | 01.1.1-T2 |
| 01.1.1-T4 | .NET 12 LTS upgrade once GA and TypeScript 7.x once `@angular/build` (today peer `>=6.0 <6.1`) and `typescript-eslint` (today `<6.1.0`) accept it | XS | V3 | M5 | 01.1.1-T2 |

#### Story 01.1.2 Two-process skeleton — acceptance: Api and Worker start from one solution; a handler change is visible in both; `/internal/health/ready` is green only with database + migrations head
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.1.2-T1 | `Evidgate.Api` + `Evidgate.Worker` hosts from the same module projects: options binding with `ValidateOnStart` for `EVIDGATE__*`, ASP.NET Core health checks (Npgsql + journal head), graceful shutdown, logging with redaction, OpenTelemetry wiring | L | V1 | M1 | 01.1.1-T3 |
| 01.1.2-T2 | Module scaffolding: the eleven module project pairs of 00 §5.1 (`Evidgate.Modules.<Name>` + `.Contracts`), each `.Contracts` exporting one service interface; reference rules asserted in 01.2.1-T1 | S | V1 | M1 | 01.1.1-T3 |
| 01.1.2-T3 | Sprint-1 skeleton slice (source §12): manifest → run → planned execution → event batch → completeness stub → decision stub as Mediator commands/queries through real module boundaries, fakes only, no business rules | L | V1 | M1 | 01.1.2-T2 |

### Epic 01.2 Quality gates (M0-M1)

#### Story 01.2.1 Analyzers, format, types and architecture — acceptance: `scripts/verify.sh` fails on a module-boundary violation, an unformatted file, an un-awaited task, a nullable warning and a TS type error under `strict`
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.2.1-T1 | .NET analyzers (`latest-recommended`, security rules, `CA2007`/`VSTHRD`-style async rules ⚠️ package licence checked in 01.1.1-T1) + `Architecture.Tests` (xUnit): module refs only via `.Contracts`, `Domain` ↛ `Infrastructure`, no `public` types outside `.Contracts`/`Endpoints`, every handler declares a permission (T47); ESLint 10 flat config + typescript-eslint 8 for `apps/web`/`packages/*` with `no-floating-promises`, `--max-warnings 0` | M | V1 | M1 | 01.1.1-T3 |
| 01.2.1-T2 | `dotnet format --verify-no-changes` + Prettier 3 + `.editorconfig` + `format:check` in CI | S | V1 | M0 | 01.1.1-T3 |
| 01.2.1-T3 | Strict types: nullable reference types as errors across the solution; `tsc -b` across TS project references with `strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes` | M | V1 | M1 | 01.1.1-T3 |
| 01.2.1-T4 | Pre-commit hooks: husky 9 + lint-staged 17 on staged files (Prettier/ESLint; `dotnet format` on staged `.cs`) | XS | V1 | M1 | 01.2.1-T1 |
| 01.2.1-T5 | Commit/PR conventions: Conventional Commits, PR template, CODEOWNERS draft | XS | V1 | M1 | — |

### Epic 01.3 Contract-first pipeline (M0-M1)

#### Story 01.3.1 Spec and schemas — acceptance: `contracts/openapi.yaml` lints clean at warn severity and carries every convention of 00 §9.1/§9.2
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.3.1-T1 | `contracts/openapi.yaml` (OpenAPI 3.1) skeleton: `/v1` prefix, error envelope, cursor pagination, `request_id`, `Idempotency-Key`, `ETag`/`If-Match`, XSRF header (T38), the V1 endpoints of 00 §9.3 as documented stubs; spectral (Apache-2.0) lint gate | L | V1 | M0 | 01.1.1-T3 |
| 01.3.1-T2 | JSON Schemas: event envelope v1 (00 §9.4) + adapter protocol v1 (00 §10), versioned files with major/minor rules | M | V1 | M1 | 01.3.1-T1 |
| 01.3.1-T3 | Contract versioning + deprecation policy (breaking = major; incompatible adapter major rejected per 00 §10) + `contracts/README` | S | V1 | M1 | 01.3.1-T1 |

#### Story 01.3.2 Generated SDK, validators and contract tests — acceptance: hand-editing a generated file, a served-document drift or a validator disagreement fails CI
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.3.2-T1 | Generate the typed TS client + types into `packages/sdk` with `@hey-api/openapi-ts` (used by the CLI and `apps/web`), committed, deterministic; `npm run codegen` + `git diff --exit-code` drift gate | L | V1 | M1 | 01.3.1-T1 |
| 01.3.2-T2 | Validation in both runtimes: `Evidgate.Contracts.Validation` loads `contracts/` schemas once at boot with Corvus.Json.Validator (server; per-event indices, 00 §9.4); Ajv 8 (2020-12) + `ajv-formats` in the CLI; parity corpus `fixtures/contracts/` (accept/reject vectors) run by both suites, any disagreement fails both builds (T42) | L | V1 | M1 | 01.3.1-T2 |
| 01.3.2-T3 | Contract tests: implemented responses validated against the spec; the document served by `Microsoft.AspNetCore.OpenApi` diffed against `contracts/openapi.yaml` (normalised; ⚠️ confirm 3.1 output, fallback = response validation only); error-envelope and pagination fixtures; drift fails CI | M | V1 | M1 | 01.3.2-T1 |
| 01.3.2-T4 | Canonical JSON hashing: `Evidgate.Canonicalization` (RFC 8785, `sha256:<hex>` per 00 §7) + `canonicalize` 5.1 in the CLI, both running the RFC 8785 reference vectors (vector licence checked ⚠️) and golden vectors for manifest/policy/candidate/evidence | M | V1 | M1 | 01.1.1-T3 |

### Epic 01.4 Database foundation and CQRS pipeline (M1)

#### Story 01.4.1 Migrations, roles and RLS — acceptance: migrations apply as owner, the app role cannot bypass RLS, an edited applied migration stops the Migrator
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.4.1-T1 | `Evidgate.Migrator` on DbUp 7: ordered plain-SQL files from `migrations/`, one transaction per file, forward-only, custom checksum `IJournal` on `schema_migration`, `--status`/`--dry-run`, checksum-mismatch refusal, exit codes for the deploy gate (10.2.2-T1) | M | V1 | M1 | 01.1.1-T3 |
| 01.4.1-T2 | Roles: owner (DDL), app (not owner, no `BYPASSRLS`), operator (audited), plus the local bootstrap script | L | V1 | M1 | 01.4.1-T1 |
| 01.4.1-T3 | RLS baseline: NULL-safe `evidgate.current_org_id()`/`current_project_id()` (`current_setting(…, true)` + `NULLIF`, T2), `FORCE ROW LEVEL SECURITY` template for every business table, empty `evidgate_resolve` schema with `EXECUTE`-only grants pattern (T1), bypass/isolation test | L | V1 | M1 | 01.4.1-T2 |
| 01.4.1-T4 | Expand/contract playbook + a fixture migration pair proving a zero-downtime column rename against the Migrator | M | V1 | M1 | 01.4.1-T1 |

#### Story 01.4.2 Tenant scope, data access and pipeline — acceptance: a command outside `ITenantScope` throws; a business FK without `org_id` or a table without `unique (org_id, id)` is rejected by the convention test; pipeline order C2 is asserted
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.4.2-T1 | Tenant scope: app-role `NpgsqlDataSource`, `ITenantScope.BeginAsync(orgId, projectId?)` opening one connection + `BEGIN` + parameterised `set_config(…, true)` (00 §5.4.2, §5.7 S1); per-module EF Core 10 `DbContext` bound to that connection/transaction (`UseTransaction`, snake_case naming, no lazy loading, `version` concurrency token); Dapper on the same connection; `DbCommandInterceptor` that throws without the tenant setting (C3) | XL | V1 | M1 | 01.4.1-T2 |
| 01.4.2-T2 | Concurrency helpers: `FOR UPDATE SKIP LOCKED` claim helper over Dapper (F6) + composite `(org_id, <id>)` FK and `unique (org_id, id)` conventions with a schema test that rejects violations (00 §5.4.1, §5.4.6, T3) | M | V1 | M1 | 01.4.2-T1 |
| 01.4.2-T3 | Column conventions: app-generated UUIDv7 ids (`Guid.CreateVersion7()` ⚠️ RFC 9562 ordering confirmed by test), `created_at`/`updated_at` (`timestamptz` UTC), `version` optimistic concurrency, `duration_ms` from runner clocks; helper + test | S | V1 | M1 | 01.4.2-T1 |
| 01.4.2-T4 | Mediator 3 pipeline behaviours in the 00 §5.5 C2 order (telemetry → validation via FluentValidation → tenant transaction → authorization through 02's permission interface → idempotency via 01.5.1-T6 → handler → outbox flush → commit); `Result` → 00 §9.2 envelope endpoint filter (C8); read-only transaction for queries (C1); architecture test for the order (T49) | L | V1 | M1 | 01.4.2-T1, 01.5.1-T2, 01.5.1-T6 |

### Epic 01.5 Outbox and task queue (M1)

#### Story 01.5.1 Durable internal tasks — acceptance: commit-then-crash loses no task; double dispatch yields one side effect; two schedule fires yield one task; retry cap lands in `dead_letter`
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.5.1-T1 | `task` queue table + `Evidgate.Worker` consumer host: batch claim with `FOR UPDATE SKIP LOCKED`, `available_at` exponential backoff with jitter, `locked_until` visibility timeout, per-queue concurrency limits, graceful shutdown; Cronos schedule runner that inserts fires with unique `(schedule_name, fire_at)` + `ON CONFLICT DO NOTHING` (00 §5.3; no leader) | L | V1 | M1 | 01.4.2-T1 |
| 01.5.1-T2 | `outbox` table + write helper that joins the tenant transaction (00 §5.3) with `idempotency_key` unique per `(org_id, idempotency_key)` | M | V1 | M1 | 01.4.2-T1 |
| 01.5.1-T3 | Dispatcher: claims undispatched outbox rows with `FOR UPDATE SKIP LOCKED` and inserts the matching `task` rows in the same transaction (natural idempotency key), marks dispatched; concurrency-safe | M | V1 | M1 | 01.5.1-T1, 01.5.1-T2 |
| 01.5.1-T4 | Handler contract (`ITaskHandler<T>`) + retry cap + `dead_letter` state + operator routes `/internal/operator/tasks/dead-letter` list/replay (00 §9.1) + alert hook for 10.3.1-T5 | M | V1 | M1 | 01.5.1-T3 |
| 01.5.1-T5 | Integration tests on Testcontainers.PostgreSQL: crash between commit and dispatch, duplicate dispatch, concurrent schedule fires, retry-cap → dead-letter, replay | M | V1 | M1 | 01.5.1-T4 |
| 01.5.1-T6 | `idempotency_record` table + store: canonical request hash (01.3.2-T4), stored status/body, ≥ 24 h `expires_at`, replay vs 409 `idempotency_conflict`, purge schedule (T11) | M | V1 | M1 | 01.4.2-T1, 01.3.2-T4 |

### Epic 01.6 Test harness (M1)

#### Story 01.6.1 Integration harness and fixtures — acceptance: `dotnet test` on the integration projects boots PostgreSQL 18 in a container, applies migrations once, and fails on any cross-tenant access
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.6.1-T1 | Test conventions: xUnit v3 + Shouldly + NSubstitute projects (`<Project>.UnitTests`, `<Module>.IntegrationTests`), deterministic order, no network by default, coverage ⚠️ tool licence checked in 01.1.1-T1; Vitest 5 configuration for `apps/web`/`packages/*` | S | V1 | M1 | 01.1.1-T3 |
| 01.6.1-T2 | Testcontainers.PostgreSQL harness for PostgreSQL 18 (image pinned by digest) as an assembly fixture: Migrator applied once, Respawn reset between tests, app-role connection with RLS on, `TenantTestScope` helper | L | V1 | M1 | 01.4.1-T2 |
| 01.6.1-T3 | `fixtures/` layout + loaders for .NET and TS + golden-file assertion helper (raw producer report → expected normalised output), ready for 04's fixtures | M | V1 | M1 | 01.6.1-T2 |
| 01.6.1-T4 | Tenant-isolation suite: cross-tenant read, write and id-guessing attempts over API, EF Core write and Dapper read paths must fail (00 §11) as a required check | M | V1 | M1 | 01.6.1-T2 |
| 01.6.1-T5 | Playwright 1.63 workspace for `apps/web` E2E: config, one smoke test, CI job wiring (screens are 09) | S | V1 | M1 | 01.1.1-T3 |

### Epic 01.7 CI and licence gate (M1)

#### Story 01.7.1 Evidgate's own pipeline — acceptance: a PR with a compile error, a drifted contract, an AGPL/RPL dependency (NuGet or npm) or an unformatted file is blocked by required checks
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.7.1-T1 | GitHub Actions pipeline: .NET 10 SDK + Node 26 setup, `dotnet restore --locked-mode`, `npm ci`, build, analyzers, unit, integration (Docker on the runner ⚠️ confirm), architecture, contract drift, web build; NuGet/npm caching, concurrency cancel | L | V1 | M1 | 01.6.1-T1, 01.6.1-T2 |
| 01.7.1-T2 | Dependency licence gate for both ecosystems: npm `license-checker` + a NuGet licence scanner (tool choice and its own licence ⚠️ in this task) over one `scripts/licence-policy.json` allow/deny (AGPL/SSPL/BUSL/RPL/non-commercial/paid-maintenance EULA denied per DECISIONS §4); runs on every PR and lock-file change; exception = ADR row (T40) | M | V1 | M1 | 01.7.1-T1 |
| 01.7.1-T3 | Branch protection on `main`/`develop`: required checks, CODEOWNERS, PR template enforcement | S | V1 | M1 | 01.7.1-T1 |
| 01.7.1-T4 | Supply chain: Actions pinned by commit SHA, Dependabot for NuGet + npm, `dotnet list package --vulnerable --include-transitive` and `npm audit --omit=dev` gates with a documented exception list, CycloneDX SBOM for both ecosystems ⚠️ tool licence (T40) | M | V1 | M1 | 01.7.1-T1 |

### Epic 01.8 Local dev stack (M1)

#### Story 01.8.1 One-command local stack — acceptance: `scripts/dev/up.sh` on a clean machine starts Api, Worker, web, PostgreSQL 18 and the S3 emulator, and a presigned PUT/GET round-trip succeeds
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.8.1-T1 | S3 emulator licence check and decision: SeaweedFS (Apache-2.0 ✅) chosen; MinIO rejected (AGPL-3.0 ✅); Scality CloudServer (Apache-2.0 ✅) fallback, LocalStack rejected (archived; free tier non-commercial); acceptance = presigned PUT/GET via `AWSSDK.S3` with SigV4, checksum header **and signed `Content-Length`** (oversize PUT rejected, T18) matching the provider path 06 uses; ADR-0004 | S | V1 | M1 | — |
| 01.8.1-T2 | Compose dev stack: PostgreSQL 18 (digest-pinned) + emulator; Api/Worker via `dotnet watch`, web via `ng serve`; healthchecks, named volumes | M | V1 | M1 | 01.8.1-T1 |
| 01.8.1-T3 | Dev seed as `Evidgate.Migrator seed-dev` (refuses non-dev environments): one org, one project, memberships from the 02 roles catalogue, one sample policy revision; `scripts/dev/up.sh` + `docs/dev-setup.md` quickstart | M | V1 | M1 | 01.8.1-T2 |

### Epic 01.9 ADRs and engineering docs (M0-M1)

#### Story 01.9.1 Decision records — acceptance: every choice in §3 has an ADR with alternatives and a dated status, referenceable by id
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.9.1-T1 | `docs/adr/` template + index + rule (one ADR per irreversible choice, reviewed before merge) | S | V1 | M0 | — |
| 01.9.1-T2 | ADRs 0001-0006: modular monolith + two processes (A1), compatibility/licence matrix + upgrade trigger, Migrator + role split, S3 emulator, licence allow/deny policy (NuGet + npm), CQRS pipeline + data access (Mediator vs MediatR, EF Core + Dapper, own task queue vs pg-boss/Hangfire) | M | V1 | M1 | 01.9.1-T1 |
| 01.9.1-T3 | Docs conventions: where runbooks (10), module READMEs, `contracts/README` and feature plans live; link check in CI | S | V1 | M1 | 01.9.1-T1 |

### Epic 01.10 Dogfooding (M2-M3)

#### Story 01.10.1 Evidgate gates Evidgate — acceptance: a real `main` build and a PR run appear in Evidgate with the same fixtures a customer would use, and a FAIL blocks the PR once 03/07 land
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 01.10.1-T1 | Emit our own CI results in the ingest format: xUnit v3 JUnit XML report (⚠️ format checked here), Vitest and Playwright JUnit → artifact set produced by the same adapter code as customers (04, T48) | M | V1 | M2 | 01.7.1-T1 |
| 01.10.1-T2 | Ingest our own runs: CI exchanges the GitHub OIDC credential (F3) for a short-lived ingest token and pushes runs for `main` and PRs | M | V1 | M2 | 01.10.1-T1 |
| 01.10.1-T3 | Gate our own pull requests on the Evidgate decision (required check; "missing evidence never yields PASS" is exercised weekly) | M | V1 | M3 | 01.10.1-T2 |
| 01.10.1-T4 | Fixture promotion loop: parser errors/warnings and mapping gaps found by dogfooding become `fixtures/` cases and 04 issues | S | V2 | M4 | 01.10.1-T3 |

## 10. Dependencies

- **02-identity-tenancy:** the roles/permissions catalogue consumed by the dev seed (01.8.1-T3) and the pipeline's authorization behaviour (01.4.2-T4); RLS helpers and the `evidgate_resolve` schema (01.4.1-T3) are shared with 02's tenancy epic, which adds the resolver functions (02.6.1-T3).
- **04-ingestion-adapters:** consumes `contracts/` event schema + both validators and the parity corpus (01.3.1-T2, 01.3.2-T2), the fixture loader (01.6.1-T3) and produces the dogfooding reports (01.10.1-T1).
- **05-runs-orchestration:** uses the outbox/dispatcher/task queue (01.5), the idempotency store (01.5.1-T6) and the claim helper (01.4.2-T2); its runner-job leasing tables stay domain logic, not the internal task queue (00 §5.3).
- **10-operations:** consumes the CI pipeline (01.7.1-T1), the Migrator and role split (01.4.1), the dev stack (01.8.1-T2) and the alert hook (01.5.1-T4); needs the layout addition of §13 CR-01-3.
- **11-security-compliance:** inherits the tenant-isolation suite (01.6.1-T4) as evidence, the NuGet + npm supply-chain gates (01.7.1-T2/T4) and the secret handling rules of §6.

## 11. Open questions

- **[OPEN]** Cloud provider/region (O3, owner) — blocks nothing in M0-M1 but decides how the emulator parity check (01.8.1-T1) and the provider smoke test (10.6.1-T2) are run. Needed by M2.
- **[OPEN]** Does the licence gate also run in the published CLI/adapter release path (shipped to customer CI), or only in the repo? Owner: Tech Lead, needed by M4 (CLI publish).
- **[OPEN]** ⚠️ GitHub-hosted runners' Docker availability for Testcontainers is assumed, not verified; 01.7.1-T1 confirms it or moves integration tests to a self-hosted runner. Needed by M1.
- **[OPEN]** Whether `scripts/` in §13 CR-01-3 is the final home for repo tooling, or each tool becomes its own project. Owner: Tech Lead, needed by M1.

## 12. Estimate summary

Units: person-days of one senior full-stack .NET + Angular developer (00 §6, CR-D-1).

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 01.1 Stack compatibility and repository bootstrap | 7.0 | 0 | 0.25 | 7.25 |
| 01.2 Quality gates | 3.0 | 0 | 0 | 3.0 |
| 01.3 Contract-first pipeline | 9.5 | 0 | 0 | 9.5 |
| 01.4 Database foundation and CQRS pipeline | 12.5 | 0 | 0 | 12.5 |
| 01.5 Outbox and task queue | 7.0 | 0 | 0 | 7.0 |
| 01.6 Test harness | 5.0 | 0 | 0 | 5.0 |
| 01.7 CI and licence gate | 4.5 | 0 | 0 | 4.5 |
| 01.8 Local dev stack | 2.5 | 0 | 0 | 2.5 |
| 01.9 ADRs and engineering docs | 2.0 | 0 | 0 | 2.0 |
| 01.10 Dogfooding | 3.0 | 0.5 | 0 | 3.5 |
| **Total** | **56.0** | **0.5** | **0.25** | **56.75** |

→ with 20 % buffer **68.1** person-days

## 13. Contract change requests

> **Status:** Applied 2026-09-24: CR-01-1/2 → DECISIONS §4; CR-01-3/4 → 00 §5.2, §9.1.
> 2026-09-25 CR-D-1 + TL review: stack migrated to .NET 10 + CQRS (DbUp Migrator, EF Core + Dapper on `ITenantScope`, Mediator pipeline, own outbox + task queue with unique fire keys, xUnit/Testcontainers.PostgreSQL) with the TypeScript side kept for web and customer packages; rulings T11, T18, T40, T42, T47, T48, T49 applied (F-01-1..8). Tasks: +01.4.2-T4 (L), +01.5.1-T6 (M); resized 01.4.1-T1 L→M, 01.4.2-T1 L→XL, 01.5.1-T1 M→L, 01.5.1-T3 L→M (net +3.0 raw). CR-01-2's TS 7 trigger now reads `@angular/build` + `typescript-eslint` peers (DECISIONS §4.2).

| # | Target | Request (verbatim) | Owner |
|---|---|---|---|
| CR-01-1 | DECISIONS §4, object storage row | Record the chosen local emulator: **SeaweedFS** (Apache-2.0 ✅, LICENSE read 2026-09-24); no dev-only licence exception is needed because MinIO (AGPL-3.0 ✅) is rejected; Scality CloudServer (Apache-2.0 ✅) is the documented fallback if presigned-URL parity fails (01.8.1-T1); LocalStack is rejected (Community repository archived, free tier non-commercial ✅). | Tech Lead |
| CR-01-2 | DECISIONS §4, language row | Replace "Move to 7.x when the lint toolchain supports it (CR in plan 01)" with an explicit trigger: TypeScript 7.x only after `typescript-eslint` peer range includes 7 **and** `@nestjs/swagger` accepts it (today: `typescript-eslint@8.70.1` peer `typescript >=4.8.4 <6.1.0` ✅; `@nestjs/swagger@12.0.2` peer `typescript ^5.5.0 \|\| ^6.0.0` ✅); tracked as 01.1.1-T4. | Tech Lead |
| CR-01-3 | 00 §5.2 repository layout | Add two entries: `scripts/` (repo tooling: codegen drift, licence scan, migration runner CLI, ops CLI, dev seed) and `infra/` (compose files, Dockerfiles, OTel collector config, dashboards, alert rules — owned by plan 10). | Tech Lead |
| CR-01-4 | 00 §9.1 general API conventions | Add: operational endpoints live outside `/v1` and are not part of the public contract (`GET /internal/health/live`, `GET /internal/health/ready`, `GET /metrics`), are never included in `contracts/openapi.yaml`, and require no tenant scope. | Tech Lead |
