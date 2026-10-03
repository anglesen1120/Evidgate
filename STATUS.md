# Evidgate — delivery status

Updated 2026-10-03. Source of truth for tasks: `plans/01`–`12` and `plans/features/`; this file tracks execution only. Task ids and sizes come from `scripts/plan_graph.py`.

## 1. Snapshot

| Item | State |
|---|---|
| Branch flow | `feature/*` or `docs/*` → PR into `develop` → owner merges. `main` = planning baseline |
| `develop` head | `db4eb44` (PR #12 merged) |
| Merged PRs | #1 plans + stack (CR-D-1) · #2 M0 foundation · #3 M1 wave 1–2 · #4, #5 plan notes (Docker on runner) · #6 M1 wave 3 · #7 owner decisions · #8 owner decisions → develop · #9 M1 wave 4 · #10 M1 wave 5 · #11 M1 wave 6 · #12 M1 wave 7 |
| Open PRs | none |
| Active branch | `develop` — Stage A started 2026-10-03: stories 02.1.1, 02.6.1 + 02.6.2 and 02.7.1 complete (on `develop`, §2); wave 7 (§3.7) is merged |
| M0/M1 tasks | 57 done · 77 open (of 134; 10.2.1-T1 counted open until T1.4); plus 02.1.1-T3 (M2) done |
| Plan checks | `plan_graph.py` errors 0 · 592 tasks · 628.25 raw / 753.9 buffered person-days (+55 tasks / +45 d from the 2026-10-02 review incl. gap stories 03.1.4, 03.9.x, 04.1.3, 06.3.6, 06.4.6, 10.5.2, 10.7.2, 10.11.1, 11.5.2, 11.9.2: 02.4.3, 02.10.1, 04.9.1, 04.9.2, 05.2.2, 05.5.2) |
| CI | `.github/workflows/ci.yml` runs every `verify.sh` stage (01.7.1-T1); interim `docker-tests.yml` deleted in wave 5 |
| Owner decisions | Decided 2026-10-02 (chat): O1 interviews waived (pilot profile in `docs/decisions/O1-pilot-profile.md`), O2 team = the owner alone, O4 = Evidgate-provided AI credit through an Evidgate-controlled gateway, O10 = (a). O3 still open. Decided 2026-09-26: O1 engine = Playwright (UI), customer CI = per-project input. Open before M1: O1 pilot names, O2 (owner note recorded, reading unconfirmed). Open before 08.5 DoR: O4 (owner note "tự build hệ thống riêng", model-or-not unconfirmed) — DECISIONS §8 |

## 2. Done

| Story | Tasks | PR | What shipped |
|---|---|---|---|
| 01.1.1 | T1–T3 | #2 | Toolchain pins, workspace skeleton, layout check |
| 01.2.1 | T1–T5 | #2 | Analyzers, format, strict TS, `tests/Architecture.Tests` |
| 01.3.1 | T1–T3 | #2 | `contracts/openapi.yaml`, `contracts/schemas/`, `contracts/adapter/` |
| 01.9.1 | T1–T3 | #2 | ADRs in `docs/adr/` incl. ADR-0002 licence matrix |
| 01.4.1 | T1–T4 | #3 | `Evidgate.Migrator`, migrations 0001–0003 (roles, RLS baseline), `db-init.sh` |
| 01.1.2 | T1–T3 | #3 | Api/Worker hosts, 11 module pairs, skeleton slice |
| 01.8.1 | T1–T2 | #3 | SeaweedFS parity tests, compose dev stack, `scripts/dev/up.sh` |
| 01.4.2 | T1 | #6 | Tenant scope, EF/Dapper binding, interceptor |
| 01.3.2 | T4 | #6 | RFC 8785 canonical JSON hashing (.NET + CLI) |
| 01.6.1 | T1 | #6 | Test conventions (.NET + Vitest) |
| 02.1.1 | T1 | #6 | `organization` / `project` migrations 0004/0005 |
| 01.4.2, 01.5.1, 01.6.1 | 01.4.2-T2/T3, 01.5.1-T1/T2/T6, 01.6.1-T2/T5 | #9 | `outbox`/`idempotency_record`/`task` (migrations 0006–0008), claim/consumer/schedule mechanism, schema/EF conventions + UUIDv7 generator, Testcontainers harness, Playwright workspace — see §3 |
| 01.4.2, 01.5.1, 01.7.1 | 01.4.2-T4, 01.5.1-T3/T4/T5, 01.7.1-T1 | #10 | C2 Mediator pipeline + Result→§9.2 filter, outbox dispatcher (migration 0009), retry/dead-letter + `/internal/operator` routes, AC1–AC7 suites, real `ci.yml` — see §3.5 |
| 01.3.2, 01.6.1, 01.7.1 | 01.3.2-T1/T2/T3, 01.6.1-T3/T4, 01.7.1-T2/T3.2/T4 | #11 | Generated TS SDK + drift gate, Corvus/Ajv dual-runtime validators + parity corpus + endpoint filter, contract tests + served-doc diff, `fixtures/` + loaders, tenant-isolation suite, licence/audit/SBOM/dependabot gates — see §3.6 |
| 02.1.2, 02.2.1, 02.6.1, 02.9.1, 04.7.1, 05.1.1, 10.1.1, 10.2.1, 10.8.1, 11.1.1 | 02.1.2-T1, 02.2.1-T1/T2, 02.6.1-T1, 02.9.1-T2, 04.7.1-T1, 05.1.1-T1, 10.1.1-T1, 10.2.1-T1.1–T1.3, 10.8.1-T1, 11.1.1-T2 | #12 | Permission catalogue + evaluator, membership API, RLS assertions + boot self-check, composite FK suite, adapter/manifest schemas, environment matrix, container images + SBOM, provider memo, DoR checklist — see §3.7 |
| 02.1.1 | T2, T3 | `develop` (Stage A, no PR) | Org/project registry API (`POST /v1/orgs`, `GET,PATCH /v1/orgs/{o}`, `GET,POST /v1/orgs/{o}/projects`, `GET,PATCH /v1/projects/{p}`), founder membership (T98), migrations 0012 (D-A1 slug) + 0013 (quota columns), `ITenantLimits`, `[AuthenticatedMember]` gate; 19 new tests — feature plan 02.1.1 §3/§8a/§9 |
| 02.6.1, 02.6.2 | 02.6.1-T2/T3, 02.6.2-T1 | `develop` (Stage A, no PR) | RLS/SECURITY DEFINER lint (`scripts/check-rls.py`, stage `check-rls`), migration 0014 (`member_orgs`, `member_org`, `member_organizations`), `TenantResolutionFilter` + `GET /v1/orgs`, service-role claim ceiling, `missing_tenant` dead-letter; 662 .NET tests |
| 02.7.1 | T1, T2, T3 | `develop` (Stage A, no PR) | `audit_log` chain (migration 0015, chain-guard trigger), `IAuditTrail` + `[AuditedAction]` in the C2 pipeline (every Identity/Tenancy mutation), `GET /v1/orgs/{o}/audit` + `/audit/export`, `audit.verify_chain` task; 706 .NET tests |

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

Carried items whose owner task lands in this wave: the `task`/`outbox` operator read path (§6 → H); `docker-tests.yml` deletion (§6 → C).

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

## 5. Ready next (dependencies met, not started)

Recomputed 2026-10-02 from `plan_graph.py` after the review additions, de-duplication and ordering fixes; 2026-10-03: 02.1.1-T2 removed (done), 02.4.1-T1 and 02.9.1-T1 added. 02.6.2-T1 must exempt `POST /v1/orgs` from tenant resolution (the endpoint publishes the new org's id itself, 02.1.1 §3), and 02.6.1-T3 maps `GET /v1/orgs`. Order follows Stage A of `plans/features/SOLO-PATH.md`; open items that must be settled first are in `plans/open-items/`.

**Stage A, ready now (build order):**

| Task | Size | Milestone | Note |
|---|---|---|---|
| 02.4.1-T2 | M | M1 | `ci_binding` model + admin API; unblocks the `seed-dev` bootstrap (01.8.1-T3) |
| 02.4.1-T1 | L | M1 | Newly ready 2026-10-03 (02.1.1-T2 done): GitHub OIDC JWT validator |
| 02.9.1-T1 | M | M1 | Newly ready 2026-10-03: API isolation suite over the registry routes |
| 03.1.1-T1 | L | M1 | Stable case identity (critical chain) |
| 05.1.1-T3 | M | M1 | Manifest |
| 04.1.2-T4 | M | M1 | `producer_slot` |
| 04.2.1-T2 | S | M1 | New: CLI skeleton + `EVIDGATE_DEV_TOKEN` dev path |
| 04.3.1-T1, 04.7.1-T2 | M each | M1 | Playwright reporter, adapter protocol |
| 07.1.1-T1, 07.3.1-T1 | M each | M2 | Policy revision, waiver/quarantine tables |
| 06.1.1-T1 | M | M2 | Artifact record |

**Next after Stage A starts (blocked only by the rows above):** 01.8.1-T3 `seed-dev` (needs 02.4.1-T2) · 02.3.1-T1 login (ready, but Stage B) · 01.7.1-T3 (T3.1 branch protection owner-blocked, §6).

**Ready but outside Stage A (do not start before the Stage B decision point unless needed):** 02.2.1-T3 · 02.10.1-T1 · 04.4.1-T1 · 08.6.3-T1 · 09.1.1-T1/T2/T6 · 09.7.1-T1/T2 (Stage B) · 10.1.1-T2/T4 · 10.1.2-T2 (push protection, XS, a setting) · 10.2.1-T1 rest after T1.1–T1.3 · 10.3.1-T1 · 10.7.1-T1 · 10.8.1-T2 and 10.11.1-T3 (O3 deferred) · 11.1.1-T1 · 11.2.1-T1 · 11.6.1-T1/T3 (now S) · 11.7.1-T2 · 11.8.1-T1/T2 (counsel) · 12.2.1-T2 · 12.3.1-T1 · 12.6.1-T1 · 01.10.1-T1 · V2/V3: 01.1.1-T4, 04.5.1-T1, 05.7.1-T1.

**Waived:** 12.1.1-T1/T2 customer interviews (O1 decided 2026-10-02: pilot profile in `docs/decisions/O1-pilot-profile.md`; real reports come from pilot zero, the owner's own pipeline).

## 6. Blocked / carried items

| Item | Blocker | Owner |
|---|---|---|
| `seed-dev` script | Needs 07.1.1 tables (02.1.1/02.1.2 landed) | Tech Lead |
| Web app | 09.1.1 (M2) | Lane C |
| `docker-tests.yml` deleted | Replaced by `ci.yml` in 01.7.1-T1 (wave 5): `global-json-file` SDK pin, single push+PR trigger, `ubuntu-24.04`, SHA-pinned actions, `check-ci-parity.sh`/`check-action-pins.sh` | done (wave 5) |
| `evidgate_owner` permissive policy on `organization`/`project` | Rationale logged in 00 §15 and in the `0003` copy block; 00 §5.4.3 still lacks the sentence | Tech Lead |
| `/internal/*` public-ingress routing rule | `[OPEN]` in 01.1.2 feature plan, no plan-10 task | Tech Lead |
| O1 pilot names; O2 and O4 readings | Owner confirmation (O1 engine decided 2026-09-26) | Owner |
| Branch-protection API (01.7.1-T3.1) | Private repo on the free plan: `PUT .../branches/*/protection` → 403 "Upgrade to GitHub Pro or make this repository public". The file half (CODEOWNERS, PR template) ships in wave 6; enforcing rules need an owner decision (Pro upgrade or public repo) | Owner |
| T80 purge-call audit for `evidgate.purge_expired_idempotency` | `audit_log` lands in 02.7.1 (M2); until then the handler logs at Information (recorded in `migrations/README.md`) | 02.7.1 |
| Audited operator read path for `task`/`outbox` (T76 grant gate) | T4.2 ships the read path on the app-role connection behind a fail-closed `IOperatorGate`; the audited `SECURITY DEFINER`/grant form lands with the real `platform_operator` credential in 02.x (0008's header still describes the dedicated-grant shape — read it as the target, not the shipped state) | 02.x (operator auth) |
| `SchemaConventionTests` exemptions: `rls_probe` (pre-convention template, checksum-locked) and `schema_migration` (02 §4) fully exempt; `task` exempt TenantKey/CompositeForeignKey/RowLevelSecurity only (AuditColumns/VersionTrigger stay gated); `outbox` exempt RLS only (`org_id` unique key enforced) | Lint enforcement belongs to 02.6.1-T2, which must adopt the same exemption list | 02.6.1-T2 |
| Owned EF entity types (OwnsOne/OwnsMany) fail the model build under `TenantModelConventions` (shadow-FK rename breaks table sharing) | Latent: no M1 entity is owned; fix sketch recorded in QA report (skip owned key/FK properties, derive names per-table) | 01.4.2 follow-up / next owner |
| Node `JSON.parse` escaped-key defect (upstream V8 regression, 24.x/26.x warm process) | upstream fix landed in V8, awaiting a Node release; the canonicalize guard stays as a partial mitigation | 01.3.2 / Node upgrade |
| `idempotency.purge` daily cap: `PurgeMaxBatchesPerFire = 50` × 1000 = 50 000 rows/day ceiling; a deployment above that accumulates `idempotency_record` forever | Needs a drain-until-time-budget or restartable cursor (perf review, 01.5.1 §7) | 01.5.1 follow-up |
| `01.7.1-T3.1` branch protection + required check names | API returns 403 on the free private repo (owner decision needed: Pro upgrade or public repo); required-check names must match `ci.yml` job names | Owner |
| Per-operation request/response body diff in `ServedDocumentTests` | Zero shared paths today (`/v1/slice/run` is the only served route); `contracts/openapi.yaml` path items use folded block scalars the strict `YamlSubset` reader refuses — parsing lands with the first real route | 04/05 ingest routes |
| `ci.yml` security stage | Feature plan §7 reserves a scaffolded-but-empty security stage; nothing to run in it yet | 11.6.1 |
| 10.2.1-T1.4 web bundle / CDN publishing | Needs 09.1.1 (web app) and object-storage publishing | 09.1.1 |
| Service-principal claim bound (W7-SEC-06) | `PermissionEvaluator`'s service path accepts any catalogue code; no reachable path until the token resolver exists. Bound claims to the service-kind roles (`ci_producer`, `agent`) and deny staff-only codes (`billing:*`, `support:grant`) | 02.6.2 |
| Last-admin guard under extreme skew | `membership_role_role_idx` (0011) drives the guard from the role (0.031 ms, 2 rows read at 200×250); on one 50k-member org the planner may still seq-scan (role_id selectivity estimated 1/ndistinct) — a constant/InitPlan role id would be needed (W7 perf review) | 02.1.2 follow-up |
