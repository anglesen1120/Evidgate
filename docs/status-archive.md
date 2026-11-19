# Status archive

Delivered and closed STATUS.md text, moved verbatim; never edited.

## 3. Wave 4 (delivered on `feature/m1-wave4`; merged PR #9)

Scope: the 01.4.2 T2/T3 conventions and the 01.5.1 durable-task tables that 01.4.2-T4 (pipeline, wires `AddEvidgateTenancy` into Api/Worker) depends on, the shared integration harness (01.6.1-T2, needed by 01.7.1-T1 `ci.yml`) and the Playwright workspace. Two wave-3 follow-ups ride along in slice A.

Gates per slice, in order: implement → QA agent (separate from the author) → code review + performance review + security review (parallel) → fixes re-tested → commit.

| Slice | Tasks | Size | Implement | QA | Code review | Perf review | Security review |
|---|---|---|---|---|---|---|---|
| A | 01.4.2-T2.2 `SchemaConventionTests`, 01.4.2-T3 column conventions; fix 1 (explicit names rejected); fix 2 (version-trigger check); + UUIDv7 `id` value generator (QA-A-05) | S + S (+ fixes) | ✅ | ✅ | ✅ | ✅ | ✅ |
| B1 | 01.5.1-T2 `outbox` + `OutboxWriter`; 01.5.1-T6.1 `idempotency_record` + `IIdempotencyStore` + purge function | M + S | ✅ | ✅ | ✅ | ✅ | ✅ |
| B2 | 01.4.2-T2.1 `SKIP LOCKED` claim helper; 01.5.1-T1 `task` queue + consumer + Cronos schedules; 01.5.1-T6.2 purge schedule | S + L + S | ✅ | ✅ | ✅ | ✅ | ✅ |
| C | 01.6.1-T2 Testcontainers harness (`tests/Foundation.IntegrationTests`) | L | ✅ | ✅ | ✅ | ✅ | ✅ |
| D | 01.6.1-T5 Playwright workspace (`apps/web`) | S | ✅ | ✅ | ✅ | ✅ | ✅ |
| E | Node 26.10 `JSON.parse` repro on other Node versions; `SESSION-SUMMARY.md` status trimmed to a pointer to this file | — | ✅ | — | — | — | — |

QA findings fixed in wave: scan false-negatives (positional composite-FK pairing, disabled/column-scoped version triggers, allow-list pin), `task_active_idx` for the reclaim poll (seq scan → 0.2 ms at 100k rows), sweep payload clamps (purge limit 1..10000, sweep window ≥1 day), harness namespace/doc corrections. `scripts/verify.sh` green after all of it: 305 tests incl. Docker, all stages pass.

Code/perf/security review fixes (parallel reviewers, all re-tested, `verify.sh` green): monotonic (not host-clock) lease-expiry check in `TaskConsumer.ExecuteAsync`; `DeadLetterExpiredLeases` always overwrites `last_error` with the lease cause instead of `coalesce`; `SchemaConventionTests` narrows the `public.task` exemption to TenantKey/CompositeForeignKey/RowLevelSecurity only (AuditColumns/VersionTrigger stay checked — task has both); misleading test comment fixed (`TaskQueueWriterTests`); `task_claim_type_idx` partial index added (0008) so an unrunnable task_type backlog can't force a sequential scan on every poll (measured 5.61 ms + Seq Scan at 100k → 1.01 ms with the index); `MarkDoneAsync` now bounded by the same 2 s `TransitionWriteBudget` as the failure transition; `WorkerSchedDataSource` validates the DSN's role at construction (fails at boot naming the key, never echoes the credential); `task`/`outbox` gain `pg_column_size(payload) <= 65536` plus matching writer-side length/size validation (queue/task_type/schedule_name/idempotency_key/payload); `IdempotencyStore.CompleteSql` adds `and org_id = @OrgId` as defence in depth alongside RLS; `ITaskHandler.HandleAsync`'s contract doc now states the no-secrets-in-exception-message rule (`last_error`/logs store the first line verbatim). Deferred, recorded in §6: idempotency purge's 50k-rows/day drain ceiling (needs a time-budget or restartable cursor).

Shared prerequisites committed first (`06e47b5`): `tests/Foundation.IntegrationTests` and `tests/Tasks.IntegrationTests` registered in `Evidgate.slnx` and `scripts/check-layout.sh`; `Cronos` and the Canonicalization project referenced from `BuildingBlocks.Persistence`; `Tenancy.IntegrationTests/MigrationTests` bounded to files 0001–0005 so new migrations do not change it. Migration numbers reserved: `0006_platform_outbox.sql`, `0007_platform_idempotency_record.sql` (B1), `0008_platform_task_queue.sql` (B2).

Not in this wave, now ready next (§5): 01.5.1-T3 dispatcher, T4 retry/dead-letter/operator routes, T5 crash tests; 01.4.2-T4 pipeline. Still blocked: 02.1.1-T2 org/project API (unrelated to this wave).

## 3.5. Wave 5 (delivered on `feature/m1-wave5`; merged PR #10)

Scope (STATUS §5 ready + plan_graph deps verified): **01.4.2-T4** Mediator pipeline + endpoint filter + `AddEvidgateTenancy` wiring (L); **01.5.1-T3** outbox dispatcher (M); **01.5.1-T4** handler contract/retry cap/dead letter/operator routes (M, needs T3); **01.5.1-T5** crash + operator suites on the harness (M, needs T4); **01.7.1-T1** real `ci.yml` (L — deletes interim `docker-tests.yml`, applies its deferred fixes: `global-json-file`, `ubuntu-24.04`, single trigger).

Gates per slice, in order: implement → QA agent (separate from the author) → code review + performance review + security review (parallel) → fixes re-tested → commit.

| Slice | Tasks | Size | Implement | QA | Code review | Perf review | Security review |
|---|---|---|---|---|---|---|---|
| P | 01.4.2-T4: C2 pipeline behaviours (telemetry → validation → tenant tx → authorization → idempotency → outbox flush), `Result` → §9.2 endpoint filter, pipeline-order arch test, `AddEvidgateTenancy` + mediator into Api/Worker | L | ✅ | ✅ | ✅ | ✅ | ✅ |
| D | 01.5.1-T3: outbox dispatcher (batch claim `SKIP LOCKED` 200, `INSERT … SELECT` task rows `idempotency_key='outbox:'‖id`, `dispatched_at` in one tx), dispatch metrics + crash test; migration `0009` dispatcher grants | M | ✅ | ✅ | ✅ | ✅ | ✅ |
| C | 01.7.1-T1: `ci.yml` (SHA-pinned actions, `ubuntu-24.04`, `global-json-file`, caches, `concurrency`, least-privilege `permissions:`), `verify.sh --stage`, `check-ci-parity.sh`, drift gate scaffold, Playwright job non-blocking, `docker-tests.yml` deleted | L | ✅ | ✅ | ✅ | ✅ | ✅ |
| H | 01.5.1-T4: retry→dead-letter semantics + redacted `last_error` + `ITaskAlertSink` + `task_dead_letter_total`; `ListDeadLetterTasksQuery` + `ReplayDeadLetterTask` under `/internal/operator/*` behind a fail-closed `IOperatorGate` (deny-all default until 02.x); no `/v1` route, no migration 0010 needed (app-role read path) | M | ✅ | ✅ | ✅ | ✅ | ✅ |
| T | 01.5.1-T5: AC1–AC7 end-to-end suites on the 01.6.1-T2 harness | M | ✅ | ✅ | ✅ | ✅ | ✅ |

Dependency order: P, D, C in parallel → H (needs D's dispatcher + P's scope) → T (needs H). Migration numbers reserved: `0009` outbox dispatcher grants (D), `0010` operator read path if needed (H).

Carried items whose owner task lands in this wave: the `task`/`outbox` operator read path (STATUS §6 row "Audited operator read path for `task`/`outbox`"); `docker-tests.yml` deletion (§6 → C).

QA defects fixed in wave: Worker host missing `AddEvidgatePipeline` (QA-P D1 — every Mediator dispatch would have run bare), `IdempotencyBehaviour` missing the `ResultContract` guard its siblings carry (QA-P D2), `task_dead_letter_total` moved into `TaskAlertNotifier` so a custom `ITaskAlertSink` can't silence it (QA-H H-1), empty `queue=` param normalised to no-filter (QA-H H-3). Review fixes: `dotnet format` whitespace (IdempotencyBehaviour switch arms — would have failed the format gate), dispatcher queues added to `TaskQueueHostedService.QueuePlan` (dispatched rows were stamped but never claimed — a silent inert chain), `outbox_undispatched` gauge capped at 100k + 5 s command timeout (was a linear seq-scan risk on the metrics thread), pooled connection released before alert-sink notify (a slow sink held a pool slot), `task.last_error` secret masking (`Password=`/bearer/`token=` patterns — mirrors SecretRedactor's regex set), main app DSN pinned to `evidgate_app` (mirrors the sweep-DSN guard), `ci.yml` job `timeout-minutes` added. `scripts/verify.sh` green after all of it: all 17 stages, full test suite incl. Docker.

Verified on GitHub after merge: run 36251021746 (PR) + 36252017788 (develop push) — all 4 jobs green, dotnet job 247 s < 10 min; AC-level deferrals closed.


## 3.6. Wave 6 (delivered on `feature/m1-wave6`; merged PR #11)

Scope (STATUS §5 ready + plan_graph deps verified): area-01 M1 remainder — **01.3.2-T1** generated TS SDK (L); **01.3.2-T2** dual-runtime validators + parity corpus (L); **01.3.2-T3** contract tests + served-doc diff (M, after T1); **01.6.1-T3** `fixtures/` layout + loaders (M); **01.6.1-T4** tenant-isolation suite (M); **01.7.1-T2** licence gate (M); **01.7.1-T3** CODEOWNERS/PR template (S — branch-protection API half is [blocked]: 403 "Upgrade to GitHub Pro or make this repository public", owner decision recorded §6); **01.7.1-T4** supply-chain hardening (M). Not in this wave: 01.8.1-T3 seed-dev (still needs 02.x/07.1.1 tables), 01.10.1 (M2), 01.1.1-T4 (M5).

Evidence landed since wave 5: `ci.yml` first runs green on PR #10 (dotnet job 247 s < 10 min budget; run 36251021746, develop push 36252017788) — closes the 01.7.1-T1 deferred AC rows.

