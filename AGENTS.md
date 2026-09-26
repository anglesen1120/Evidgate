# Repository Guidelines

## Project Overview

Evidgate collects automated test results (web, API, mobile, unit) from customer CI or agents, keeps them as immutable evidence bound to exact builds, and turns that evidence into a release decision (PASS / FAIL / INCONCLUSIVE) through versioned policies. It adds **safe healing**: repairing test mechanics (for example a changed locator) without changing test intent, shadow first, never hiding a product bug.

**Current state: M1 foundation in progress** (stack per DECISIONS CR-D-1). Merged (PR #2): 01.1.1 T1–T3 (toolchain pins, workspace skeleton), 01.2.1 (analyzers, format, strict TS, `tests/Architecture.Tests`), 01.3.1 (`contracts/openapi.yaml` stubs, `contracts/schemas/`, `contracts/adapter/`), 01.9.1 (ADRs in `docs/adr/`). Merged (PR #3): 01.4.1 (`src/Evidgate.Migrator`, `migrations/0001`–`0003` roles + RLS baseline, `scripts/dev/db-init.sh`), 01.1.2 (Api/Worker hosts, 11 module pairs, in-memory skeleton slice), 01.8.1 (SeaweedFS parity tests, compose dev stack, `scripts/dev/up.sh`). Merged (PR #6): 01.4.2-T1 (`src/BuildingBlocks/Persistence/Tenancy/` — `ITenantScope`, `ITenantDb`, `ITenantDbContextFactory`, `TenantCommandInterceptor`), 02.1.1-T1 (`migrations/0004`/`0005` organization + project, FORCE RLS, `set_updated_at_and_version` trigger; operator policies deferred to 02.8.1), 01.3.2-T4 (`JsonCanonicalizer`/`CanonicalHash` RFC 8785 + `packages/cli` wrapper + `fixtures/canonical`/`rfc8785`), 01.6.1-T1 (`tests/Directory.Build.props` + `*.UnitTests` no-network guard, `vitest.config.base.mts` + workspace configs, `tsc -p` + `vitest` stages in `verify.sh`). Delivered on `feature/m1-wave4` (PR pending): 01.4.2-T2/T3 (schema/EF conventions, `SchemaConventionTests`, explicit-name rejection, version-trigger check, UUIDv7 `id` value generator), 01.5.1-T1/T2/T6 (`migrations/0006`–`0008` `outbox`/`idempotency_record`/`task`, claim/lease/consumer, Cronos schedule runner, purge functions/schedules), 01.6.1-T2 (`tests/Shared/` + `tests/Foundation.IntegrationTests` Testcontainers harness), 01.6.1-T5 (`apps/web/e2e` Playwright workspace). `STATUS.md` tracks task state; operator policies live in 02.8.1 (T76); Api/Worker still don't call `AddEvidgateTenancy` (ready next: 01.4.2-T4, needs 01.5.1-T2/T6 which just landed). Not yet: `seed-dev` (needs 02.1.2/07.1.1 tables), web app (09.1.1), most business tables, the outbox dispatcher (01.5.1-T3) and retry/dead-letter/operator routes (01.5.1-T4). Local gate: `scripts/verify.sh`.

## Where things are

| Path | Content |
|---|---|
| `DECISIONS.md` | Name, locked architecture (A1–A10), stack + versions (§4), safe-healing rules (§5), verified facts (§6), open owner decisions (§8), review deltas vs the source (§9) |
| `RECOMMENDATIONS.md` | Working recommendations for open decisions (O1–O10) and plan §11 questions; not decisions |
| `plans/00-overview.md` | Shared contracts: glossary, milestones, architecture, IDs, state machines, API, NFRs, feature-plan process, estimate roll-up |
| `plans/01`–`12` | High-level area plans (epics → stories → tasks with sizes); 502 tasks, 560.25 raw / 672.3 buffered person-days (00 §14, after CR-D-1) |
| `plans/features/` | TL review + binding rulings (`README.md` §3, T1–T101), findings register (`REVIEW-2026-09-25.md`), one detailed feature plan per story in `<NN>-<area>/` (00 §12) |
| `docs/source/` | Original Vietnamese system design (working name "ReleaseHub") and its .docx |

## Authority and editing rules

- Precedence: `DECISIONS.md` (why, facts) and `plans/00-overview.md` (contracts) win over `plans/01`–`12`; a feature plan refines, never silently contradicts, its area plan.
- A contract change goes to 00 first and is logged in 00 §15; then affected plans are updated.
- Plan files are **English**. The owner often writes Vietnamese in chat.
- Legend in every file: ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Never state an external fact (version, price, law, API behaviour) without a source or a ⚠️.
- Estimates: XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 person-days; epic totals equal the sum of task rows; each plan adds +20 % buffer; the roll-up is 00 §14. Re-check sums after every edit.
- IDs: stories `NN.E.S`, tasks `NN.E.S-Tn` where `NN.E.S` is the story the row sits under; reference stories/tasks, not page numbers. Every referenced id must exist.
- Open before M1: O1 pilot names and O2 (DECISIONS §8). Decided 2026-09-26: Playwright is the UI engine; customer CI is per-project input. O2/O4 owner notes are recorded as unconfirmed `[INFERENCE]` readings. O8 is decided: milestones are sequence + exit gates, not dates.

## Planned stack (DECISIONS §4)

Server/worker: .NET 10 LTS · ASP.NET Core minimal APIs · CQRS (`Mediator` 3, EF Core 10 write model, Dapper read model) · PostgreSQL 18 (RLS, FORCE) · DbUp + plain SQL migrations · own transactional outbox + task queue (`SKIP LOCKED`), own lease tables for runner jobs · OpenTelemetry · xUnit v3, Shouldly, Testcontainers. Web: Angular 22 (standalone, zoneless, signals) + `@ngrx/signals` · Vitest, Playwright. Customer-side CLI/adapters/agent: Node.js 26 LTS · TypeScript 6.0 · npm workspaces. S3 API. Contract-first: OpenAPI 3.1 + JSON Schema in `contracts/`. Code rules: 00 §5.5 (CQRS), §5.6 (Angular), §5.7 (performance/security).

## Invariants code must keep

- Every business row has `org_id`; composite tenant FKs; one explicit transaction per request with `SET LOCAL evidgate.org_id`; app role never owner/BYPASSRLS.
- Completeness, outcome and decision are separate fields; missing evidence never yields PASS.
- Manifests, revisions, evaluations are immutable; late evidence creates a new revision.
- Events are acknowledged only after commit; dedupe by `event_id` + payload hash; order by producer sequence.
- Healing never removes/weakens assertions, never skips, never overwrites the original failure; runtime auto-apply is off in V1–V2; kill switches exist before any apply feature.

## Definition of Done

See `plans/00-overview.md` §12: API/schema version, migration, authorisation, telemetry, failure path, compatibility fixture, operator notes, CI green, integration tests on real PostgreSQL, OpenAPI updated, vi + en strings.
