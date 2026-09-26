# Evidgate — delivery status

Updated 2026-09-26. Source of truth for tasks: `plans/01`–`12` and `plans/features/`; this file tracks execution only. Task ids and sizes come from `scripts/plan_graph.py`.

## 1. Snapshot

| Item | State |
|---|---|
| Branch flow | `feature/*` or `docs/*` → PR into `develop` → owner merges. `main` = planning baseline |
| `develop` head | `73f957b` (PR #9 merged) |
| Merged PRs | #1 plans + stack (CR-D-1) · #2 M0 foundation · #3 M1 wave 1–2 · #4, #5 plan notes (Docker on runner) · #6 M1 wave 3 · #7 owner decisions · #8 owner decisions → develop · #9 M1 wave 4 |
| Open PRs | none |
| Active branch | `feature/m1-wave5` (off `develop` `73f957b`); wave 5 delivered, gates green, PR pending |
| M0/M1 tasks | 39 done · 95 open (of 134) |
| Plan checks | `plan_graph.py` errors 0 · 502 tasks · 560.25 raw / 672.3 buffered person-days |
| CI | Interim `.github/workflows/docker-tests.yml` (88/88 green on hosted runner, run 36205176754); replaced by `ci.yml` in 01.7.1-T1 |
| Owner decisions | Decided 2026-09-26: O1 engine = Playwright (UI), customer CI = per-project input. Open before M1: O1 pilot names, O2 (owner note recorded, reading unconfirmed). Open before 08.5 DoR: O4 (owner note "tự build hệ thống riêng", model-or-not unconfirmed) — DECISIONS §8 |

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
| 01.4.2, 01.5.1, 01.7.1 | 01.4.2-T4, 01.5.1-T3/T4/T5, 01.7.1-T1 | pending | C2 Mediator pipeline + Result→§9.2 filter, outbox dispatcher (migration 0009), retry/dead-letter + `/internal/operator` routes, AC1–AC7 suites, real `ci.yml` — see §3.5 |

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

## 3.5. Wave 5 (delivered on `feature/m1-wave5`; gates green, PR pending)

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

Deferred to first CI run on GitHub (by design, AC-level): a green trivial-PR run, cache-hit annotations, <10 min wall time, Testcontainers on the hosted runner (the deleted docker-tests.yml already proved Docker works — run 36205176754).

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
| Org creation must seed the new UUIDv7 as `evidgate.org_id` **before** the `CreateOrganization` insert: 0004's app policy is `for all … using (id = evidgate.current_org_id()) with check (id = evidgate.current_org_id())`, so an insert as `evidgate_app` passes the check only under a scope opened for the id being inserted | 02.1.1-T2 |
| `contracts/openapi.yaml` has no `Organization`/`Project` component schemas **and no `POST /orgs` at all**; `createProject` (1005-1040) sends an inline `{name}` with no `slug` — although `project.slug` is NOT NULL with `project_slug_format_check` — and answers inline `{project_id}`, not the `ProjectDto` shape, so the contract cannot see either DTO | 02.1.1-T2 |
| Slug grammar `[OPEN]`: the shipped check `^[a-z0-9][a-z0-9-]{1,62}$` accepts a trailing hyphen (`ab-`); the owner decides whether to tighten it before the validator pins it | 02.1.1-T2 (owner) |
| Node 26.10.0 can mis-decode control-escape member names in a warm `JSON.parse` (ADR-0002 WARN row). The CLI's self-consistency guard withholds 64–78 of 27,146 generated documents as a partial mitigation; a `jsCanonical` recorded in a long-lived process must be recomputed in a fresh one before it is trusted | Node upgrade / 01.3.2 |
| Keyset split **landed**: `TenancyReadSql.ProjectsFirstPage` carries no cursor predicate and `ProjectsNextPage` compares `(created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)` with both parameters required, so the comparison stays an `Index Cond`; the single `@cursor_created_at is null or …` statement it replaced showed a residual `Filter` under a generic plan (measured 110x: 2.93 ms → 0.026 ms at 25 000 rows in on 50 000). The 02.1.1 plan §"Recorded" note still describes the residual-Filter shape | 02.1.1-T2 statement budget |
| Canonicalizer cost **landed** and still to be recorded in the plans: duplicate detection now compares each sorted name with its predecessor and the per-object `HashSet` with its `char[]`/string copies is gone (`JsonCanonicalizer.cs:198`) — the perf review measured 12.5 MB/op → 8.3 MB/op for a 1 MiB record doc and 35.8 MB → 19.1 MB for 100 000 single-member objects on that variant. Nested member values are still re-parsed once per enclosing level (3.0x the flat cost: 0.92 MB in 47.8 ms vs 16.0 ms — a deliberate CPU-for-memory trade), hashing a `JsonElement` re-materialises the text (+3.1 MB per MiB) and the CLI round-trip guard costs 2.44x (8.26 → 20.15 ms) | 01.3.2 §8 |
| `version` bump **landed** as a database-owned trigger: `public.set_updated_at_and_version()` (created in 0004, `EXECUTE` granted to `evidgate_app`) sets `updated_at = now()` and `version = old.version + 1`, attached as a `before update` trigger on `organization` (0004) and `project` (0005), and `TenantModelConventions` maps `version` as `ValueGenerated.OnUpdate` so EF reads the server's value back. 00 §5.5 C4 and §7 still read as `IsConcurrencyToken` alone | 00 §7/§5.5 C4 wording |
| `delete` is revoked from `evidgate_app` on `organization` and `project` (0004:127, 0005:96) because 0001's default privileges grant DELETE on every table the owner creates in `public`; `migrations/README.md`'s role table and the `0003` copy block (items 5 and 7) still prescribe the old operator policy and the DELETE grant for new tables | Tech Lead / next table author |
| Per-request EF context creation measures 0.109–0.140 ms and 56.2 KiB (the first command on a fresh context +0.55–0.7 ms) — noise against the p95 < 1 s ingest budget, but one line in the 01.4.2 plan keeps a future handler from creating a context per row | 01.4.2 |

## 5. Ready next (dependencies met, not started)

| Task | Size | Milestone | Note |
|---|---|---|---|
| 01.3.2-T1 | L | M1 | Generated SDK |
| 01.7.1-T2 | M | M1 | Licence gate (ready: `ci.yml` landed — `licence-policy.json`/`licence-scan.sh` per 01.7.1 §5) |
| 01.7.1-T3 | S | M1 | Branch protection, CODEOWNERS, PR template (ready: T1 landed) |
| 01.7.1-T4 | M | M1 | Supply-chain hardening: `dependabot.yml`, `audit-deps.sh`, SBOM (ready: T1 landed) |
| 01.6.1-T3, 01.6.1-T4 | M each | M1 | Harness follow-ups (ready: T2 harness landed) |
| 01.3.2-T2 | L | M1 | Validators |
| 01.8.1-T3 | M | M1 | Dev stack follow-up |
| 02.1.2-T1, 02.2.1-T1, 02.4.1-T2, 02.9.1-T2 | M each | M1 | Identity area, on 02.1.1-T1 |
| 02.6.1-T1 | L | M1 | RLS lint / policy helpers |
| 03.1.1-T1 | L | M1 | Stable case identity (critical chain) |
| 04.1.2-T4 | M | M1 | `producer_slot` table + registry |
| 04.7.1-T1 | M | M1 | |
| 05.1.1-T1 | L | M1 | |
| 10.1.1-T1 | M | M1 | |
| 10.2.1-T1 | L | M1 | Container images + SBOM |
| 10.8.1-T1 | S | M1/M2 | Provider decision memo (O3) |
| 11.1.1-T2, 11.2.1-T1, 11.8.1-T1, 11.8.1-T2 | S, M, L, M | M1 | Security area |
| 12.1.1-T1, 12.1.1-T2 | L, M | M0 | Customer interviews (owner) |
| 12.2.1-T2, 12.6.1-T1 | M, M | M1 | Commercial area |

## 6. Blocked / carried items

| Item | Blocker | Owner |
|---|---|---|
| `seed-dev` script | Needs 02.1.1 / 02.1.2 / 07.1.1 tables | Tech Lead |
| Web app | 09.1.1 (M2) | Lane C |
| `docker-tests.yml` deleted | Replaced by `ci.yml` in 01.7.1-T1 (wave 5): `global-json-file` SDK pin, single push+PR trigger, `ubuntu-24.04`, SHA-pinned actions, `check-ci-parity.sh`/`check-action-pins.sh` | done (wave 5) |
| `evidgate_owner` permissive policy on `organization`/`project` | Rationale logged in 00 §15 and in the `0003` copy block; 00 §5.4.3 still lacks the sentence | Tech Lead |
| `/internal/*` public-ingress routing rule | `[OPEN]` in 01.1.2 feature plan, no plan-10 task | Tech Lead |
| O1 pilot names; O2 and O4 readings | Owner confirmation (O1 engine decided 2026-09-26) | Owner |
| T80 purge-call audit for `evidgate.purge_expired_idempotency` | `audit_log` lands in 02.7.1 (M2); until then the handler logs at Information (recorded in `migrations/README.md`) | 02.7.1 |
| Audited operator read path for `task`/`outbox` (T76 grant gate) | T4.2 ships the read path on the app-role connection behind a fail-closed `IOperatorGate`; the audited `SECURITY DEFINER`/grant form lands with the real `platform_operator` credential in 02.x (0008's header still describes the dedicated-grant shape — read it as the target, not the shipped state) | 02.x (operator auth) |
| `SchemaConventionTests` exemptions: `rls_probe` (pre-convention template, checksum-locked) and `schema_migration` (02 §4) fully exempt; `task` exempt TenantKey/CompositeForeignKey/RowLevelSecurity only (AuditColumns/VersionTrigger stay gated); `outbox` exempt RLS only (`org_id` unique key enforced) | Lint enforcement belongs to 02.6.1-T2, which must adopt the same exemption list | 02.6.1-T2 |
| Owned EF entity types (OwnsOne/OwnsMany) fail the model build under `TenantModelConventions` (shadow-FK rename breaks table sharing) | Latent: no M1 entity is owned; fix sketch recorded in QA report (skip owned key/FK properties, derive names per-table) | 01.4.2 follow-up / next owner |
| Node `JSON.parse` escaped-key defect (upstream V8 regression, 24.x/26.x warm process) | upstream fix landed in V8, awaiting a Node release; the canonicalize guard stays as a partial mitigation | 01.3.2 / Node upgrade |
| `idempotency.purge` daily cap: `PurgeMaxBatchesPerFire = 50` × 1000 = 50 000 rows/day ceiling; a deployment above that accumulates `idempotency_record` forever | Needs a drain-until-time-budget or restartable cursor (perf review, 01.5.1 §7) | 01.5.1 follow-up |
