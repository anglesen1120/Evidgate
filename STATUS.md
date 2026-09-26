# Evidgate — delivery status

Updated 2026-09-26. Source of truth for tasks: `plans/01`–`12` and `plans/features/`; this file tracks execution only. Task ids and sizes come from `scripts/plan_graph.py`.

## 1. Snapshot

| Item | State |
|---|---|
| Branch flow | `feature/*` or `docs/*` → PR into `develop` → owner merges. `main` = planning baseline |
| `develop` head | `e6e2a38` (PR #5 merged) |
| Merged PRs | #1 plans + stack (CR-D-1) · #2 M0 foundation · #3 M1 wave 1–2 · #4, #5 plan notes (Docker on runner) |
| Active branch | `feature/m1-wave3` (this wave) |
| M0/M1 tasks | 23 done · 111 open (of 134) |
| Plan checks | `plan_graph.py` errors 0 · 502 tasks · 560.25 raw / 672.3 buffered person-days |
| CI | Interim `.github/workflows/docker-tests.yml` (88/88 green on hosted runner, run 36205176754); replaced by `ci.yml` in 01.7.1-T1 |
| Owner decisions | Decided 2026-09-26: O1 engine = Playwright, customer CI = per-project input; O2 QA lead = customer tenant role; O4 = no third-party LLM, own healing system. Still open before M1: pilot names (O1), Evidgate team size/TL (O2). Open before 08.5 DoR: O4 model-or-not — DECISIONS §8 |

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

## 3. Wave 3 (in progress on `feature/m1-wave3`)

Every task passes the gates in order: implement → QA agent (separate from the author) → code review + performance review + security review (parallel) → fixes re-tested → commit.

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

## 4. Ready next (dependencies met, not started)

| Task | Size | Milestone | Note |
|---|---|---|---|
| 01.3.2-T1 | L | M1 | Generated SDK |
| 01.3.2-T2 | L | M1 | Validators |
| 01.6.1-T2 | L | M1 | Integration harness |
| 01.6.1-T5 | S | M1 | Playwright workspace |
| 01.8.1-T3 | M | M1 | Dev stack follow-up |
| 04.1.2-T4 | M | M1 | `producer_slot` table + registry |
| 04.7.1-T1 | M | M1 | |
| 05.1.1-T1 | L | M1 | |
| 10.1.1-T1 | M | M1 | |
| 10.2.1-T1 | L | M1 | Container images + SBOM |
| 10.8.1-T1 | S | M1/M2 | Provider decision memo (O3) |
| 11.1.1-T2, 11.2.1-T1, 11.8.1-T1, 11.8.1-T2 | S, M, L, M | M1 | Security area |
| 12.1.1-T1, 12.1.1-T2 | L, M | M0 | Customer interviews (owner) |
| 12.2.1-T2, 12.6.1-T1 | M, M | M1 | Commercial area |

## 5. Blocked / carried items

| Item | Blocker | Owner |
|---|---|---|
| `seed-dev` script | Needs 02.1.1 / 02.1.2 / 07.1.1 tables | Tech Lead |
| Web app | 09.1.1 (M2) | Lane C |
| `docker-tests.yml` fixes (global-json-file, single trigger, `ubuntu-24.04`) | Folded into 01.7.1-T1; the file is deleted when `ci.yml` lands | Tech Lead |
| `evidgate_owner` permissive policy on `organization`/`project` | Rationale logged in 00 §15 and in the `0003` copy block; 00 §5.4.3 still lacks the sentence | Tech Lead |
| `/internal/*` public-ingress routing rule | `[OPEN]` in 01.1.2 feature plan, no plan-10 task | Tech Lead |
| O1 pilot names, O2 Evidgate team size/TL | Owner decision (engine + QA-lead parts decided 2026-09-26) | Owner |