Dependency order: S (SDK) ∥ V (validators) ∥ F (fixtures) ∥ I (isolation) ∥ G (CI governance) in parallel → K (contract tests, needs S's SDK + the parity corpus format). Prereqs landed by orchestrator before dispatch: `@hey-api/openapi-ts` 0.99.0, `license-checker-rseidelsohn` 5.0.1, `@cyclonedx/cyclonedx-npm` 6.0.1 (root devDeps), `ajv`/`ajv-formats` into `packages/cli`, dotnet tools `nuget-license` 4.0.18 + `CycloneDX` 6.2.0 (`.config/dotnet-tools.json`), ADR-0002 rows for all of them.

Gate order per slice: implement → QA agent → code/perf/security reviews → fixes re-tested → commit.

| Slice | Tasks | Size | Implement | QA | Code review | Perf review | Security review |
|---|---|---|---|---|---|---|---|
| S | 01.3.2-T1: `@hey-api/openapi-ts` codegen config (deterministic), `packages/sdk/gen/` output, real `npm run codegen`, `scripts/check-codegen-drift.sh`, header-injection hooks, golden-file check | L | ✅ | ✅ | ✅ | ✅ | ✅ |
| V | 01.3.2-T2: `Evidgate.BuildingBlocks.Contracts.Validation` (Corvus, §9.2 `details[]` mapping, endpoint filter), `packages/cli` Ajv validator, `fixtures/contracts/` corpus + verdicts, `tests/Contract.Tests` ParityCorpusTests + `packages/cli` parity spec | L | ✅ | ✅ | ✅ | ✅ | ✅ |
| F | 01.6.1-T3: `fixtures/<domain>/<name>/` + `meta.json` contract, `tests/Shared/FixtureLoader.cs`, `packages/test-fixtures` loader + golden canonical-JSON helper | M | ✅ | ✅ | ✅ | ✅ | ✅ |
| I | 01.6.1-T4: isolation suite in `tests/Isolation.Tests` — EF read/write, Dapper read, API id-guessing via real route, planted-violation meta-test | M | ✅ | ✅ | ✅ | ✅ | ✅ |
| G | 01.7.1-T2 (`licence-policy.json`, `licence-scan.sh` both ecosystems, licence job) + T3.2 (PR-template/CONTRIBUTING polish) + T4 (`dependabot.yml`, `audit-deps.sh` + `exceptions.json`, CycloneDX SBOM artifacts) | M+XS+M | ✅ | ✅ | ✅ | ✅ | ✅ |
| K | 01.3.2-T3: `tests/Contract.Tests` response-vs-spec + normalised `Microsoft.AspNetCore.OpenApi` served-doc diff vs `contracts/openapi.yaml` (3.1.1 emission confirmed; diff allows only the recorded `/v1/slice/run` dev surface) | M | ✅ | ✅ | ✅ | ✅ | ✅ |

Contract for slices: `verify.sh`/`ci.yml`/`check-ci-parity.sh`/`check-adr-matrix.sh`/`scripts/check-layout.sh`/`Evidgate.slnx`/`STATUS.md`/`AGENTS.md`/`docs/adr/*` are orchestrator-owned; slices produce their own scripts (`scripts/check-codegen-drift.sh`, `licence-policy.json`, `licence-scan.sh`, `audit-deps.sh`, `exceptions.json`, `.github/dependabot.yml`) which the orchestrator wires into `verify.sh` stages (new stage names: `check-codegen-drift`, `licence-scan`, `audit-deps`) + `ci.yml` after wave A. New test project `tests/Contract.Tests` is created once by V (csproj skeleton + slnx + check-layout registration); K adds files only. `packages/test-fixtures` belongs to F. The `Isolation_ApiGet_*` tests may add a test-only route inside `tests/` hosts if no tenant read endpoint exists yet — mark clearly, real `/v1` routes inherit the check when 02.x lands.

Outcome (2026-09-27): all `verify.sh` stages green (`/tmp/verify-w7-final3.log`), 614 .NET tests incl. Identity.IntegrationTests (81), Contract.Tests (123), Hosts.IntegrationTests boot self-check (21). QA found and the wave fixed: `invited_email` normalisation, keyset cursor and replay-assertion gaps, self-check blind spots (wrong role, `using(true)`, select-only, unscoped sibling policies), a stale generated SDK. Reviews found and the wave fixed: a dead SQL constant and duplicated edge helpers (now `EdgeInputs`), a cross-org 404 test that targeted a random id, the three-roots loader rationale; perf: `membership_role_role_idx` for the last-admin guard (252→2 rows read); security: the self-check predicate matcher accepted OR-widened policies once parentheses were stripped (now structural, shared with `RlsPolicyCoverageTests`), `role.kind` so staff/service roles can't be granted to members, `role_permission` insert/update tied to the role's org, self-escalation refused on grant/invite/create/update role, and `forbiddenValues` keeping `EVIDGATE__ISOLATION__SELFCHECK=Disabled` out of staging/prod. Carried: W7-SEC-06 (02.6.2), guard skew, 10.2.1-T1.4 (see §6).

Pre-merge review (2026-09-27, commit `5cfcc28`): an independent authz and RLS-matcher re-review of the post-review fixes found and the wave fixed: a self-grant onto a pending invite for the caller's own address, the service-path divergence between `ResolveAsync` and the evaluator, catalogue tables skipping the OR-widening sibling guard in `IsolationSelfCheck`, the coverage lint accepting a tenant policy scoped only by WITH CHECK, and two `check-env-matrix` gaps (enum declared after its options class, untyped declared values). `verify.sh` green: 620 .NET tests.

Outcome (2026-09-27): all 21 `verify.sh` stages green (`/tmp/w6-verify3.log`) — 9 .NET test assemblies incl. Contract.Tests (60), Foundation.IntegrationTests (34), Isolation.Tests (13); vitest 119 tests across workspaces; licence-scan 693 packages (0 deny, 2 MPL warns covered by ADR-0002); audit-deps 0 findings; SBOMs npm 598 + nuget 133 components. QA found and the wave fixed: TS loader duplicate-key hole, drift-gate precondition wording, `licence-report.json` missing `--report`, unwritten filter tests. Reviews found and the wave fixed: O(n²) error dedup + 100-row `truncated` marker (both runtimes, CWE-770), self-leaf composite keywords (`not`/`contains`/`propertyNames`/…) no longer dropped to `IsValid=true`, `Flag`-before-`Detailed` accept path (13.8 MB→48 B per 1 MiB body), schema dir resolved by upward search not content root, `JsonSchema` compiled with `allowFileSystemAndHttpResolution:false` end to end, `sbom.sh -dpr` (19 s→0.9 s, no unlocked lockfile rewrite) + vacuous-BOM refusal, licence/audit empty-inventory fail-closed + ADR-id format+existence on exception keys, `.config/dotnet-tools.json` now an ADR-matrix source, flow-style/quoted `uses:`/`run:` keys rejected in the pin and parity gates, `persist-credentials:false` + `npm ci --ignore-scripts` everywhere in ci.yml. Recorded deviations: `packages/sdk/gen` compiles strict minus `exactOptionalPropertyTypes` (generator-side TS2379, noted in the 01.3.2 plan §11); TS fixture loader uses a raw-text scanner for duplicate-key parity.

## 3.7. Wave 7 (delivered on `feature/m1-wave7`; merged PR #12)

Scope (STATUS §5 ready + `plan_graph.py` deps verified): **02.2.1-T1** permission catalogue (M); **02.6.1-T1** RLS role/helper/policy assertions + boot self-check + `contracts/rls-exceptions.txt` (L; roles, helpers and the org/project policies already landed in 01.4.1/02.1.1); **04.7.1-T1** adapter protocol schemas + corpus (M); **05.1.1-T1** manifest schema + corpus (L); **10.1.1-T1** environment model + config matrix (M); **10.2.1-T1.1–T1.3** container images + SBOM (T1.4 web bundle/CDN needs 09.1.1 and object-storage publishing — carried); **10.8.1-T1** provider criteria memo (S); **11.1.1-T2** DoR security checklist (S); **02.9.1-T2** composite FK suite (M); then **02.1.2-T1** membership (M) and **02.2.1-T2** authorization evaluator (M). Not in this wave: 03.1.1-T1 (T1.4 needs 02.2.1-T2 — next wave), 04.1.2-T4 (FK to 04.1.x `producer_slot` parent not in graph), 02.4.1-T2, 11.2.1-T1, 11.8.1-T1/T2, 12.x.

Dependency order: P ∥ R ∥ CM ∥ E ∥ G ∥ D ∥ F → B (membership needs P's `role`; evaluator needs membership). Migration numbers: `0010` P (`identity_permission_catalogue`); R needed no migration, so B took `0011` (`identity_membership`). C and M share the two schema loaders (`ContractSchemaSet.cs`, `packages/cli/src/validate.ts`), so one slice owns both.

Gate order per slice: implement → QA agent → code/perf/security reviews → fixes re-tested → commit.

| Slice | Tasks | Size | Implement | QA | Code review | Perf review | Security review |
|---|---|---|---|---|---|---|---|
| P | 02.2.1-T1: `contracts/permissions.json`, generated C# `Permissions` + drift check, `0010` catalogue tables + seed, `GET /v1/permissions`, `GET /v1/roles` | M | ✅ | ✅ | ✅ | ✅ | ✅ |
| R | 02.6.1-T1: role/GUC/forced-RLS assertion tests, `contracts/rls-exceptions.txt`, boot self-check refusing startup | L | ✅ | ✅ | ✅ | ✅ | ✅ |
| CM | 04.7.1-T1 (`contracts/adapter/` capability/config/operations + corpus + README) and 05.1.1-T1 (`contracts/manifest/` + corpus + `ManifestUpload` body) | M+L | ✅ | ✅ | ✅ | ✅ | ✅ |
| E | 10.1.1-T1: environment model, `EVIDGATE_*` matrix + lint, `infra/<env>/` probes, `docs/runbooks/environments.md`, `/internal/*` ingress rule, smoke | M | ✅ | ✅ | ✅ | ✅ | ✅ |
| G | 10.2.1-T1.1–T1.3: Api/Worker/Migrator images, CycloneDX per image, SBOM↔lock consistency | S+S+S | ✅ | ✅ | ✅ | ✅ | ✅ |
| D | 10.8.1-T1 criteria memo + shortlist; 11.1.1-T2 DoR checklist + PR-template questions | S+S | ✅ | ✅ | ✅ | ✅ | ✅ |
| F | 02.9.1-T2: composite FK suite + negative fixtures | M | ✅ | ✅ | ✅ | ✅ | ✅ |
| B | 02.1.2-T1 membership schema/API/contracts; 02.2.1-T2 `RequirePermission` + evaluator | M+M | ✅ | ✅ | ✅ | ✅ | ✅ |

Contract for slices: `verify.sh`/`ci.yml`/`check-ci-parity.sh`/`check-layout.sh`/`Evidgate.slnx`/`STATUS.md`/`AGENTS.md`/`docs/adr/*`/`migrations/README.md` are orchestrator-owned; slices report new scripts, packages and migration notes, and the orchestrator wires them. `contracts/openapi.yaml` is shared by P (paths) and CM (`ManifestUpload`): each re-reads before editing, and nobody runs `npm run codegen` — the orchestrator regenerates the SDK once after wave A. New test project `tests/Identity.IntegrationTests` (shared harness linked) is created and registered by the orchestrator.

## 4. Wave 3 (delivered, PR #6)

Every task passed the gates in order: implement → QA agent (separate from the author) → code review + performance review + security review (parallel) → fixes re-tested → commit.

| Task | Size | Depends on | Implement | QA | Code review | Perf review | Security review |
|---|---|---|---|---|---|---|---|
| 01.4.2-T1 Tenant scope + EF/Dapper binding + interceptor | XL | 01.4.1-T2 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 01.3.2-T4 Canonical JSON hashing (.NET + CLI) | M | 01.1.1-T3 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 01.6.1-T1 Test conventions (.NET + Vitest) | S | 01.1.1-T3 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 02.1.1-T1 `organization` / `project` schema + migrations | M | 01.4.1-T3 | ✅ | ✅ | ✅ | ✅ | ✅ |

Shared prerequisites already applied on the branch:

- `vitest` 5.0.1 (root dev) and `canonicalize` 5.1.0 (`packages/cli`) installed exact and registered in ADR-0002.
- New test projects pre-registered in `Evidgate.slnx` and `scripts/check-layout.sh`: `tests/Canonicalization.UnitTests`, `tests/Persistence.IntegrationTests`, `tests/Tenancy.IntegrationTests`; `vitest.config.base.mts` on the root allow-list.

Implementation result (2026-09-26): `scripts/verify.sh` green on the host with Docker after all QA and review fixes — **218/218 .NET tests** (88 before the wave), plus tsc -b + the new `tsc -p` spec stage, eslint, prettier, layout, ADR matrix, links, contract conventions, npm `vitest` suites (21 tests across 6 files in 5 workspaces).

| Task | Delivered | Deviations to confirm in review |
|---|---|---|
| 01.4.2-T1 | `src/BuildingBlocks/Persistence/Tenancy/` (`ITenantScopeFactory.BeginAsync`, `ITenantDb` Dapper path, `ITenantDbContextFactory<T>`, `TenantCommandInterceptor`, `TenantScopeMissingException`, `AddEvidgateTenancy`); `tests/Persistence.IntegrationTests` (11 tests at implementation) | Read-only via `set transaction read only` as first statement (Npgsql has no read-only `BeginTransaction`); the probe showed PG 18.6 accepts a _late_ `SET TRANSACTION READ ONLY`, so the T95 rationale was corrected in 00 §15, the 01.4.2 plan and the `TenantScope` comment on 2026-09-26 (requirement unchanged); not yet wired into Api/Worker (01.4.2-T4) |
| 01.3.2-T4 | `JsonCanonicalizer` + `CanonicalHash` (`sha256:<hex>`); `packages/cli/src/canonical.ts`; shared vectors `fixtures/canonical/`; RFC 8785 vectors (cyberphone, Apache-2.0) | — |
| 01.6.1-T1 | `tests/Directory.Build.props` (central test packages, no-network guard for `*.UnitTests`), `vitest.config.base.mts` + per-workspace configs, `npm run test` | Guard blocks `HttpClient`/proxy-honouring processes only, not raw sockets/Npgsql (documented in `tests/README.md`); `apps/web` runs on `node` env until jsdom is added (09.1.1). QA fixes: the base config is `vitest.config.base.mts` (Node's native config loader never remaps a `.js` specifier onto an `.mts` source), the root `vitest.config.ts` lists the workspaces as `test.projects` so a bare `npx vitest run` cannot collect `apps/web/e2e/*.spec.ts`, and `verify.sh` gained a `tsc -p` stage over every `tsconfig.spec.json` because nothing type-checked the specs before |
| 02.1.1-T1 | `migrations/0004_identity_organization.sql`, `0005_identity_project.sql`; Tenancy entities, EF configurations, Dapper SQL, DTOs; `tests/Tenancy.IntegrationTests` (10 tests at implementation) | Review fix (code review #3): the write-model test now builds its model through the production path — a context derived from `EvidgateDbContext` with `UseTenantModel`, so 01.4.2's `TenantModelConventions` are exercised — and asserts the snake_case column/key/index contract, the `version` concurrency token and the application-generated `id` across the whole model (it previously used a bare `DbContext` that applied only the module configurations). RLS policies created now (plan §4 defers to 02.6.1-T1), `ENABLE` + `FORCE` plus a permissive `evidgate_owner` policy (FORCE applies to the owner too) and deliberately **no** `TO evidgate_operator` policy — a permissive org-scoped read policy OR-combines with T76's grant-gated one and would defeat the support-grant gate, so 02.8.1-T1.1 owns it; project PK `id` + `unique (org_id, id)`; quota columns deferred to 02.1.1-T3; `contracts/openapi.yaml` lacks Organization/Project schemas (02.1.1-T2) |

**Notes carried into next tasks** (recorded 2026-09-26 by the wave-3 fix pass, reworded against the tree after the tenancy and canonicalization fixes landed; each row names the task that has to act on it):

| Note | Owner |
|---|---|
| `organization`/`project` deliberately carry **no** `TO evidgate_operator` policy (0004:100-105, 0005:73-76): a permissive org-scoped read policy OR-combines with T76's grant-gated one and would defeat the support-grant gate, so the operator's `select` grant reads zero rows until the grant-gated `evidgate.operator_grant_ok` form lands | 02.8.1-T1.1 |
| Org creation must seed the new UUIDv7 as `evidgate.org_id` **before** the `CreateOrganization` insert: 0004's app policy is `for all … using (id = evidgate.current_org_id()) with check (id = evidgate.current_org_id())`, so an insert as `evidgate_app` passes the check only under a scope opened for the id being inserted | **Resolved 2026-10-03** (02.1.1-T2) |
| `contracts/openapi.yaml` has no `Organization`/`Project` component schemas **and no `POST /orgs` at all**; `createProject` (1005-1040) sends an inline `{name}` with no `slug` — although `project.slug` is NOT NULL with `project_slug_format_check` — and answers inline `{project_id}`, not the `ProjectDto` shape, so the contract cannot see either DTO | **Resolved 2026-10-03** (02.1.1-T2) |
| Slug grammar `[OPEN]`: the shipped check `^[a-z0-9][a-z0-9-]{1,62}$` accepts a trailing hyphen (`ab-`); the owner decides whether to tighten it before the validator pins it | **Resolved 2026-10-03** (02.1.1-T2) |
| Node 26.10.0 can mis-decode control-escape member names in a warm `JSON.parse` (ADR-0002 WARN row). The CLI's self-consistency guard withholds 64–78 of 27,146 generated documents as a partial mitigation; a `jsCanonical` recorded in a long-lived process must be recomputed in a fresh one before it is trusted | Node upgrade / 01.3.2 |
| Keyset split **landed**: `TenancyReadSql.ProjectsFirstPage` carries no cursor predicate and `ProjectsNextPage` compares `(created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)` with both parameters required, so the comparison stays an `Index Cond`; the single `@cursor_created_at is null or …` statement it replaced showed a residual `Filter` under a generic plan (measured 110x: 2.93 ms → 0.026 ms at 25 000 rows in on 50 000). The 02.1.1 plan §"Recorded" note still describes the residual-Filter shape | 02.1.1-T2 statement budget |
| Canonicalizer cost **landed** and still to be recorded in the plans: duplicate detection now compares each sorted name with its predecessor and the per-object `HashSet` with its `char[]`/string copies is gone (`JsonCanonicalizer.cs:198`) — the perf review measured 12.5 MB/op → 8.3 MB/op for a 1 MiB record doc and 35.8 MB → 19.1 MB for 100 000 single-member objects on that variant. Nested member values are still re-parsed once per enclosing level (3.0x the flat cost: 0.92 MB in 47.8 ms vs 16.0 ms — a deliberate CPU-for-memory trade), hashing a `JsonElement` re-materialises the text (+3.1 MB per MiB) and the CLI round-trip guard costs 2.44x (8.26 → 20.15 ms) | 01.3.2 §8 |
| `version` bump **landed** as a database-owned trigger: `public.set_updated_at_and_version()` (created in 0004, `EXECUTE` granted to `evidgate_app`) sets `updated_at = now()` and `version = old.version + 1`, attached as a `before update` trigger on `organization` (0004) and `project` (0005), and `TenantModelConventions` maps `version` as `ValueGenerated.OnUpdate` so EF reads the server's value back. 00 §5.5 C4 and §7 still read as `IsConcurrencyToken` alone | 00 §7/§5.5 C4 wording |
| `delete` is revoked from `evidgate_app` on `organization` and `project` (0004:127, 0005:96) because 0001's default privileges grant DELETE on every table the owner creates in `public`; `migrations/README.md`'s role table and the `0003` copy block (items 5 and 7) still prescribe the old operator policy and the DELETE grant for new tables | Tech Lead / next table author |
| Per-request EF context creation measures 0.109–0.140 ms and 56.2 KiB (the first command on a fresh context +0.55–0.7 ms) — noise against the p95 < 1 s ingest budget, but one line in the 01.4.2 plan keeps a future handler from creating a context per row | 01.4.2 |


## Closed carried items (from STATUS.md §6)

| Item | Blocker | Owner |
|---|---|---|
| OIDC grant accepts `workflow_run` (from S17, 11.2.1-T1) | **Closed 2026-10-27 by F50 (merged #238, f59ec29; F50 has no §2 task row, so no task or person-day count changes).** The server refuses `event_name` `pull_request_target` and `workflow_run` (default `DeniedEventNames`, `CiAuthOptions.cs:67-73`; check `GitHubOidc.cs:133-138`) and requires the `event_name` claim: a token that omits it, or sends it empty or blank, answers the same `token_invalid` 401, so omitting the claim no longer skips the deny list. Comparison is ordinal and case-exact, as GitHub sends lowercase values. A `workflow_run` of the allowed workflow file on `main` still passes the binding ref/workflow allowlist, so this server-side event check is the control, with the workflow file content (lint rule R1, branch protection) as defence in depth | owner-decided 2026-10-27 (D-S108 §2.7, approved — coordinator recommendation accepted; analysis in `docs/security/ingest-token-review.md` §2.7): deny `workflow_run` by default and require the `event_name` claim — add `workflow_run` to the `DeniedEventNames` default in code and in the configuration matrix, and add `event_name` to `Extract`'s required claims so the deny list cannot be bypassed by omitting it; long-term an event allowlist with per-binding audited opt-in. Delivered by F50 (backend, T3, HIGH priority, before pilot data; its matrix refuses `workflow_run`, refuses a missing, empty or blank `event_name`, keeps `push`/`workflow_dispatch`/`pull_request`/`merge_group`/`schedule`/`release` minting, pins the ordinal case-exact comparison and pins that an explicit override replaces the default — `CiAuthTests.cs:216-220,248-263,269-278,280-297`); the long-term event allowlist with per-binding audited opt-in is handed on as the STATUS §6 row `Event allowlist replaces the deny list (F50 follow-up (c))`. Hand-over Notes: `plans/features/11-security-compliance/11.2.1-a-fork-pull-request-cannot-reach.md:112` |
| run.current_evidence_revision_id composite FK (05.3.1-T1 hand-over, from S41) | Closed by PR #239 (S115, 05.3.2-T5): composite FK `run_current_evidence_revision_fk` and the pointer guards are in `migrations/0049_orchestration_evidence_revision.sql`; proved by `RunPointer_MovesToTheLatestRevision_AndGetRunReportsIt` and `RunPointer_RefusesStaleCrossRunCrossOrgAndNull` (`tests/Tenancy.IntegrationTests/EvidenceRevisionSchemaTests.cs:256`, `:288`). Row stays in place only so STATUS line cites stay valid. | receiver: none, closed by this PR; `plans/features/05-runs-orchestration/05.3.1-run-state-machine.md:125` |
| `ReconcileRunTask` consumer (05.3.2-T1 hand-over, S51) | Closed by 05.3.2-T2.3/T2.4 (#257, S123): `AddTaskHandler<RunReconcilePayload, ReconcileRunTaskHandler>` registers `RunReconcileContract.OutboxTaskType` (`src/Modules/Evidgate.Modules.Orchestrator/OrchestratorModule.cs:79`), so the rows finalize already queued become claimable, and the terminal-run rule holds: a trigger for a `complete`, `incomplete` or `canceled` run answers `NotCollectable` from the run-lock statement alone — no slot list and no event aggregate run (`RunReconciler.cs:41-45`) — and the handler returns before any finisher call at `ReconcileRunTask.cs:79-82`, so no `evidence_revision` is appended after the run completed (`Reconcile_AfterCommit_SecondTriggerNotCollectableAndNoSecondRevision`, `Reconcile_NonCollectable_NotCompared`). AC3's "missing reason recorded" half holds for a shard whose slot never closed only once `collection_deadline_at` has passed: the attempt then finishes `incomplete` with `missing_shard`, but nothing enqueues a reconcile at the deadline yet, so the trigger has to arrive from elsewhere (05.3.2-T3.1, the collecting-deadline row below). Row stays in place only so STATUS line cites stay valid. | receiver: none, closed by #257; hand-over Notes: `plans/features/05-runs-orchestration/05.3.2-finalize-and-reconciliation.md:178` |
| Admin session revoke committed before its audit row and carried no target user (F48 PARTIAL, S118, D-S102-Q3; CLOSED by F55 in S120) | Closed by #251 (S120, F55, merged as `c4c9600d`): `DELETE /v1/auth/sessions/{s}` with `X-Evidgate-Org` now writes the `auth.session_revoke_admin` row INSIDE `evidgate_resolve.session_revoke_one` (`migrations/0050_identity_session_revoke_audit.sql`), so the revoke and its audit row commit or roll back together and the S118 gaps are gone — no 503 after a committed revoke, no retry 404 with a missing row, no client-abort window. The row carries what the partial lacked: `target_user_id` metadata (the definer resolves the target now), actor = the admin, object = `session` / the revoked id, and a chain entry hashed in SQL exactly as `AuditHashing.CanonicalFields` does, so `AuditChainVerifier` still passes over a chain that mixes the app writer and this definer. S118's handler-side `IIdentityAuditWriter.WriteSessionRevokedAsync` write, its detached 10 s timeout and its `RequestAborted` window go with the writer (`SessionEndpoints.cs` `RevokeOneAsync` no longer takes the audit writer; the action string lives only in the migration and taxonomy row 65). A lock timeout (55P03) or any other fault inside the definer rolls the revoke back and answers 503 `unavailable` with the session still active, which the `DELETE` description now states. Self revoke, logout and revoke-all still write no `audit_log` row (security_event under 11.5.1-T5, D-S102-Q3). Row stays in place only so STATUS line cites stay valid. | receiver: none, closed by #251; F48 is whole, and since neither F48 nor F55 is a plan task row there is no §2 row and no count change; hand-over Notes: `plans/features/02-identity-tenancy/02.3.2-sessions-and-revocation.md:185` |
| `EVIDGATE__PARSE__*` consumers list `api` although only the Worker registers the runner (S121) | Resolved by 06.3.6-T1.4 (S125): the Api now genuinely reads `EVIDGATE__PARSE__OUTOFPROCESS` (`CompleteArtifactUpload` enqueues `artifact_parse` only when it is on), so `["api","worker"]` is the true consumer set and the rows stay as they are; the Api still never registers `IParseRunner` | closed in 06.3.6-T1.4 (S125, #262) |
| Reconciler acceptance on the merge head (S123) | Closed: the three reconciler projects ran at the merged head 8a12963b — after the N1 narrowing commit 1a7a6abe and the develop merge through #250 (e037f2fe) — through the built runner under `locked-run.sh`, with build and format stages exit 0: `ReconcileRunTests` 51 passed / 0 failed (37 facts/theories, 51 expanded cases, including the 8 added during the fix round: `Reconcile_OpenShardBeforeDeadline_Pending`, `Reconcile_OpenShardBeforeDeadline_ThenClosingShardCompletes`, `Reconcile_OpenShardPastDeadline_IncompleteWithMissingShard`, `Reconcile_OpenShardsBeforeDeadline_PendingUntilLastShardCloses`, `Reconcile_PolicyRequiresUnknownKind_IncompleteWithArtifactMissing`, `Reconcile_UnsatisfiableRequirementSet_IncompleteWithArtifactMissing`, `Reconcile_TwoConcurrentAttempts_AppendExactlyOneRevision`, `Reconcile_DuplicateMissingCodes_StoredOnce`), `ReconcileComparerUnitTests` 46/0 (including the 4 `RequiredSet_*` bound cases) and `PolicyContractsTests` 12/0. The earlier QA run at 94ff7db3 (43/0, 42/0, 12/0) covered the smaller suite and the two delta reviews ran nothing, so 8a12963b is the first full-suite evidence on the reconciler; the same projects re-ran at the head on top of 8a12963b that carries that case: `ReconcileRunTests` 52 passed / 0 failed (38 facts/theories, 52 expanded cases, including `Reconcile_PolicyRequiredKindReady_WritesCompleteRevision` at `ReconcileRunTests.cs:2016`, 1/0), `ReconcileComparerUnitTests` 46/0, `PolicyContractsTests` 12/0, `Architecture` `ModuleBoundariesTests` 8/0, build and format exit 0. Nothing is carried from this row. | closed (evidence above, no action); hand-over Notes: `plans/features/05-runs-orchestration/05.3.2-finalize-and-reconciliation.md:216` |
| Web test flake under load: DataTableComponent R6 (S115, observed in 3 of 4 full-gate vitest runs, passes alone) | `apps/web/src/app/shared/ui/data-table/data-table.component.spec.ts:204` expects `aria-rowindex` 402 after `viewport.setRenderedRange` and gets 2 when the machine is loaded (other lanes' Docker gates); no apps/web change in S115. Unowned; the 09.1.1 data-table owner should await a rendered-range change instead of one `whenStable`. | receiver: coordinator queue F56 (09.1.1 shared-ui owner picks it up) |
| `smoke-clean-build.sh` fails on 25 `Foundation.IntegrationTests` without Docker (S121, pre-existing on develop) | `scripts/smoke-clean-build.sh` runs `verify.sh` with `EVIDGATE_VERIFY_SKIP_DOCKER=1`, which filters `Category=Docker`; 25 `Foundation.IntegrationTests` (for example `FixtureLoaderTests.FixtureLoader_RoundTrip_Sample`) have no Docker trait but build `Evidgate.Testing.DatabaseFixture` as an assembly fixture, so they fail with `DockerUnavailableException`. Reproduced on a clean worktree of origin/develop `6306d38f` (2157 tests, 25 failed, rc 2) and on S121 head `6c271d8b` (2282 tests, 25 failed, same 25); the S121 diff does not touch `tests/Foundation.IntegrationTests` or `tests/Shared`. Fix options: trait the 25 tests `Category=Docker` or make the fixture lazy | receiver: 01.1.1-T3 (skeleton and clean-build smoke, AC1 of 01.1.1) |
| Web a11y remainder: T1.3 + T1.4 and onboarding findings (09.8.1-T1, S135, PR #284) | S135 landed 09.8.1-T1.1 (findings list `docs/a11y/findings-09.8.1-T1.md`) and T1.2 (keyboard/focus fixes + walkthroughs) as a declared partial. Open: T1.3 (contrast + target size in `shared/ui` tokens; no finding row yet, proven by the 09.8.1-T2 axe pass), T1.4 (error identification; F-ONB-01..03), onboarding focus findings F-ONB-04..10 (subtree owned by S128 in this wave), failure triage and evidence viewer not auditable until built. | receiver: 09.8.1-T1 remainder (T1.3 + T1.4); F-ONB-04..10 the next onboarding lane; 09.4.1 and 09.3.1-T3 for the unbuilt flows; Notes at the end of the 09.8.1 plan |
| ADR-0010 N2/N3: verifier-owned store (S131, #270) | The verifier keeps the highest `checkpoint_seq` outside the database and bucket (Decision 8) but no custody, backup or restore rule exists for that store. The high-water must be the highest signature-verified number (an unsigned object must not raise it), and Decision 6 does not say how `prev_checkpoint_hash` continues across revoked or unproven checkpoints. | receiver: 11.5.2-T2 and 11.5.2-T3; recorded in the 11.5.2 plan Notes |
| ADR-0010 N4: exporter bucket permissions (S131, #270) | Decision 3 gives the exporter `PutObject` only on `audit-checkpoints/v1/`, but the plan derives `checkpoint_seq` from the highest bucket object and `prev_checkpoint_hash` needs the previous document, so it needs List and Get on that prefix (or local state). | receiver: 11.5.2-T2; recorded in the 11.5.2 plan Notes |
| ADR-0010 Node ECDSA verification (S131, #270) | Verifying the ECDSA P-256 checkpoint signature from Node is not checked (DECISIONS F17 ⚠️); ADR-0010 Decision 9 makes it a blocking check for accepting the ADR, and the signature encoding is open. | receiver: 11.5.2-T3 (verifier); recorded in the 11.5.2 plan Notes |
| Merge debt (S136 plans 13.5.1 + 13.5.2) | Delta review of the final head left plan-text residuals, none a contradiction between the two plans on activation (closed): 13.5.1 AC5/AC2 vs the route table (`409`, `environment_missing`), `generated_script_step` has no `evidence_artifact_id`/`project_id` column and the composite FKs omit `project_id` (13.1.1 offers `unique (org_id, project_id, id)`), the unbound-reason abort set is wider than two codes and `origin_refused` vs `origin_not_allowed` is undefined, server-side screenshot opt-in limit (OI-13-524), seed file handling (never uploaded, logged or embedded; size cap), `locator_name`/`reason` secret scan, T1.5 Done-when needing T1.6 and leftover `binding_source = model` wording in T3.2 and the §5 `MapUnboundStepsCommand` shape, AC20 names 05.2.2 where §4 says 05.2.1, 13.5.2 namespace note omits OI-13-544 | receiver: 13.5.1-T1 and 13.5.2-T1 DoR review before build (owner confirmation of the OI-13-510 to OI-13-544 rows is required first); recorded in the 13.5.1 and 13.5.2 plan Notes and `plans/open-items/05-product-phases-g1.md` | S136, PR #281 merged as cdecef28 |
| Delta residuals (S139 plans 13.6.1 + 13.8.1, T0 delta at eb5588dc; #289, 8f8e365e) | Open after the one fix round, none changes an estimate: X1 13.6.1 exposes no read of the failing-step (red) profile that 13.8.1 stores as `failing_profile_id` (add `FailedStepAsync` or put the failing row in `candidates[]`); X2 13.8.1-T2.2 mint wiring depends on the T3.1 `drift_proposal` table and the T4.2 `shadow→open` promotion (move it into T4.2 or state the edge); X3 STATUS rows 13.6.1-T1 and 13.8.1-T1 list dependencies per story, not per task (13.5.1-T2 is a T1 need, 08.3.1-T1/08.8.1-T1/13.1.2-T1 are T2-T4 needs); X4 `permissions.json:54-96` should read `:54-91` in four places of 13.6.1; X5 stale ids `T4.2b` and `T3.2/b` in 13.8.1 section 11 and route 4 row; X6 reason code spelled `similarity_version_unsupported` and `unsupported_similarity_version`, 13.6.1 `requires_backfill` unmapped; X7 two Done-when test names missing from the 13.8.1 section 9 table; X8 13.6.1 names 13.5.1-T2 as the owner of the capture call but 13.5.1-T2 is locator policy and lint (name T1/T5 or add a receiver row). | receiver: X1, X6 13.6.1-T3 and 13.8.1-T1 DoR; X2, X5, X7 13.8.1-T2/T4 DoR; X3 the build lane that flips the STATUS rows; X4 first 13.6.1 build task; X8 13.5.1-T1/T5 build lane; Notes at the end of each plan |
| Stale security_event does-not-exist wording (S137) | Present-tense "table does not exist" text after `0053`: `scripts/check-rls.py:126-127` (guarded by `scripts/tests/test_check_rls.py:238`), `src/BuildingBlocks/Persistence/Audit/VerifyAuditChainTask.cs:53-55`, `plans/features/11-security-compliance/02.7.1-audit-trail.md:39`, the 11.9.1 plan `:150`, plan 11.5.1 `:101`. Gate scripts were out of scope for S137. | receiver: queue F64 (the lane allowed to edit `scripts/check-rls.py` fixes the script and its test; plans by their owners) |
| Cosmetic nits from the 11.5.1-T5 final delta (S137) | 4a: `tests/Isolation.Tests/Api/IsolationRouteCases.cs:2406` comment says a FORCE-RLS table hides every row from a tenant-less owner; false since the owner backfill policy of `0053` (the query runs in the tenant scope, so behaviour is right). 4c: a keyset comment sits above the owner-policy comment in `0053`. 4d: `0053` has no `drop ... if exists` on the tenant policy, indexes or table (harmless, one-apply migrator). jev triage: 4a false_positive 0.59, 4c/4d valid_defer. | receiver: queue F64 (cosmetic, none blocks); any lane that next touches `0053` or `IsolationRouteCases.cs` Closed S140 (#292, a9c8b246): 4a fixed; 4c/4d won't-fix, applied migration `0053` is immutable. |
| Plan tooling drift (F47, S130, #268) | Found while re-summing F47 and NOT committed there: (1) `python3 scripts/regen_docs.py` on develop rewrites `plans/features/README.md` away from the committed text (§5 wave count for 13.2.2 34 -> 23, 13.2.2 added to the blocks cells of 02.7.1/08.5.2/08.5.3, the `Checked 2026-10-26 against scripts/plan_graph.py --json` line deleted, which shifts every README line below it) and rewrites the 00 §14 calendar-check sentence; (2) `python3 scripts/plan_graph.py` prints `max wave` 34 or 37 between identical runs (set-order nondeterminism). Receiver: F37 (plan tooling and feature-plan consistency pass; `scripts/**` is outside the docs lanes). Until then regen_docs.py output must be applied hunk by hunk. |
| ADR-0010 N5/N6: wording and checkpoint shape (S131, #270) | N5: Decision 13 and the residual-risk table overstate detection of tail truncation between two checkpoints and imply that pinning a forged row detects it. N6: the 02.7.1 gap note omits the `key_id` prefix of Decision 1; the checkpoint shape in Decision 7 lacks the tombstone (`deleted_at_seq`) and the first suspect `seq`; `legacy_unkeyed_through_seq` is defined only for orgs present in the first checkpoint. | receiver: ADR follow-up inside 11.5.2-T2; recorded in the 11.5.2 plan Notes and the 02.7.1 plan Notes |
| Debt-docs remainders (S141) | Two corrections in `plans/open-items/05-product-phases-g1.md` were left because open-items rows are owner-confirmed and S141 may not edit them: (1) the OI-13-545 row names receiver `13.5.1-T2/T5` while the 13.6.1 plan now says `13.5.1-T1/T5`; (2) a row near `:110` still cites `contracts/permissions.json:54-96`, the correct span is `:54-91`. | receiver: the owner's next open-items edit (both), recorded by S141 PR #293 |
| Stale security_event wording in docs/security (S140) | `docs/security/parse-child-network-isolation.md:71` and `:93` still say 11.5.1-T5.1 "creates `security_event`" / "creates the table and the emitter"; the table exists since `0053` (#286) and only the emitter is open. Found by the S140 QA agent (D2); the file was outside the S140 edit list. | receiver: queue F64 follow-up |
| Phase-review pointer sweep (S142) | S142 moved 345 closed rows to `docs/phase-review-archive.md`; the `:N` cites in status-carried.md are remapped, but 45 bare pointers (`docs/phase-review.md` plus a row id, e.g. status-carried Merge debt rows, STATUS.md:197, ADR-0008:56, plans 04.3.3, 05.2.2, 09.2.1, 12.3.1, 13.1.1) and the cite at `plans/open-items/02-catalog-ingest-runs-evidence.md:74` (D-95b, now archive) still name the old file. | receiver: next owner edit of each file, or the S144 F61 cite remap (open-items:74) |
| D-S137-OWNER wording sweep (S144) | D-S137-OWNER is recorded as decided (separate platform table) by S142, but present-tense "open" wording remains at migrations/README.md:391, plans/features/02-identity-tenancy/02.7.1-audit-trail.md:39, plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md:110 and scripts/check-rls.py:133; docs/security/parse-child-network-isolation.md already says it as decided | receiver: owners of migrations/README.md, 02.7.1, 11.5.1 and scripts/check-rls.py |
| `smoke-clean-build.sh` fixed temp-script name (found by #23) | `scripts/smoke-clean-build.sh:19` runs `mktemp "$HOME/.evidgate-smoke-inner.XXXXXX.sh"`; macOS `mktemp` substitutes only trailing `X`s, so every run uses the literal path `~/.evidgate-smoke-inner.XXXXXX.sh`. A concurrent run, or a copy left by a killed run, fails the next smoke with `mktemp: mkstemp failed ... File exists` (hit by #23 on 2026-10-08). Put the `X`s last (e.g. `mktemp "$HOME/.evidgate-smoke-inner.XXXXXX"`) (recorded in 01.1.1 §10 under 01.1.1-T3) | receiver: queue row F38; recorded owner: 01.1.1-T3; hand-over Notes: `plans/features/01-foundation/01.1.1-toolchain-and-workspace.md:115`; State: fixed by the CR-06 PR (S151): suffixless `mktemp` template plus `scripts/tests/test_smoke_mktemp.py`; close-out archives the row |
| Gate-scope lows (01.2.1-T7) | Open after the 01.2.1-T7 delta check (no second round), all wording or unreachable edge cases of the path-scoped gate; none narrows a tier: (H1) `scripts/gate_scope.py`, `docs/engineering/omp-setup.md` Step 4a and the 01.2.1 plan T7 block say a catalogue test needs every story file, but `CatalogueTests` needs only the `PendingV1Consumers` story files (the classifier over-approximates, the safe direction); (H2) the `plans/00-overview.md` §15 2026-10-30 row says CI sets `full` for jobs without `--stage`, but `ci.yml` sets it workflow-wide and every job passes `--stage`; (N1) `verify.sh` runs `main` only when executed directly, so `bash < scripts/verify.sh` runs no stage (not reachable from CI); (N2) the reverse-coverage test in `scripts/tests/test_gate_scope.py` sees full path literals only, not `Path.Combine(.., "docs", ..)` segments (none exist today); (N3) a bare run whose tier is `full` prints `== gate scope: no usable tier/stage list (full gate)` instead of the `full (<reason>, ...)` line, so the reason is not shown (all stages still run). Source: 01.2.1-T7 delta review on 01297954 and the clean-clone smoke log. | receiver: CR-06 (`plans/features/REVIEW-2026-10-29.md`, gate hardening lane); recorded owner: Tech Lead; State: closed by the CR-06 PR (S151): H1, H2, N2, N3 fixed, N1 piped `main` fixed in `scripts/verify.sh`; close-out archives the row |
| check-rls policy lint gaps (CR-03 reviews, S148) | Deferred by design after the one fix round, all lint-only and none reachable in the current migrations: (a) `create policy`/`grant` inside `DO $$ ... $$` or `EXECUTE` bodies are invisible to `scripts/check-rls.py`; (b) `operator_select_no_policy` accepts any policy that names `evidgate_operator` whatever its predicate, although the operator can set `evidgate.org_id` itself; (c) `revoke select (col)` and `revoke grant option for` are modelled conservatively (a standing table grant stays visible). Recorded in `migrations/README.md` 0055 notes | receiver: CR-06; recorded owner: Tech Lead; hand-over Notes: `plans/features/02-identity-tenancy/02.6.1-database-enforced-isolation.md` end block; State: closed by the CR-06 PR (S151): (a) and (b) implemented in `scripts/check-rls.py` with fixtures, (c) stays conservative and is documented in `migrations/README.md`; close-out archives the row |
| gate --wait token lifetime (S168) | One token refresh per invocation: a `--wait` spanning two token lifetimes exits 3 at the second expiry (PR #353) | receiver: next CLI task touching `gate`/auth; State: closed by #375 (S178, b1ddc7d1) |
| gate redaction low forms (S168) | `redact()` leaves scheme-relative or percent-encoded URLs, non-http schemes (`postgres://`), a raw space in URL userinfo, lowercase percent-encoded or base64 forms of a registered secret; judged low risk in the post-delta review of PR #353 | receiver: later CLI redaction hardening; State: closed by #375 (S178, b1ddc7d1) |
| CLI dev-exchange spec helper duplication (S177) | `devStubFetch`/`exchangeBody` are copied across about 7 `packages/cli` specs; `gate.kit.test.ts` is the existing shared kit they should move to (#372) | receiver: next `packages/cli` task; State: closed by #375 (S178, b1ddc7d1) |
| CLI token cache not bound to credential (S177) | the token cache key is `apiUrl` + project only, so a dev run can reuse a token minted for another loopback credential; pre-existing for ingest and OIDC sources, now also reachable by the dev source (`packages/cli/src/auth/tokenCache.ts`; #372) | receiver: next `packages/cli` auth task; State: closed by #375 (S178, b1ddc7d1) |
| adapter-playwright writerLock mismatch restore spec (S175) | `packages/adapter-playwright/src/writerLock.ts:318` has the same mismatch-restore arm as the CLI spool lock and its own `restoreQuarantinedLock`, but its specs use no `afterQuarantine` hook, so a retained mismatching quarantine is not tested there (found by the #365 fix round) | receiver: next adapter-playwright task; State: closed by #381 (S181, 450b1a65) |
| CLI token cache read follows symlinked cache dir (S178) | `readCachedToken` checks the directory owner but not that it is a real directory, so a symlinked `cache_dir` is read (writes refuse it); unchanged by #375 (`packages/cli/src/auth/tokenCache.ts`) | receiver: next `packages/cli` auth task; State: closed by #381 (S181, 450b1a65) |
| CLI ingest-exchange spec stubs not shared (S178) | `eventsSend`, `runsCreate`, `artifactsUpload`, `attachmentUpload` specs, `gate.kit.test.ts` `withTokenExchange` and `packages/adapter-playwright/src/attach.e2e.test.ts` keep their own exchange stubs next to the shared `commands/devExchange.testing.ts` (#375) | receiver: next `packages/cli` task; State: closed for the listed set (eventsSend, runsCreate, artifactsUpload, attachmentUpload, gate.kit) by #381 (S181, 450b1a65); remaining copies in row "CLI ingest-exchange stubs remaining copies (S181)" |
| CLI token cache orphans after rotation (S178) | a credential rotation leaves the previous `ci-token.<destination>.<binding>.json` until `auth logout` or a 401/403 purge; each holds only a token valid at most about 65 minutes at mode 0600 (#375) | receiver: next `packages/cli` auth task; State: closed by #381 (S181, 450b1a65) |
| CLI test comment carries a plan id (S178) | `packages/cli/src/auth/tokenCache.test.ts:52` comment names `04.2.1-T1.2 / AC3` (U1 ratchet); the line pre-dates #375 and was reworded in it | receiver: next `packages/cli` task; State: closed by #381 (S181, 450b1a65) |
| CLI dev command fails on unwritable cache (S177) | a dev command used to need no cache; since #372 it exchanges and writes the cache like an ingest token, so an unwritable `EVIDGATE_CACHE_DIR` is `token_cache_error`, exit 3 (consistent with `contracts/cli.md` §4; cold-cache `config check` now does a real exchange and concurrent cold starts can each exchange) — closed by S184 PR (pending merge) | receiver: owner decision if a cache-less dev path is wanted; State: closed by #384 (S184, c33fed43; owner decision 2026-11-01 (chat), recorded by the coordinator): an unwritable or unsafe cache now warns once and continues in memory |
| Pilot-0 web against the live API needs a human login (S186) | Closed by #395 (S187, 5a4e7b81): sign-in served by `POST /v1/dev/session`; the shell still needs 09.1.1-T4.2 (S190). Was: The web `development` build uses a session cookie and the dev user has no login; `GET /v1/runs/{id}` returns 401 with the dev token, so no live screenshots exist (exit check (e) not met, D-S186-Q4). A Development-only dev login is an owner option (#392) | receiver: 09.1.1-T4.2 (shell) |

## STATUS replaced text (S189, 2026-11-02)

Moved verbatim from STATUS.md §1 Resume here (lines 8, 9, 11, 12, 13, 14, 17, 18, 19) and §2 when the counters were refreshed after the S173-S186 wave.

- S124 (08.6.3-T1, `0052_healing_switch.sql`) is merged (#260, 569b538f); 11.5.1-T5 (`0053_security_security_event.sql`) is merged (#286, 560b5aba), 0054 (08.5.1-T1.1/T1.2, #306) merged, next free `0055`; T1 stays partial. Older line: Develop: `787f592` (PR #228, S102 02.3.2-T2, merged); 220 first-parent PR merges into develop. Migration `0048_identity_session_routes.sql` is merged in #228. S115 (05.3.2-T5) is in review with `0049_orchestration_evidence_revision.sql` (PR number in the delivery row below); the migration lane is held by S115 until it merges, next free migration `0051`. Closed owner-review rows now live in `docs/phase-review-archive.md` (S127, merged #266, c91f63a5); a row moves there verbatim in the commit that closes it.
- Open PRs: the S91 close-out PR plus dependabot PRs (not lanes).
- Active lanes (SESSION-CLAIMS 2026-10-27): S91 10.3.1-T2 not started (T1 merged, #203, closed out here); S98 09.1.1-T5 web-only (merged, #213, closed out here). S97 (03.2.1-T5) and S99 (09.1.1-T9.2, #209) are merged and closed out here. S102 (02.3.2-T2, sessions/logout) is merged (#228, 787f592) and closed out here.
- Fully landed on develop (counting S115 05.3.2-T5, S123 05.3.2-T2 and S131 11.5.2-T1 and S132 10.11.1-T3 once merged, and 01.2.1-T6, PR #310): 187/659 tasks; 191.75/705.75 raw person-days; remaining 472 tasks / 514.0 d. Via merged PRs: 160 tasks / 166.75 d; the other 27 tasks / 25.0 d are direct commits on develop. Method: task ranges in merged STATUS rows expanded against plan_graph task ids; a parent counts only when all its sub-rows landed.
- Landed by phase (done tasks / raw d): G1 137 / 147.75; G2 44 / 38; G3 0 / 0; G4 6 / 6.
- Plan totals: 659 tasks / 705.75 raw / 846.9 buffered person-days (S130 F47 added 02.3.2-T3, S, V1, G1; the owner decisions of 2026-10-30 added 01.2.1-T6 and 01.2.1-T7, both M, V1, G1; S171 added 05.3.2-T6, S, V1, G2; the S93 canonical figures were 655 / 702.75 / 843.3). `plan_graph.py` verified in this S102 close-out on `787f592`: errors 0, 205 stories, TOTAL 655 / 702.75 raw / 843.3 buffered. `check_features.py` still exits 1, now with 8 missing 13.x feature plans (13.5.1 to 13.9.1) after the S112 plans for 13.3.1 and 13.4.1 (queue F37 first part, §6 row); both new plans are `proposed, owner confirmation pending`.
- Next picks, web-only lane: 09.1.1-T5 is merged (#213) and 09.1.1-T9.2 is done (#209), so the web-only lane moves to G2 (§5).
- **Priority override (owner 2026-10-29): the code-review fix register `plans/features/REVIEW-2026-10-29.md` comes first — its P0 rows (CR-01…CR-06, 6.0 d) before any §5 pick in either lane, its P1 rows before the task named in their *Must precede* cell (07.2.1 waits on CR-07…CR-11); the next free migration `0055` goes to CR-03.** Next picks, backend lane (after P0): refill order in `.omp/coordinator/prompts/held/README.md`; when a backend slot frees, 08.5.1-T1 (XL, split, migration lane) unless §5 ranks another G1 task higher, and 04.1.2-T1 only after S91 releases.
- Next step: §5, 90 dependency-ready tasks / 101.5 d split by backend and web-only lane; §6 lists the non-graph blockers and carried items.

Section 2 tables replaced (baseline 187 tasks / 191.75 d):

| Delivery scope | Tasks | Raw person-days |
|---|---|---|
| Fully landed on develop | 187 | 191.75 |
| Via merged PRs | 160 | 166.75 |
| Direct commits on develop, no PR | 27 | 25.0 |
| Remaining | 472 | 514.0 |
| Plan total | 659 | 705.75 |

| Group | Tasks | XS | S | M | L | XL | Person-days |
|---|---|---|---|---|---|---|---|
| 01 | 46 | 2 | 10 | 22 | 11 | 1 | 52.5 |
| 02 | 29 | 0 | 8 | 18 | 3 | 0 | 28 |
| 03 | 8 | 0 | 3 | 4 | 1 | 0 | 7.5 |
| 04 | 22 | 0 | 7 | 12 | 3 | 0 | 21.5 |
| 05 | 19 | 0 | 5 | 9 | 5 | 0 | 21.5 |
| 06 | 12 | 0 | 4 | 8 | 0 | 0 | 10 |
| 07 | 6 | 0 | 2 | 4 | 0 | 0 | 5 |
| 08 | 1 | 0 | 0 | 1 | 0 | 0 | 1 |
| 09 | 22 | 0 | 6 | 13 | 3 | 0 | 22 |
| 10 | 5 | 0 | 2 | 2 | 1 | 0 | 5 |
| 11 | 11 | 1 | 5 | 4 | 1 | 0 | 8.75 |
| 12 | 1 | 0 | 0 | 0 | 1 | 0 | 2 |
| 13 | 5 | 0 | 0 | 3 | 2 | 0 | 7 |
| TOTAL | 187 | 3 | 52 | 100 | 31 | 1 | 191.75 |

| Group | Tasks | XS | S | M | L | XL | Person-days |
|---|---|---|---|---|---|---|---|
| G1 | 137 | 3 | 32 | 74 | 27 | 1 | 147.75 |
| G2 | 44 | 0 | 18 | 23 | 3 | 0 | 38 |
| G3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| G4 | 6 | 0 | 2 | 3 | 1 | 0 | 6 |
| TOTAL | 187 | 3 | 52 | 100 | 31 | 1 | 191.75 |

Section 2 partial-parent paragraph replaced (old STATUS line 34):

Seven partial parents in this table (01.7.1-T3, 01.8.1-T3, 10.2.1-T1, 09.1.1-T6, 10.7.1-T4 (T4.2 staging drill awaits staging), 09.3.1-T2, whose T2.1 is merged (#216, 1742764), T2.2 is delivered by S146 (#302, fbb2fe91) and whose remainder is T2.3 and T2.4; and 09.8.1-T1, whose T1.1 + T1.2 landed in #284 and whose remainder is T1.3 + T1.4) are excluded from done counts and dependency satisfaction; their landed subrows and remainder are recorded in the delivery table and in §5, except 09.1.1-T6, whose remainder T6.2 waits on 02.3.1-T3. Plan-only changes and queue rows add no task credit. Tasks cells list exactly the ids in the delivery evidence; the pre-S93 table also claimed `03.1.1-T3` and `03.1.3-T2/T3`, which have no landed delivery and were removed.

## STATUS replaced text (S299, 2026-11-08)

Moved verbatim from STATUS.md §1 Resume here (lines 8, 9, 11, 12, 13, 14) and §2 (the intro line, the delivery scope table, the partial-parent paragraph, Done by area, Done by phase) and from the AGENTS.md Current state bullets when the counters were recounted after #393 (S299, through #612, `2a641af8`).

- Develop: `fb7b8683` (#393, S186 close-out). Migrations `0001`–`0060` are contiguous; highest is `0060_identity_ingest_token_decision_read.sql` (#390), next free `0061`; 08.5.1-T1.1/T1.2 merged (#306), T1 stays partial (T1.3–T1.7 open). Closed owner-review rows live in `docs/phase-review-archive.md` (a row moves there verbatim in the commit that closes it).
- Open PRs: lane PRs of S187/S188/S189 plus dependabot PRs (not lanes).
- Lanes in flight (SESSION-CLAIMS 2026-11-02): S187 DEV-LOGIN, S188 REVIEW-DEPLOY, S189 STATUS-REFRESH (this refresh). S173-S186 are merged and closed out.
- Fully landed on develop (S173-S186 wave merged through #393, `fb7b8683`): 201/660 tasks; 205.0/706.75 raw person-days; remaining 459 tasks / 501.75 d. Via merged PRs: 174 tasks / 180.0 d; the other 27 tasks / 25.0 d are direct commits on develop. Method: task ranges in merged PR bodies and plan Notes expanded against plan_graph task ids; a parent counts only when all its sub-rows landed. The S189 recount added 14 tasks / 13.25 d after #310: 01.2.1-T7 #311; 01.8.1-T3 #351+#358; 01.8.1-T4 #362+#366; 05.3.2-T6 #378; 07.2.1-T1+T2 #368; 07.2.2-T1 #374, T3+T4 #377; 07.5.2-T1 #390; 07.6.1-T2 #353+#365+#384+#392; 09.3.1-T3+T5 #387; 09.8.1-T1 #284+#325. Declared partial, no credit: 05.3.2-T3, 05.3.2-T4, 07.2.2-T2, 09.6.1-T6, 10.7.1-T1/T2.
- Landed by phase (done tasks / raw d): G1 140 / 150.25; G2 49 / 43.5; G3 3 / 3.5; G4 9 / 7.75.
- Plan totals: 660 tasks / 706.75 raw / 848.1 buffered person-days (`plan_graph.py` on `fb7b8683`: errors 0, 205 stories; 04.2.2-T3 manifest generator added in S186, M; the S93 canonical figures were 655 / 702.75 / 843.3). `check_features.py` still exits 1 (8 missing 13.x feature plans, 13.5.1 to 13.9.1; queue F37).

Section 2 text (old STATUS lines 24-34):

Counted by expanding task ranges in merged STATUS rows against plan_graph task ids; a parent task counts only when every defined sub-row has landed on develop; sizes XS .25 S .5 M 1 L 2 XL 3. detail: carried row 'STATUS §2 count log S112-S128' (S144 F61).

| Delivery scope | Tasks | Raw person-days |
|---|---|---|
| Fully landed on develop | 201 | 205.0 |
| Via merged PRs | 174 | 180.0 |
| Direct commits on develop, no PR | 27 | 25.0 |
| Remaining | 459 | 501.75 |
| Plan total | 660 | 706.75 |

Five partial parents in this table (01.7.1-T3, 10.2.1-T1, 09.1.1-T6, 10.7.1-T4 (T4.2 staging drill awaits staging), 09.3.1-T2, whose T2.1 is merged (#216, 1742764), T2.2 is delivered by S146 (#302, fbb2fe91) and whose remainder is T2.3 and T2.4) are excluded from done counts and dependency satisfaction; their landed subrows and remainder are recorded in the delivery table and in §5, except 09.1.1-T6, whose remainder T6.2 waits on 02.3.1-T3. 01.8.1-T3 and 09.8.1-T1 were credited in the S189 recount on their sub-row done-whens, but their delivery rows (01.8.1 and 09.8.1 "partial"), their §5 rows (01.8.1-T3 including its T4.1 201-check wording, 09.3.1-T3, 09.8.1-T1) and the §5 08.5.1-T1 row still read as before and are not yet pruned (recorded gap). Plan-only changes and queue rows add no task credit. Tasks cells list exactly the ids in the delivery evidence; the pre-S93 table also claimed `03.1.1-T3` and `03.1.3-T2/T3`, which have no landed delivery and were removed.

Done by area and Done by phase (old STATUS lines 38-53 and 57-63, the two data tables):

| Group | Tasks | XS | S | M | L | XL | Person-days |
|---|---|---|---|---|---|---|---|
| 01 | 49 | 2 | 11 | 24 | 11 | 1 | 55.0 |
| 02 | 29 | 0 | 8 | 18 | 3 | 0 | 28 |
| 03 | 8 | 0 | 3 | 4 | 1 | 0 | 7.5 |
| 04 | 22 | 0 | 7 | 12 | 3 | 0 | 21.5 |
| 05 | 20 | 0 | 6 | 9 | 5 | 0 | 22 |
| 06 | 12 | 0 | 4 | 8 | 0 | 0 | 10 |
| 07 | 13 | 1 | 4 | 8 | 0 | 0 | 10.25 |
| 08 | 1 | 0 | 0 | 1 | 0 | 0 | 1 |
| 09 | 25 | 0 | 6 | 14 | 5 | 0 | 27 |
| 10 | 5 | 0 | 2 | 2 | 1 | 0 | 5 |
| 11 | 11 | 1 | 5 | 4 | 1 | 0 | 8.75 |
| 12 | 1 | 0 | 0 | 0 | 1 | 0 | 2 |
| 13 | 5 | 0 | 0 | 3 | 2 | 0 | 7 |
| TOTAL | 201 | 4 | 56 | 107 | 33 | 1 | 205.0 |

| Group | Tasks | XS | S | M | L | XL | Person-days |
|---|---|---|---|---|---|---|---|
| G1 | 140 | 3 | 33 | 76 | 27 | 1 | 150.25 |
| G2 | 49 | 0 | 19 | 26 | 4 | 0 | 43.5 |
| G3 | 3 | 0 | 1 | 1 | 1 | 0 | 3.5 |
| G4 | 9 | 1 | 3 | 4 | 1 | 0 | 7.75 |
| TOTAL | 201 | 4 | 56 | 107 | 33 | 1 | 205.0 |

AGENTS.md Current state (old lines 7, 9-11, 13-16):

**Current state (2026-11-02).** Stack decided in DECISIONS CR-D-1 (§4). Task-level execution state lives in `STATUS.md` (§1 snapshot, §2 done, §5 ready next) and `docs/status-carried.md` (blocked / carried items, formerly STATUS §6), not here.
- Landed on `develop` (through #393, `fb7b8683`; recount S189 from the merged PRs after #310, method in STATUS §2 and §1 Resume): 201 of 660 planned tasks = 205.0 of 706.75 raw person-days (≈30 % of tasks, ≈29 % of effort); 459 tasks / 501.75 d remain.
- Landed by CR-D-2 phase (tasks / landed person-days, of the phase's planned raw): G1 140 / 150.25 of 283.75 · G2 49 / 43.5 of 197 · G3 3 / 3.5 of 106.75 · G4 9 / 7.75 of 119.25.
- Landed by area plan (tasks / planned): 01 49/55 · 02 29/49 · 03 8/40 · 04 22/54 · 05 20/54 · 06 12/36 · 07 13/42 · 08 1/69 · 09 25/72 · 10 5/54 · 11 11/51 · 12 1/30 · 13 5/54.
- Delivered modules: catalog case/suite identity and revisions (03.1.1/03.1.3/03.2.1), runs manifest upload (05.1.1), the run reconciler with evidence revisions and the freeze seam (05.3.2-T1/T2/T5), evidence upload + safe parsing (06.1/06.3.1–06.3.2), policy revisions and waiver/quarantine lifecycles (07.1.1/07.3.1), the evaluator core and handler (07.2.1, 07.2.2-T1/T3/T4), the run decision route (07.5.2-T1) and the CLI `gate` command with spool (07.6.1-T2), sessions with list/revoke/revoke-all/logout and the cleanup sweep (02.3.2-T1/T2), and the Angular 22 web app with scaffold, app shell, shared state components and generated-SDK data layer (09.1.1-T1/T3/T4/T5/T7/T8/T9/T10/T12).
- Migrations `0001`–`0060` are contiguous; highest is `0060_identity_ingest_token_decision_read.sql` (merged in #390), the migration lane is free and the next free number is `0061`.
- Not yet started: plan 08 (safe healing/learning) has only 08.6.3-T1 (the healing kill switch) plus the partial 08.5.1-T1.1/T1.2 (gateway table, T1.3–T1.7 open, not counted); plan 12 (pilot/commercial) has only 12.3.1-T1 (the usage ledger).
- Not yet inside started areas: 07 evaluator remainder (07.2.2-T2 matrix and fixtures, partial), waiver/quarantine effects + expiry (07.3.2), flaky detection (07.4), late evidence (07.5.1), release decision aggregation (07.5.3, 07.5.4), webhooks and CI delivery beyond the CLI gate (07.6.1-T1, 07.6.2, 07.6.3); 06 sensitivity/redaction/secret masking (06.2) and the isolated viewer (06.3.3–06.3.4); most web product screens; real `platform_operator` auth (02.8.1); human login (02.3.1-T3).

## STATUS replaced text (S305, 2026-11-08)

Moved verbatim from STATUS.md §1 Resume here (line 18, the Pilot-0 bullet), replaced by a sentence checked against `plans/features/SOLO-PATH.md` Notes S187/S188 and merges #395, #401 and #403 at `2718c29f`.

- Pilot-0 (SOLO-PATH §2): criteria a-d met live in #392 (run A PASS gate 0, run B FAIL gate 1); (e) web against the live API is open (needs human login 02.3.1; S187 DEV-LOGIN in flight); review deployment is S188.

## STATUS replaced text (S317, 2026-11-08)

Moved verbatim from STATUS.md §1 Resume here (lines 8, 9, 11, 12, 13, 14), §2 (updated line 3, the intro line 24, the delivery scope rows, the 09.3.1-T6 and 09.8.1-T3 partial rows, the Done by area rows 02 and TOTAL, the Done by phase rows G1 and TOTAL) and from the AGENTS.md Current state bullets (landed counts, phase and area counts) when the counters were recounted after #648.

Updated 2026-11-02. Source of truth for tasks: `plans/01`–`13` and `plans/features/`; this file tracks execution only. Task ids and sizes come from `scripts/plan_graph.py`.
- Develop: `2a641af8` (#612, S294 close-out; recount base). Migrations `0001`–`0084` are contiguous; highest is `0084_read_order_compatible_indexes.sql`, next free `0085`; 08.5.1-T1.1/T1.2 merged (#306), T1 stays partial (T1.3–T1.7 open). Closed owner-review rows live in `docs/phase-review-archive.md` (a row moves there verbatim in the commit that closes it).
- Open PRs: lane PRs of S296/S298/S299 plus dependabot PRs (not lanes).
- Lanes in flight (2026-11-08): S296 HARNESS-SPEED, S298 GATE-PERF, S299 STATUS-RECOUNT (this recount); earlier lanes are merged and closed out.
- Fully landed on develop (recount S299 through #612, `2a641af8`): 235/660 tasks (≈35.6 %); 236.5/706.75 raw person-days (≈33.5 %); remaining 425 tasks / 470.25 d. Via merged PRs: 208 tasks / 211.5 d; the other 27 tasks / 25.0 d are direct commits on develop. Method (unchanged from S189): task ranges in merged PR bodies, close-out rows and plan Notes expanded against plan_graph task ids; a parent counts only when all its sub-rows landed. The S299 recount added 33 tasks / 31.0 d after #393 (ids in §2); the S189 base of 201 / 205.0 is unchanged. 31 parents are partial and carry no credit (§2).
- Landed by phase (done tasks / raw d): G1 151 / 158.75; G2 67 / 61.5; G3 5 / 4.5; G4 12 / 11.75.
- Plan totals: 660 tasks / 706.75 raw / 848.1 buffered person-days (`plan_graph.py` on `2a641af8`: errors 0, 205 stories; 04.2.2-T3 manifest generator added in S186, M; the S93 canonical figures were 655 / 702.75 / 843.3). `check_features.py` still exits 1 (8 missing 13.x feature plans, 13.5.1 to 13.9.1; queue F37).
Counted by expanding task ranges in merged PR bodies, close-out rows and plan Notes against plan_graph task ids (S299 recount through #612, `2a641af8`, method unchanged from S189); a parent task counts only when every defined sub-row has landed on develop; sizes XS .25 S .5 M 1 L 2 XL 3. detail: carried rows 'STATUS §2 count log S112-S128' (S144 F61) and 'STATUS §2 count log S299'.
| Fully landed on develop | 235 | 236.5 |
| Via merged PRs | 208 | 211.5 |
| Direct commits on develop, no PR | 27 | 25.0 |
| Remaining | 425 | 470.25 |
| Plan total | 660 | 706.75 |
| 09.3.1-T6 | T6.1, T6.2 | #568 |
| 09.8.1-T3 | T3.2, T3.3, T3.4 | #561 |
| 02 | 32 | 0 | 10 | 19 | 3 | 0 | 30 |
| TOTAL | 235 | 4 | 69 | 124 | 37 | 1 | 236.5 |
| G1 | 151 | 3 | 40 | 79 | 28 | 1 | 158.75 |
| TOTAL | 235 | 4 | 69 | 124 | 37 | 1 | 236.5 |
- Landed on `develop` (through #617, `1a06941c`; recount S299 from the merged PRs after #393 with the S189 method, STATUS §2 and §1 Resume): 235 of 660 planned tasks = 236.5 of 706.75 raw person-days (≈35.6 % of tasks, ≈33.5 % of effort); 425 tasks / 470.25 d remain. 31 parent tasks are partial and carry no credit (list in STATUS §2).
- Landed by CR-D-2 phase (tasks / landed person-days, of the phase's planned raw): G1 151 / 158.75 of 283.75 · G2 67 / 61.5 of 197 · G3 5 / 4.5 of 106.75 · G4 12 / 11.75 of 119.25.
- Landed by area plan (tasks / planned): 01 49/55 · 02 32/49 · 03 10/40 · 04 29/54 · 05 25/54 · 06 15/36 · 07 13/42 · 08 1/69 · 09 31/72 · 10 5/54 · 11 13/51 · 12 5/30 · 13 7/54.
- Delivered modules (S173-S298 additions after the S189 list): catalog case/suite identity and revisions (03.1.1/03.1.3/03.2.1, taxonomy 03.1.4-T1, fixture revisions 03.3.1-T1), runs manifest upload (05.1.1), the run reconciler with evidence revisions and the freeze seam (05.3.2-T1/T2/T5), run cancel (05.4.1-T1–T3) and release tables with digest binding (05.6.1-T1/T2), evidence upload + safe parsing (06.1/06.3.1–06.3.2), artifact sensitivity (06.2.1-T1), async secret scan (06.2.3-T1), retention policy (06.4.1-T1), the pytest/JUnit report path (04.4.1-T1/T3–T6), policy revisions and waiver/quarantine lifecycles (07.1.1/07.3.1), the evaluator core and handler (07.2.1, 07.2.2-T1/T3/T4), the run decision route (07.5.2-T1) and the CLI `gate` command with spool (07.6.1-T2), the usage ledger, price table, roll-up, quota gate and summary read (12.3.1-T1/T3–T6, 04.8.1-T1, 04.1.2-T1), sessions with list/revoke/revoke-all/logout and the cleanup sweep (02.3.2-T1/T2), member removal guard (02.1.2-T3), project API tokens (02.4.4-T1), and the Angular 22 web app with scaffold, app shell, shared state components, generated-SDK data layer, run detail timeline (09.3.1-T2), quota and retention settings (09.7.1-T5/T11), API tokens screen (09.7.2-T1) and a11y/i18n QA (09.8.1-T2/T4).

## STATUS replaced text (S337, 2026-11-09)

Moved verbatim from STATUS.md (line 3, §1 Resume here lines 8, 9, 11, 12, 14, §2 lines 24, 34 and 82) and from the AGENTS.md Current state bullets (heading date, landed counts, migrations; the Delivered modules line is replaced in full) when the counters were recounted after #688. The lines are shown in file order; the AGENTS.md bullets follow the STATUS lines.

Updated 2026-11-08. Source of truth for tasks: `plans/01`–`13` and `plans/features/`; this file tracks execution only. Task ids and sizes come from `scripts/plan_graph.py`.

- Develop: `f317114b` (#648, S312 close-out; recount base). Migrations `0001`–`0087` are contiguous; highest is `0087_usage_ledger_issued_period_guard.sql`, next free `0088`; 08.5.1-T1.1/T1.2 merged (#306), T1 stays partial (T1.3–T1.7 open). Closed owner-review rows live in `docs/phase-review-archive.md` (a row moves there verbatim in the commit that closes it).

- Open PRs: lane PRs of S315/S316/S317 plus dependabot PRs (not lanes).

- Lanes in flight (2026-11-08): S315 TESTHOST-STALL, S316 CAPTURE-CAP-RUN, S317 STATUS-RECOUNT (this recount); earlier lanes are merged and closed out.

- Fully landed on develop (recount S317 through #648, `f317114b`): 236/660 tasks (≈35.8 %); 237.0/706.75 raw person-days (≈33.5 %); remaining 424 tasks / 469.75 d. Via merged PRs: 209 tasks / 212.0 d; the other 27 tasks / 25.0 d are direct commits on develop. Method (unchanged from S189): task ranges in merged PR bodies, close-out rows and plan Notes expanded against plan_graph task ids; a parent counts only when all its sub-rows landed. The S317 recount adds 1 task / 0.5 d after the S299 recount of #612 (02.3.2-T3, #628; 11.2.1-T7 of #617 was already in the S299 figure; every other PR #613–#648 is hardening, tests, gate or docs without a task credit); the S189 base of 201 / 205.0 is unchanged. 31 parents are partial and carry no credit (§2).

- Plan totals: 660 tasks / 706.75 raw / 848.1 buffered person-days (`plan_graph.py` on `f317114b`: errors 0, 205 stories; 04.2.2-T3 manifest generator added in S186, M; the S93 canonical figures were 655 / 702.75 / 843.3). `check_features.py` still exits 1 (8 missing 13.x feature plans, 13.5.1 to 13.9.1; queue F37).

Counted by expanding task ranges in merged PR bodies, close-out rows and plan Notes against plan_graph task ids (S317 recount through #648, `f317114b`, method unchanged from S189; the S299 recount covered #612); a parent task counts only when every defined sub-row has landed on develop; sizes XS .25 S .5 M 1 L 2 XL 3. detail: carried rows 'STATUS §2 count log S112-S128' (S144 F61), 'STATUS §2 count log S299' and 'STATUS §2 count log S317'.

The S299 recount added 33 tasks / 31.0 d to the S189 figures (201 / 205.0; none removed); 11.2.1-T7 (#617) made it 235 / 236.5, and the S317 recount adds 02.3.2-T3 (G1, S, #628, one sub-row T3.1; Area 02 below) for 236 / 237.0. Parents credited by the S299 recount:

The 31 partial parents above are excluded from done counts and dependency satisfaction; their landed subrows and remainder are recorded in the delivery table and in §5. This table replaces the S189 partial list: 01.8.1-T3 and 09.8.1-T1 stay credited from the S189 recount on their sub-row done-whens (their delivery rows and §5 rows were not pruned then, recorded gap), 09.3.1-T2 is now credited (T2.3 #572, T2.4 #583), and 09.1.1-T6 still waits on its T6.2. Plan-only changes and queue rows add no task credit. Tasks cells list exactly the ids in the delivery evidence; the pre-S93 table also claimed `03.1.1-T3` and `03.1.3-T2/T3`, which have no landed delivery and were removed.

AGENTS.md:

**Current state (2026-11-08).** Stack decided in DECISIONS CR-D-1 (§4). Task-level execution state lives in `STATUS.md` (§1 snapshot, §2 done, §5 ready next) and `docs/status-carried.md` (blocked / carried items, formerly STATUS §6), not here.
- Landed on `develop` (through #648, `f317114b`; recount S317 with the S189 method from the merged PRs after #612, STATUS §2 and §1 Resume): 236 of 660 planned tasks = 237.0 of 706.75 raw person-days (≈35.8 % of tasks, ≈33.5 % of effort); 424 tasks / 469.75 d remain. 31 parent tasks are partial and carry no credit (list in STATUS §2).
- Delivered modules (S173-S314 additions after the S189 list): catalog case/suite identity and revisions (03.1.1/03.1.3/03.2.1, taxonomy 03.1.4-T1, fixture revisions 03.3.1-T1), runs manifest upload (05.1.1), the run reconciler with evidence revisions and the freeze seam (05.3.2-T1/T2/T5), run cancel (05.4.1-T1–T3) and release tables with digest binding (05.6.1-T1/T2), evidence upload + safe parsing (06.1/06.3.1–06.3.2), artifact sensitivity (06.2.1-T1), async secret scan (06.2.3-T1), retention policy (06.4.1-T1), the pytest/JUnit report path (04.4.1-T1/T3–T6), policy revisions and waiver/quarantine lifecycles (07.1.1/07.3.1), the evaluator core and handler (07.2.1, 07.2.2-T1/T3/T4), the run decision route (07.5.2-T1) and the CLI `gate` command with spool (07.6.1-T2), the usage ledger, price table, roll-up, quota gate and summary read (12.3.1-T1/T3–T6, 04.8.1-T1, 04.1.2-T1), sessions with list/revoke/revoke-all/logout, the admin end-all-sessions route and the cleanup sweep (02.3.2-T1/T2/T3), member removal guard (02.1.2-T3), project API tokens (02.4.4-T1), the definer review (11.2.1-T7), and the Angular 22 web app with scaffold, app shell, shared state components, generated-SDK data layer, run detail timeline (09.3.1-T2), quota and retention settings (09.7.1-T5/T11), API tokens screen (09.7.2-T1) and a11y/i18n QA (09.8.1-T2/T4).
- Migrations `0001`–`0087` are contiguous; highest is `0087_usage_ledger_issued_period_guard.sql`, the next free number is `0088`.

## STATUS replaced text (S369, 2026-11-11)

Moved verbatim from STATUS.md (line 3, §1 Resume lines, §2 intro, delivery-scope, phase, area and partial-parent rows, the S299 paragraph and partial-table note, four §5 rows) and from AGENTS.md Current state bullets (heading date, landed counts, Delivered modules, migrations) when the S369 recount through #751 (`41edd414`) replaced them. Each block names its origin.

STATUS.md line 3:

Updated 2026-11-09. Source of truth for tasks: `plans/01`–`13` and `plans/features/`; this file tracks execution only. Task ids and sizes come from `scripts/plan_graph.py`.

STATUS.md §1 Resume develop line:

- Develop: `fe750819` (#689, S334 purge lease guard; recount base). Migrations `0001`–`0089` are contiguous; highest is `0089_identity_ci_token_purge_acl.sql`, next free `0090` (reserved by the open S336); 08.5.1-T1.1/T1.2 merged (#306), T1 stays partial (T1.3–T1.7 open). Closed owner-review rows live in `docs/phase-review-archive.md` (a row moves there verbatim in the commit that closes it).

STATUS.md §1 Resume open PRs:

- Open PRs: dependabot PRs only (not lanes); the S336 and S337 lanes are in flight and S337 opens its PR on this recount.

STATUS.md §1 Resume lanes line:

- Lanes in flight (2026-11-09): S336 SESSION-REVOKE-HELPER, S337 STATUS-RECOUNT (this recount); earlier lanes are merged and closed out. S360 STALE-EPOCH landed 04.1.2-T2 (#736, no migration); counts not recomputed for it. S358 ARTIFACT-STATE-GUARD landed 06.1.3-T1 (#729, migration 0095); the counts below are not recomputed for it. S356 FORK-PR-FIXTURE landed 11.2.1-T2 (#726); counts not recomputed for it. S357 METRICS-CATALOGUE landed 10.3.1-T3 (#732, no migration); counts not recomputed for it. S367 HEALING-PROVIDER fixed review row CR-20 (#750, merge d9c2304f, no migration, no task credit: provider from config, gateway switch and budget guards, Apply-off clamp); counts not recomputed for it.

STATUS.md §1 Resume fully-landed line:

- Fully landed on develop (recount S337 through #689, `fe750819`): 236/660 tasks (≈35.8 %); 237.0/706.75 raw person-days (≈33.5 %); remaining 424 tasks / 469.75 d. Via merged PRs: 209 tasks / 212.0 d; the other 27 tasks / 25.0 d are direct commits on develop. Method (unchanged from S189): task ranges in merged PR bodies, close-out rows and plan Notes expanded against plan_graph task ids; a parent counts only when all its sub-rows landed. The S299 recount of #612 gave 234 / 236.0, #617 (11.2.1-T7) made it 235 / 236.5, and the S317 recount added 02.3.2-T3 (#628) for 236 / 237.0. The S337 recount adds 0 tasks / 0 d after it: the feature PRs #650–#689 (S315–S335 and S334 lanes) are hardening, tests, gate, perf and docs without a task credit, and the only plan task they name is 09.5.2-T3.1 (#684), which leaves its parent 09.5.2-T3 partial (T3.2 open); the S189 base of 201 / 205.0 is unchanged. 32 parents are partial and carry no credit (§2). The S337 count-log carried row is appended at close-out; the recount script output is in the PR body.

STATUS.md §1 Resume phase line:

- Landed by phase (done tasks / raw d): G1 152 / 159.25; G2 67 / 61.5; G3 5 / 4.5; G4 12 / 11.75.

STATUS.md §1 Resume plan totals line:

- Plan totals: 660 tasks / 706.75 raw / 848.1 buffered person-days (`plan_graph.py` on `fe750819`: errors 0, 205 stories; 04.2.2-T3 manifest generator added in S186, M; the S93 canonical figures were 655 / 702.75 / 843.3). `check_features.py` still exits 1 (8 missing 13.x feature plans, 13.5.1 to 13.9.1; queue F37).

STATUS.md §2 intro:

Counted by expanding task ranges in merged PR bodies, close-out rows and plan Notes against plan_graph task ids (S337 recount through #689, `fe750819`, method unchanged from S189; the S317 recount covered #648 and the S299 recount #612); a parent task counts only when every defined sub-row has landed on develop; sizes XS .25 S .5 M 1 L 2 XL 3. detail: carried rows 'STATUS §2 count log S112-S128' (S144 F61), 'STATUS §2 count log S299' and 'STATUS §2 count log S317'.

STATUS.md §2 delivery table row:

| Fully landed on develop | 236 | 237.0 |

STATUS.md §2 delivery table row:

| Via merged PRs | 209 | 212.0 |

STATUS.md §2 delivery table row:

| Remaining | 424 | 469.75 |

STATUS.md §2 S299 paragraph:

The S299 recount added 33 tasks / 31.0 d to the S189 figures (201 / 205.0; none removed); 11.2.1-T7 (#617) made it 235 / 236.5, and the S317 recount adds 02.3.2-T3 (G1, S, #628, one sub-row T3.1; Area 02 below) for 236 / 237.0; the S337 recount adds none (no task landed in full after #648). The per-phase and per-area splits below are carried from the S317 tables because no credit changed: the S337 script reproduces the totals and the G1/G4 splits from the task ids named in this file, but not G2/G3 (68 / 62.5 and 4 / 3.5 against 67 / 61.5 and 5 / 4.5) nor areas 09 and 11 (one M task each), because this file does not name every credited id. Parents credited by the S299 recount:

STATUS.md §2 phase row:

| G1 | 152 | 3 | 41 | 79 | 28 | 1 | 159.25 |

STATUS.md §2 phase row:

| G2 | 67 | 0 | 23 | 38 | 6 | 0 | 61.5 |

STATUS.md §2 total row:

| TOTAL | 236 | 4 | 70 | 124 | 37 | 1 | 237.0 |

STATUS.md §2 total row:

| TOTAL | 236 | 4 | 70 | 124 | 37 | 1 | 237.0 |

STATUS.md §2 area row:

| 04 | 29 | 0 | 9 | 16 | 4 | 0 | 28.5 |

STATUS.md §2 area row:

| 06 | 15 | 0 | 5 | 10 | 0 | 0 | 12.5 |

STATUS.md §2 area row:

| 09 | 31 | 0 | 9 | 16 | 6 | 0 | 32.5 |

STATUS.md §2 area row:

| 10 | 5 | 0 | 2 | 2 | 1 | 0 | 5 |

STATUS.md §2 area row:

| 11 | 13 | 1 | 6 | 5 | 1 | 0 | 10.25 |

STATUS.md §2 partial row:

| 09.3.1-T6 | T6.1, T6.2 | #568, #642 (producer slot facts served by `GET /v1/runs/{r}`; the web wiring of `ProducerStatusTable` landed in #703 (9b0738c8); open slots show their count since #715) |

STATUS.md §2 partial row:

| 10.3.2-T1 | none (T1.2 landed by S348, #713, 1c021d7b; credit at the next recount) | #465, #713; 10.3.1-T2 landed by S352 (#722, dbcba3e2, dev profile only, credit at the next recount); 10.3.1-T3 landed by S357 (#732, credit at the next recount) |

STATUS.md §2 partial-table note:

The 31 partial parents above plus 09.5.2-T3 (T3.2 open; T3.1 landed in #684) make 32; they are excluded from done counts and dependency satisfaction; their landed subrows and remainder are recorded in the delivery table and in §5. 09.5.2-T3 has no row in the table above because a row insert would shift every STATUS.md:N cite below. This table replaces the S189 partial list: 01.8.1-T3 and 09.8.1-T1 stay credited from the S189 recount on their sub-row done-whens (their delivery rows and §5 rows were not pruned then, recorded gap), 09.3.1-T2 is now credited (T2.3 #572, T2.4 #583), and 09.1.1-T6 still waits on its T6.2. Plan-only changes and queue rows add no task credit. Tasks cells list exactly the ids in the delivery evidence; the pre-S93 table also claimed `03.1.1-T3` and `03.1.3-T2/T3`, which have no landed delivery and were removed.

STATUS.md §5 row:

| 04.1.2-T2 | S | LANDED (S360, #736, merge 7e610275, no migration, no contract change): the batch insert marks `stale = true` when `event.epoch` differs from `run.ci_run_ref` `run_attempt` (V1, D-S360-Q1), `stale_epoch` once per closed shard in reconciler `missing[]`; V2 lease-epoch seam in the plan Notes; credit at the next recount | yes | no | no | n/a | yes |

STATUS.md §5 row:

| 06.1.3-T1 | S | LANDED (S358, #729, merge 584946d3, migration 0095): `artifact_guard_transition_trg` allow-list trigger, `ArtifactState` domain guard shared with the SQL list, `complete` maps a refused move to 409 `artifact_state_conflict`; `ready → rejected` kept as the parse-verdict edge (00 §8.4 amended, 00 §15 row 2026-11-10); `rejected` and `deleted` terminal. Open items: carried rows titled `(S358)`. Not yet counted in the §1 recount. |

STATUS.md §5 row:

| 10.3.2-T1 | S | ADR: observability backend for M1 with retention and cost limits, plus the compose profile; unblocks 10.3.1-T2 (T1 merged in #203); T1.1 landed (ADR-0012, #465, 9e81d0ec), T1.2 LANDED by S348 (#713, 1c021d7b, no migration): opt-in compose profile `observability` (collector, Prometheus, VictoriaLogs, VictoriaTraces), `scripts/dev/up.sh --observability`, `observability-smoke.sh`; both subrows landed, so the parent is complete and credited by the next recount; open items are carried rows titled `(S348)` | yes | no | no | n/a | no — landed |

STATUS.md §5 row:

| 09.3.1-T6 | M | Producer/shard view: finalize status, `last_sequence`, count vs expected, stale epochs, late events. PARTIAL, S276 #568 96b4a263: `ProducerStatusTable` (one row per declared slot, 200 rows per page), informational stale/after-cancel badges that never touch completeness, run-level `after_cancel_events` on the timeline; per-slot facts are served and wired (#703, 9b0738c8); an open slot shows its count (#715, 741a548e); a fact the server omits renders unavailable | no | no | no — consumes the 03.2.1 suite-composition/slot API that S97 is building (dependency, not a shared file) | n/a | yes |

AGENTS.md Current state heading line:

**Current state (2026-11-09).** Stack decided in DECISIONS CR-D-1 (§4). Task-level execution state lives in `STATUS.md` (§1 snapshot, §2 done, §5 ready next) and `docs/status-carried.md` (blocked / carried items, formerly STATUS §6), not here.

AGENTS.md landed bullet:

- Landed on `develop` (through #689, `fe750819`; recount S337 with the S189 method from the merged PRs after #648, STATUS §2 and §1 Resume; base 236 / 237.0 unchanged, no task landed in full since the S317 recount): 236 of 660 planned tasks = 237.0 of 706.75 raw person-days (≈35.8 % of tasks, ≈33.5 % of effort); 424 tasks / 469.75 d remain. 32 parent tasks are partial and carry no credit (list in STATUS §2).

AGENTS.md phase bullet:

- Landed by CR-D-2 phase (tasks / landed person-days, of the phase's planned raw): G1 152 / 159.25 of 283.75 · G2 67 / 61.5 of 197 · G3 5 / 4.5 of 106.75 · G4 12 / 11.75 of 119.25.

AGENTS.md area bullet:

- Landed by area plan (tasks / planned): 01 49/55 · 02 33/49 · 03 10/40 · 04 29/54 · 05 25/54 · 06 15/36 · 07 13/42 · 08 1/69 · 09 31/72 · 10 5/54 · 11 13/51 · 12 5/30 · 13 7/54.

AGENTS.md Delivered modules bullet:

- Delivered modules (S173-S335 additions after the S189 list): catalog case/suite identity and revisions (03.1.1/03.1.3/03.2.1, taxonomy 03.1.4-T1, fixture revisions 03.3.1-T1), runs manifest upload (05.1.1), the run reconciler with evidence revisions and the freeze seam (05.3.2-T1/T2/T5), run cancel (05.4.1-T1–T3) and release tables with digest binding (05.6.1-T1/T2), evidence upload + safe parsing (06.1/06.3.1–06.3.2), artifact sensitivity (06.2.1-T1), async secret scan (06.2.3-T1), retention policy (06.4.1-T1), the pytest/JUnit report path (04.4.1-T1/T3–T6), policy revisions and waiver/quarantine lifecycles (07.1.1/07.3.1), the evaluator core and handler (07.2.1, 07.2.2-T1/T3/T4), the run decision route (07.5.2-T1) and the CLI `gate` command with spool (07.6.1-T2), the usage ledger, price table, roll-up, quota gate and summary read (12.3.1-T1/T3–T6, 04.8.1-T1, 04.1.2-T1), sessions with list/revoke/revoke-all/logout, the admin end-all-sessions route and the cleanup sweep (02.3.2-T1/T2/T3), member removal guard (02.1.2-T3), project API tokens (02.4.4-T1), the definer review (11.2.1-T7), and the Angular 22 web app with scaffold, app shell, shared state components, generated-SDK data layer, run detail timeline (09.3.1-T2), quota and retention settings (09.7.1-T5/T11), API tokens screen (09.7.2-T1) a11y/i18n QA (09.8.1-T2/T4) and the healing availability page with per-scope switch controls (09.5.2-T3.1, a partial parent, uncounted).

AGENTS.md migrations bullet:

- Migrations `0001`–`0089` are contiguous; highest is `0089_identity_ci_token_purge_acl.sql`, the next free number is `0090` (reserved by the open S336).

## Moved from STATUS.md by S372 (2026-11-11)

Segments removed verbatim from STATUS.md at develop `18d98e16` to bring every line to 768 characters or less. Each item names the original STATUS.md line number; leading and trailing spaces of a segment are part of the text.

### Item 1 (STATUS.md line 11)

```text
 The S369 recount credits 04.1.2-T2 (#736), 06.1.3-T1 (#729), 09.3.1-T6 (#568, #642, #703, #715) and 10.3.1-T3 (#732); 10.3.1-T2 (#722), 10.3.2-T1 (#465, #713) and 11.2.1-T2 (#726) landed with an explicit open Done-when and stay partial. S367 HEALING-PROVIDER fixed review row CR-20 (#750, merge d9c2304f, no migration, no task credit). S368 USAGE-LEDGER-HASH fixed review row CR-21 (#749, head 9c192102, merge 95056051, migration 0098, no task credit): the usage ledger stores a payload hash and a reused billing key with another payload is a conflict; debts in the carried rows titled `(S368)`. S370 FEATURE-PLANS (#755, head c87da410, merge 8cdf54ca, docs only, no migration, no task credit, no recount): `check_features.py` errors 9 -> 0; new feature plans 13.8.2 (review evidence) and 13.9.1 (gate metrics), the 02.3.2-T3 and 04.2.2-T3 plan blocks and three section-order repairs; open items are OI-13-571..580 in `plans/open-items/05-product-phases-g1.md`, merge debts D-S370-M1..M3 in the #755 comments.
```

### Item 2 (STATUS.md line 12)

```text
 Method (unchanged from S189): task ranges in merged PR bodies, close-out rows and plan Notes expanded against plan_graph task ids; a parent counts only when all its sub-rows landed; from S369 an explicitly open Done-when also withholds credit (earlier counts, including the S299 credits kept while an AC sat in a carried row, stay as they are). The S299 recount of #612 gave 234 / 236.0, #617 (11.2.1-T7) made it 235 / 236.5, the S317 recount added 02.3.2-T3 (#628) for 236 / 237.0, and the S337 recount (through #689) added none. The S369 recount adds 4 tasks / 3.0 d from the 60 merged PRs after `fe750819` (#690–#751): 04.1.2-T2, 06.1.3-T1, 09.3.1-T6 (exactly two sub-rows, T6.1 and T6.2, both delivered: served facts #642, web wiring #703) and 10.3.1-T3. Three parents the lanes called landed stay partial because a Done-when is recorded as open: 11.2.1-T2 (T2.2 AC3 `auth_failure` event, no emitter), 10.3.1-T2 (public browser intake and forced-sampling cap not delivered, dev profile only) and 10.3.2-T1 (T1.2 AC2/AC3 expiry and size-ceiling behaviour unexercised). CR-* register fixes (#740, #741, #743, #746, #750) and the other lanes name no further plan task that landed in full; 10.7.1-T3, T5 and T6 (doc parts #696, #698) and 09.5.2-T3 stay partial; 0098 (open S368) is not counted. 36 parents are partial and carry no credit (§2). The S369 count-log carried row is appended at close-out; the recount script output is in the PR body.
```

### Item 3 (STATUS.md line 34)

```text
The S299 recount added 33 tasks / 31.0 d to the S189 figures (201 / 205.0; none removed); 11.2.1-T7 (#617) made it 235 / 236.5, the S317 recount added 02.3.2-T3 (G1, S, #628, one sub-row T3.1; Area 02 below) for 236 / 237.0, and the S337 recount added none.
```

### Item 4 (STATUS.md line 34)

```text
; the S337 note stands that a script over the task ids named in this file does not reproduce the G2/G3 and area 09/11 base splits, because this file does not name every credited id.
```

### Item 5 (STATUS.md line 82)

```text
 This table replaces the S189 partial list: 01.8.1-T3 and 09.8.1-T1 stay credited from the S189 recount on their sub-row done-whens (their delivery rows and §5 rows were not pruned then, recorded gap), 09.3.1-T2 is now credited (T2.3 #572, T2.4 #583), and 09.1.1-T6 still waits on its T6.2. Plan-only changes and queue rows add no task credit. Tasks cells list exactly the ids in the delivery evidence; the pre-S93 table also claimed `03.1.1-T3` and `03.1.3-T2/T3`, which have no landed delivery and were removed.
```

### Item 6 (STATUS.md line 224)

```text
 Whole task credit: the only defined sub-row T2.1 is landed (S, 0.5 d); T1 + T2 fully land story 02.3.2. The web `SessionEnder` swap/session screen and admin all-sessions verb remain separate F41 hand-overs, not unbuilt T2 sub-rows.
```

### Item 7 (STATUS.md line 232)

```text
, route `runs/:runId`, run-list row link, `fixtures/web-api/run-detail*`, `run-attempts*`, review routes `dev/run-detail`, e2e `run-detail.spec.ts`, vi + en strings
```

### Item 8 (STATUS.md line 237)

```text
 (search_path, return shape, EXECUTE, audit, abuse matrix)
```

### Item 9 (STATUS.md line 237)

```text
; nothing else in §5 was unblocked (04.9.1-T2, 08.4.3-T2, 11.2.1-T4 and 11.2.1-T5 wait on 11.2.1-T6/T4, 05.9.2-T1 on all of 11.2.1)
```

### Item 10 (STATUS.md line 278)

```text
; the enqueuer is not wired so nothing triggers it yet (wired by S261, #538, 332c443a: completion enqueues `raw_report_parse`, events metered, queued run moved to running; T1.4 met); 04.4.1-T5 LANDED by S263 (#541, ecddae70, no migration): `contracts/adapter/junit-pytest-mapping.md` with its doc-drift test and the declared-vs-observed engine (`JUnitDeclaredObservedCheck`; `status_mismatch` stored, `malformed` derived by `EffectiveCodes`, T6 is its first caller); open items are carried rows titled `(S255)` and 'Non-JUnit root report is mapped, not refused' (closed by S264); 04.4.1-T3 LANDED by S264 (#545, ef2d8e9a: a non-JUnit root is refused as malformed, a refusal enqueues the run reconcile trigger); 04.4.1-T6 LANDED by S266 (#549, 1bbabccd: `GET /v1/runs/{r}/reports/{report_id}/comparison`, no migration; per-case discrepancies PARTIAL, carried row 'Per-case discrepancy attribution not stored (S266)'); 04.4.2-T1 reprocess: T1.1/T1.2 LANDED by S267 (#556, e46a4ccc: `POST /v1/runs/{r}/reprocess`, `ReprocessReportTask`, migration 0081 `raw_report_reprocess`, next free 0082), T1.3 (`reprocessed` label) open; verdicts do not change yet, carried rows tagged (S267)
```

### Item 11 (STATUS.md line 333)

```text
 T2 landed in #496 (migration 0070, 7222b2d1): kill task at `kill_at`, required cancel window config, `after_cancel` stamping; AC5/AC7 met; open items are carried rows titled `(S236)`. Run state history landed by S277 (#578, dfa94868): `run_state_event` (migration 0082, trigger-filled, no backfill) and `state_history` on `GET /v1/runs/{r}`; highest migration is `0082_orchestration_run_state_event.sql`, next free `0083`; open items are carried rows titled `(S277)`. The run read serves release bindings from the run row statement, capped at 500, landed by S286 (#588, dc39cb55); open items are carried rows titled `(S286)`.
```

### Item 12 (STATUS.md line 344)

```text
 (`GET /internal/operator/orgs/{o}/invoice-worksheet?period=`, per-line ledger rows plus corrections plus reconciliation, `issued` and `drifted_since_issue` for an issued period)
```

### Item 13 (STATUS.md line 344)

```text
 (`POST /internal/operator/billing/orgs/{o}/statements`, server-side recompute, 422 on a tampered total, idempotent replay, 409 on different content or a ledger row landing during the freeze)
```

### Item 14 (STATUS.md line 344)

```text
 Notes: amounts are used as priced with no price lookup; the plan file carries no T2.1 notes because editing a feature plan trips `check-dor-security` (§8 needs labelled authorization, telemetry and failure-path lines).
```

### Item 15 (STATUS.md line 345)

```text
 (`GET /internal/operator/orgs/{o}/cost?period=`, closed periods only; five lines `api_db_share`, `object_gb_month`, `egress_gb`, `ai_tokens`, `support_hours`, each a quantity, or null plus a reason when unmetered, never a fabricated 0)
```

### Item 16 (STATUS.md line 357)

```text
 (T2.1 merged #216, 1742764; T2.2 S146 #302 fbb2fe91; T2.3 + T2.4 PARTIAL, S272 #564 fd230b78: release panel and run-state timeline built, release id, state history and after_cancel count render unavailable until GET /v1/runs/{r} serves them, D-S272-Q1; T2 stays partial. Update S276 #568: the run read now serves `after_cancel_events` (#566) and the timeline shows it; release id and state history still unavailable; S278 #572: T2.3 landed, the panel reads `releases[]`, T2.4 still waits for state history, S277; S283 #583: T2.4 landed, timeline reads `state_history`; all four sub-rows landed, parent credited by the S299 recount, nothing left to build)
```

## Moved from status-carried.md and replaced in STATUS.md by S401 (2026-11-13)

Closed carried row, verbatim (closed by #811, c932d98c; decision D-S401-A: the 409 is documented as a defensive refusal on `createRun`, proven at registry level by `ProducerSlotTests`, wire-unreachable while run create issues a fresh run id; a future idempotent create path owns the wire test):

| `producer_slot` write path wiring (04.1.2-T4 hand-over, from S22) | Run-create registration and finalize slot-close wiring have landed. Remaining: document producer_slots_conflict (409) on the run-create contract; the original combined delivery claim is not reopened. | receiver: 04.1.2-T3; hand-over Notes: `plans/features/04-ingestion-adapters/04.1.2-quota-epochs-and-observability.md:191` |

STATUS §5 row replaced text (04.1.2-T3):

| 04.1.2-T3 | M | OpenAPI + integration tests for both stories. Partial: events operation documented (`contracts/openapi.yaml:1009,6848,7182`) and the 04.1.1+04.1.2 suite is on develop; the run-create 409 `producer_slots_conflict` still needs documenting. | yes | no | S97: `contracts/openapi.yaml`, `packages/sdk` | n/a | yes — remainder only |

## Moved from status-carried.md by S405 (2026-11-13)

| check-no-secret-literals hits in S398 files (S403) | `scripts/check-no-secret-literals.sh` reports 3 connstr-password hits (`tests/Hosts.IntegrationTests/ManagementListenerProcessTests.cs:19`, `ManagementSurfaceTests.cs:14`, `RequestBodyLimitTests.cs:17`), so the check-env-matrix stage exits 1 on the wave head; the matrix and inventory sub-check pass. Medium. | coordinator |

closed by #818 (4f8a2362)

## Moved from status-carried.md by S407 (2026-11-13)

| Secret inventory dev-stack runbook links (S250) | 10.1.2 AC1 asks for runbook links into 10.7.1-T4 sections; 13 rows in `infra/secrets-inventory.json` do, ten dev-stack rows link `docs/runbooks/environments.md` without an anchor because no 10.7.1-T4 section covers dev-stack credentials. AC1 is PARTIAL (#516). Add a dev-stack section or accept the file link. | 10.7.1-T4 |
| Secret inventory slug edge cases (S250) | `scripts/secrets-inventory.mjs` heading-anchor parser: a 3-backtick line closes a 4-backtick fence (accepts a ghost anchor, rejects a real one) and a duplicate-heading suffix collides with an existing `-1` heading. No referenced runbook triggers either today. Also split-token value evasion is an accepted heuristic limit. | 10.1.2-T3.2 or next owner of `scripts/secrets-inventory.mjs` |

closed by #820 (68358713)

## Moved from status-carried.md by S411 (2026-11-13)

| Cursor page shape not delivered (04.1.3-T1, S251) | AC4-AC6 of 04.1.3 are not delivered (#520 landed the caps half only, COORDINATOR-DECISION 251): no `{items, next_cursor}` page shape on the run-detail executions section, on the comparison list (04.4.1-T6) or on a `case-attempts` list route, no HMAC cursor, no reconciler paging. No migration needed. Task 04.1.3-T1 stays partial in STATUS §5. | receiver: coordinator (next slice for 04.1.3-T1); plan `plans/features/04-ingestion-adapters/04.1.3-large-report-caps-and-streaming.md` AC4-AC6 |

closed by #830 (1cc3b9be)

## Moved from status-carried.md by S409 (2026-11-13)

| Sixth bounded JSON reader copy (S404) | `GitHubResponseReader` in `src/Modules/Evidgate.Modules.Catalog/Infrastructure/GitHub/` is the sixth hand-written bounded-JSON body reader (same shape as the other five); agent-code-rules §7 calls a second copy of protocol logic a defect. Extract one shared reader when the next HTTP client lands. | next HTTP-client task (T1.4 or 10.x) |

closed by #826 (88ca42cf)

## Moved from status-carried.md by S406 (2026-11-13)

| Hard-kill reservation leak until budget reset (S402) | A process killed between the reserve and settle commits of a healing LLM call leaves that call's estimate counted in `healing_llm_budget` until the period resets; it only over-counts (the next reservation refuses sooner), never lets spend pass a cap. The reset is not built yet. Source: 08.5.1-T1.4 (#814, decision 402 A). | 08.5.1-T1.7 |
| Provider output ceiling and estimate overshoot (S402) | The adapter sends no max-tokens field, so the held `ReservedOutputTokens` is not enforced on the wire; real usage above the estimate is charged at settlement and a call can overshoot a cap by its excess. Source: security review F2, #814. | 08.5.1-T1.5 |

closed by #822 (c0609f31: `max_tokens` on the wire) and #828 (b66504d0: a leaked hold sits on its own day's row and the daily roll starts the next day at zero, so the leak is bounded to one period)

## Moved from status-carried.md by S410 (2026-11-13)

| Install-callback ownership check in 02.4.3 AC2 (from S21, 02.4.3-T1) | DECISIONS §8 O12 `[OPEN]` (recommended `[INFERENCE]`, ADR-0007 §Install binding): bind only when `installation_id` appears in `GET /user/installations` for the user who completed OAuth and that user administers the installation account, else 403 `installation_not_owned`; OAuth user token used once, never stored; what the install-time redirect carries is ⚠️ to confirm in 02.4.3-T2. AC2 and T2.2 in the 02.4.3 plan carry the recommendation | receiver: 02.4.3-T2; hand-over Notes: `plans/features/02-identity-tenancy/02.4.3-github-app-installation.md:110` |

closed by #835 (33cd2497)

## Moved from status-carried.md by S412 (2026-11-14)

| Run identity fork rules (05.2.2-T1 hand-over, S45) | `fork_run_not_bindable`, `advisory_only`, the `runs_created_total` counter and the no-opt-out test stay with T3 (the `run_project_ref_idx` index landed with 05.2.2-T2.2, migration `0038`, PR #106). Plan 05.2.2 Notes (line 163) | receiver: 05.2.2-T3; hand-over Notes: `plans/features/05-runs-orchestration/05.2.2-branch-pr-and-commit-identity-of-a-run.md:137` |

closed by #837 (f865f6bf)

## Moved from status-carried.md by S443 (2026-11-19)

| AI settings forbidden state on scope loss (S441, #910) | The delta review found ai-settings.page.ts:89 answers a generic error when scope() is lost (authoring:read revoked or project switched) instead of the forbidden state design UI42/UI44 require; ai-settings.page.spec.ts:282 accepts the generic state. Fix: map an undefined scope to a forbidden failure in the store and tighten the spec; receiver a 13.4.1-T5.1 follow-up (same feature folder). | web foundation |

closed by #917 (f00ae760: `ai-settings.page.ts` renders the forbidden pattern when `scope()` is lost, pinned by `ai-settings.page.spec.ts`)

## Moved from status-carried.md by S444 (2026-11-19)

| Authoring tree store: four open P2 races (S440) | authoring-tree-reads.ts: F1 :390 a continuation append overwrites rows a selection refresh updated (select, Load more, selection finishes first); F2 :187-192 select aborts the child read without settling loading, a failed select leaves the spinner and store.ts:129-130 refuses a retry; F3 :295-296 refresh of an exhausted branch keeps the null cursor; authoring.page.ts:98-104 F4 a failed select announces all roots. Receiver 13.4.1-T2.3/T2.4 before the wave reaches develop. | web foundation |

closed by #914 (3a3cde1b: `authoring-tree-races.spec.ts` pins F1-F4; fixes in `authoring-tree-reads.ts` and `authoring.page.ts`)

## Moved from status-carried.md by S446 (2026-11-19)

| Tailwind @source now covers authoring, availability, catalog (S443, #917) | Unreported global CSS change: `spartan-tailwind.css` (`source(none)` + hand-kept `@source` list) lacked `features/authoring`, `availability`, `catalog`, so their utilities were never generated: 34 authoring, 9 availability, 12 catalog class tokens missing from the built CSS (ce63d84e message). Computed styles measured on authoring only (design review DR-01, uncommitted notes): 75ch form `max-width:none`, `lg:last:col-span-2` `grid-column:auto`, 60vh cap `max-height:none`. Catalog and availability may change visibly; no before/after screenshots were taken for them (none). | receiver: web foundation (visual check of catalog and availability) |

closed by S446 (#919): no regression. 25 new utility selectors (authoring 17, catalog 5, availability 3); 56 shots, 7 routes x 360/1280 px x light/dark x two builds, no overflow; catalog identical but 2 px shorter, authoring spacing intended. Availability is unreachable (phase G3), see its new carried row.

## Moved from status-carried.md by S449 (2026-11-19)

| Authoring case table: delta findings open after the one fix round (S444, #914) | DeltaS444 on 6fc94c85..8df2c011: R1 `authoring-case-table.store.ts:109` a `page_without_new_rows` refusal clears `failedRead`, the error UI still shows Retry and `retry()` returns without a request; R2 `authoring-case-filters.ts:90-91` a malformed `item_id` in the URL is dropped, so the read widens to all items and Apply stays disabled; selection resets when `visibleRows()` changes above 200 loaded rows; D4 at 1280 px the rows stack (`fitToContainer` false) instead of the dense table; P3 retained rows grow without eviction and the seen-set is rebuilt per Load more. | receiver: 13.4.1-T2.3 follow-up; recorded owner: Tech Lead |
| Fixture validator $defs cap declared after first use (S447) | `apps/web/src/app/core/dev-fixtures/fixture-contract.support.ts:213` `MAX_LOCAL_REF_NODES` is declared after CONTRACT_DOCUMENT (line 109) is built eagerly and used at :199, so the 20,000-node cap is undefined at that time. Fix = move the constant above line 109 and add a test that a ref chain over the cap is refused. | receiver: web foundation |

closed by S449 (#926, sha: 7c2eaf52): non-advancing page ends paging without Retry, malformed item_id/node_id refuses with an invalid-link state, selection survives layout switch and Load more, retained rows capped at 10,000 with a notice, and `MAX_LOCAL_REF_NODES` moved above its first use with a refusing spec. D4 and the new P2 are recorded as new rows.

## Moved from status-carried.md by S450 (2026-11-19)

| Authoring XLSX import/export needs a binary-capable http adapter (S442, #915) | The import/export screens render XLSX as unavailable with a reason and never send an XLSX request: the `core/http` adapter is text-only (it decodes the response with `text()` and sends string bodies), so a binary export would be corrupted. A binary-capable adapter in `core/http` is its own T3-reviewed lane; until then the screens stay CSV and JSON only (`import.store.ts`, `export.store.ts`). | receiver: a `core/http` binary-transport task (owner decides the lane); recorded owner: Tech Lead |

closed by S450 (#925, sha: 0c6701dc): `core/http` has an opt-in binary path (`X-Evidgate-Transport` marker, 8 MiB cap while reading and downloading, JSON error envelope kept) and the authoring import/export screens offer XLSX through it; the 'unavailable' rendering and its strings are removed. Remaining parts are carried as rows S450 #925.

## Moved from status-carried.md by S448 (2026-11-19)

| Pending changes: delta findings open after the one fix round (S445, #921) | DeltaS445, three P2s from the fix. N1 `pending-changes.page.ts` openApply reuses the retained `applyRequest` after the selection changed, so Apply sends the old ids; lock selection while unresolved or offer Replay vs New apply. N2 `pending-changes.store.ts:158` keeps the queue ready during reload so Discard stays enabled and a late read reinstalls a discarded row; block discard while reading. N3 `checklist-review-tab.component.html:80,108` native maxlength counts UTF-16, the contract counts code points; drop it, keep the counters. | receiver: next authoring lane (13.4.1) |
| Review tab not mounted in the case editor (S445, #921) | `checklist-review-tab.component` (13.4.1-T3.3) is built, specified and fixture-backed but no route or editor mounts it; the editor lane (S443, 13.4.1-T3.1) owns the mount. Until then its queue and delete flows are covered by component specs only, not a browser spec. Fix = mount in the editor checklist item panel with the item id input and add an e2e for queue and delete. | receiver: authoring editor lane (13.4.1-T3.1 follow-up) |

closed by S448 (#924, sha: 112b6323): the editor mounts the checklist review tab (`editor.page.html` third tab, lazy, with the loaded case's `checklist_item_id`), apply replay refuses a changed selection, discard is refused during a retained reload, and the review textareas no longer carry a native maxlength (code-point validation and counters govern). Browser coverage: `e2e/shell/editor-review-tab.spec.ts`. Browser submission of comment and delete is not covered: carried as row 'Review tab: no browser spec submits a comment or a delete (S448, #924)'.

## Moved from status-carried.md by S451 (2026-11-19)

| Item | Blocker | Owner |
|---|---|---|
| Binary http path: security delta P2 (S450, #925) — fix before the wave PR to develop | S450SecDelta, four P2 (no P0/P1): (1) XLSX import sends `binary-request` only, so its response/error body takes the uncapped text path and `decodeBinaryErrorBody` returns a string error uncapped; (2) telemetry `trace.interceptor.ts:121` cannot read error code/retryable from an ArrayBuffer error; (3) a gzip bomb is buffered before the final length check (progress counts compressed bytes, `[INFERENCE]`); (4) a caller-supplied stream with non-Uint8Array chunks is accepted (`http-client-fetch-binary.ts` readAllBytes). | receiver: core/http owner; before the wave PR to develop |
| Binary http path: two Content-Length specs missing (S450, #925) | `http-client-fetch-binary.spec.ts` pins a header over the cap and a body over the cap with no progress, but no spec covers a Content-Length smaller than the real body (progress and final-length checks must still refuse it) or a non-numeric/empty Content-Length (ignored, final check applies); the logic exists in `http-client-fetch-binary.ts` advertisedSize and toBinaryResponse. | receiver: core/http owner |

closed by S451 (#929, sha: 5e6cfc4f): non-byte request chunks (including a forged `Symbol.toStringTag`) are refused with the reader cancelled; request-only binary calls cap the text reply and error body; Content-Length is read as any-length digits and the two missing specs are in `http-client-fetch-binary-caps.spec.ts`; the telemetry interceptor decodes a binary error body up to 64 KiB. Gzip handling stays open as row 'Binary http path: span outcome, chunk copy, wire-size cap (S451, #929)'.

## Moved from status-carried.md by S452 (2026-11-19)

| Authoring case table cap notice after denied refresh (S449, #926) | After the 10,000-row cap, a Refresh answered 403/404 clears the rows but leaves `browseCapped`, so the cap notice shows with zero rows (`authoring-case-table.store.ts:222-229`). Fix: clear the flag in the row-discard branch; pin with a forbidden-after-cap store test. | receiver: authoring web lane |
| Authoring tree walk and catalog case-list retention bounds (S449, #926) | Observed, not changed: catalog case-list paging keeps the same cursor-retention shape, and per-branch authoring tree walks have no row cap. The case table now caps at 10,000 rows with a notice. The design review of the new notices was source-only; no 360/1280 px visual check. | receiver: authoring/catalog web lane |
| Review tab: no browser spec submits a comment or a delete (S448, #924) | `e2e/shell/editor-review-tab.spec.ts` proves the mount, keyboard reach and unsent drafts only; the queue-comment and queue-delete flows are covered by component specs (`checklist-review-tab.component.spec.ts`, `checklist-review-tab.a11y.spec.ts`), not in a browser, so a wiring break between the tab and the editor route would pass. Fix = one e2e that queues a comment and a delete from the editor tab and asserts the request bodies. | receiver: authoring editor lane (13.4.1-T3 follow-up) |

closed by S452 (#932, sha: 9a94ed82): the case-table cap flag clears when a 403/404 refresh discards rows (store spec 'clears the cap notice after a %s refresh'); catalog list and authoring tree now cap at 10,000 retained rows through `shared/keyset-retention.ts` with notices and typed capped state, checked at 360/1280 px, light/dark, vi/en in `e2e/shell/browse-cap-notices.spec.ts`; `e2e/shell/editor-review-tab-submit.spec.ts` asserts the queued comment and delete bodies. Leftovers stay open as rows 'Authoring cap announcer ignores childless nodes (S452, #932)', 'app-live-status has no gallery specimen (S452, #932)' and 'Browse cap e2e drives 10,000 real rows (S452, #932)'.

## Moved from status-carried.md by S455 (2026-11-19)

| Authoring cap announcer ignores childless nodes (S452, #932) | `cappedBranchOnScreen` (`authoring.page.ts:143-147`) walks expanded nodes but not the renderer's `child_count !== 0` guard, so a roots refresh that returns an expanded capped node with `child_count: 0` announces a load-limit warning for a branch and notice that are not rendered. Fix: apply the guard before inspecting or descending; add a page spec. Found by the S452 delta check after the one fix round. | receiver: authoring web lane |
| app-live-status has no gallery specimen (S452, #932) | `shared/ui/states/live-status.component.ts` has its catalogue row in `docs/engineering/ui-rules.md` and a component spec, but no `/dev/ui-gallery` specimen, which A15 requires for a new shared component. Add the specimen (and any gallery spec pin) in `shared/ui/gallery/`. | receiver: shared/ui owner |
| Browse cap e2e drives 10,000 real rows (S452, #932) | `e2e/shell/browse-cap-notices.spec.ts` reaches each cap through 50-200-row cursor pages (no product cap override). Measured: tree 3.0-3.1 min per locale, budget 240 s; case table 47 s alone, ~85 s in the whole shell run, budget 180 s; catalog 11 s. Option: make `CASE_ROWS_CAP` an injectable token so e2e seeds a small cap (a product change; store specs already prove boundaries at the real constant). | receiver: authoring web lane |

closed by S455 (#937, sha: eb99a7cd): cappedBranchOnScreen and the tree pane keyboard walk skip child_count 0 (authoring.page.spec.ts, authoring-tree-pane.component.spec.ts); app-live-status specimen in gallery-states.component.ts; RETAINED_ROWS_CAP token (default 10,000) with the only override in dev-backend.fixtures.ts, browse-cap-notices e2e seeds 25 and every case runs 1.6-4.3 s with no raised timeout.

## Moved from status-carried.md by S454 (2026-11-19)

| Review tab lacks parent checklist context and row provenance (S448, #924) | `checklist-review-tab.component.html:1-3,17-25` shows no parent item identifier or related cases (design `authoring-editor.md` row 8) and omits author, source and pending state per row; the editor passes `view.checklist_item_id` but only the id reaches the tab. Fix = render the parent id and the supplied facts with explicit unavailable states (no fabricated projections), reuse shared value/status primitives. | receiver: authoring editor lane (13.4.1-T3 follow-up) |
| Review tab and editor: duplicate narrow notice (S448, #924) | At 360 px the desktop-required notice renders twice (`editor.page.html` notice and `checklist-review-tab.component.html:3`). Fix = keep the notice owned by the editor composition and drop the tab's copy, with a 360 px spec. Minor, no behaviour impact. | receiver: authoring editor lane (13.4.1-T3 follow-up) |
| Review tab and pending queue: unbounded paste count and unfiltered queue read (S448, #924) | Perf P3s: `Array.from` code-point counting of a pasted comment or reason has no input bound (`checklist-review-tab.component.ts` limit check); the review tab reads the 200-row pending queue unfiltered and filters by parent item client-side. Fix = bound the counted slice at limit+1 and request it filtered by `target_id` (`contracts/openapi.yaml:7298`). | receiver: authoring lane (13.4.1) |

closed by S454 (#936, sha: aae8e25c): the review tab shows the parent item identifier and per-row change id, author membership id, source, state, base version, UTC time and reason with explicit 'Not provided' states (checklist-review-tab.context.spec.ts); the editor owns the single narrow notice (editor-review-tab-narrow.spec.ts); code-point counting stops at limit+1 (checklist-review-tab.bounds.spec.ts) and the queue read carries target_id through the store scope (editor-review-tab.spec.ts request log, fixture-authoring-pending.spec.ts).

| Binary http path: span outcome, chunk copy, wire-size cap (S451, #929) | Open P3s from the S451 security review: an over-cap success span ends ok (interceptor runs before the adapter cap); request chunks held by reference until the final copy; text replies measured as re-encoded UTF-8; DownloadProgress.loaded under Content-Encoding unverified per browser, so a gzip body is bounded only by the post-hoc length check. | receiver: core/http owner |

closed by S457 (#939, sha: 3d5c2c6e): the span records an over-cap byte reply as error/network (trace.interceptor.binary.spec.ts); request chunks accumulate into one capped growing buffer with the intrinsic byteLength getter (http-client-fetch-binary-bounds.spec.ts); the size check is shared by adapter and interceptor and counts decoded bytes, with a non-finite loaded failing closed; the string error body is bounded to 64 KiB UTF-8 before JSON.parse; an unaborted EmptyError maps to the network error. Remaining residuals are in the row "Binary http path: text-reply span outcome (S457)".

## Moved from status-carried.md by S466 (2026-11-20)

Closed carried row, verbatim (closed by #957, aeeab06f: GetEvaluationRunHeaderAsync_ReportsIsForkTrueForAForkRunAndFalseForANormalRun and GetEvaluationRunHeaderAsync_WithACancelledToken_ThrowsInsteadOfAnsweringTheHeader in OrchestratorEvaluationPlanTests; mutation run showed both fail with IsFork forced false and the token dropped):

|Decision header read optional tests (S464)|Two optional tests were not added in #951: cancellation propagation of `GetEvaluationRunHeaderAsync`, and a header assertion with `IsFork` true (every current assertion expects false, so a constant-false mapping would survive). Add both to `OrchestratorEvaluationPlanTests`.|receiver: backend lane|
| CR-13 slice B: rate-limit windows in PostgreSQL, evict instead of refuse, per-surface limiter options (S377) | `ExchangeRateLimiter` windows stay local to each process, so with N replicas the limits are N times looser, and at 50,000 keys it refuses every new key shared across login and CI. Slice A (#768) moved only the key ring and the login replay set. | receiver: coordinator (next CR-13 lane) |

closed by S461 (#958, sha: ee2cd9b7): windows are in PostgreSQL (migration 0110, `evidgate_resolve.rate_limit_acquire`), eviction replaces refusal at the key bound, and login, link, invite and CI exchange have their own validated options (`Identity:RateLimits:*`); a store outage answers 503. Residuals are the carried rows added in the same commit.

## Moved from status-carried.md by S471 (2026-11-20)

Closed carried rows, verbatim (closed by #965, 120cc9ac: `readFileBounded` takes a `verify` callback that runs before the close and `playwrightList` checks lstat identity plus realpath equality there, the residual race is the carried row "Report read residual race (S471)"; the case-id validator is kept two by design, drift pinned by test, coordinator decision 471 B: `cli/src/caseId.ts` and `sidecarRules.ts` stay, and `cli/src/caseId.table.json` drives both tests):

| Report identity check after close (S467) | `readFileBounded` compares dev/ino after the descriptor closes, so a replaced file can reuse the inode and pass; `O_NOFOLLOW` covers only the last path component. Move the check before the close and re-check the parent path. Fix commit 1e1bbde2 only keeps the old inode alive in the test | receiver: T3.1b CLI lane |
| Case-id validator duplicate (S467) | `packages/cli/src/playwrightList.ts` carries a second canonical case-id regex because the CLI may not import the adapter; `adapter-playwright/src/sidecarRules.ts` owns the first. Move it into one shared CLI module the adapter imports (it already depends on the CLI) | receiver: T3.1b CLI lane |

## Moved from status-carried.md by S470 (2026-11-20)

Closed carried rows, verbatim (closed by #966, faee2c0b: the outage row by `SharedRateLimitOutageTests` (link, invite, CI exchange answer 503 with Retry-After on a store outage); the startup-refusal row by `OptionsValidationTests.Invalid_Rate_Limit_Option_Fails_Startup_Cleanly` (Production and Development, exit 1, named key, no stack trace), its `TimeoutException` half stays open as the carried row "CR-13 slice B: TimeoutException clause not separately observable (S470)"; the concurrent row by `RateLimitStoreFaultTests` (parallel new keys at the bound plus the exact-bound Fact); the mutation row ran on 59e01b78 against the unchanged migration 0110: removing the on-conflict update failed 10 C# tests, disabling eviction failed 6):

|CR-13 slice B: outage 503 tests for link, invite, CI exchange (S461)|Only the login surface has an HTTP test of the fail-closed 503 with Retry-After on a store outage (`Hosts.IntegrationTests`); `RateLimitGateTests` covers the helper, and the other three endpoints rely on the shared helper without an HTTP-level test.|receiver: coordinator (next CR-13 lane)|
|CR-13 slice B: startup-refusal host test and TimeoutException branch (S461)|`RateLimitOptionsValidator` is unit tested, but no host test shows the Api refusing to start on a bad limiter option, and no test drives the `TimeoutException` branch of `RateLimitGate` (for example with pg_sleep).|receiver: coordinator (next CR-13 lane)|
|CR-13 slice B: concurrent new-key-at-bound test (S461)|No test runs many callers inserting new keys at the key bound in parallel to show the bound converges and eviction never removes the caller; the sampled eviction is only covered sequentially (`RateLimitStoreTests`).|receiver: coordinator (next CR-13 lane)|
|CR-13 slice B: C# mutation evidence not run (S461)|Acceptance 9 was shown only on a throwaway local PostgreSQL 17 (removing the on-conflict update breaks counting; disabling eviction leaves 200 rows at bound 10), not by running the C# tests against a mutated migration, and the two-instance test was not run on the old wave head (the in-process limiter cannot pass it). One LAN run of a mutated migration closes it.|receiver: coordinator (LAN round)|

## Moved from status-carried.md by S473 (2026-11-20)

Closed carried rows, verbatim (closed by #971, 5653cc73: the Worker row by `OptionsValidationTests.Invalid_Rate_Limit_Option_Fails_Worker_Startup_Cleanly` and a LAN mutation that removed the Worker call and failed all 6 cases; the clause row by `RateLimitGateFaultClauseTests`, whose mutations of the NpgsqlException and TimeoutException clauses each fail their own tests).

|CR-13 slice B: Worker eager rate-limit validation (S470) | `ApiHost.ValidateOptions` now materialises `RateLimitOptions` through `RateLimitOptionsValidation` (clean exit 1, named key, no stack trace, proven in Production by `OptionsValidationTests.Invalid_Rate_Limit_Option_Fails_Startup_Cleanly`). `WorkerHost.ValidateOptions` lacks the same call although the Worker also calls `AddIdentityModule`: a bad value still refuses via `ValidateOnStart` but logs a start-failure stack trace. Add the call plus a Worker refusal case (decided: not this lane) | receiver: coordinator (Identity/hosts lane) |
|CR-13 slice B: TimeoutException clause not separately observable (S470) | The held-lock test (`RateLimitStoreFaultTests`) proves a stalled store answers 503 within the 15 s bound and recovers, but not whether the `NpgsqlException` or the `TimeoutException` clause of `RateLimitGate.AcquireAsync` fired; that needs a seam in src (an injectable command executor) and no test can tell them apart today | receiver: coordinator (src seam, Identity lane) |
