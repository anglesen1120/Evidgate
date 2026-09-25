# Repository Guidelines

## Project Overview

Evidgate collects automated test results (web, API, mobile, unit) from customer CI or agents, keeps them as immutable evidence bound to exact builds, and turns that evidence into a release decision (PASS / FAIL / INCONCLUSIVE) through versioned policies. It adds **safe healing**: repairing test mechanics (for example a changed locator) without changing test intent, shadow first, never hiding a product bug.

**Current state: planning only** (stack changed 2026-09-25 to .NET 10 + CQRS + Angular 22, DECISIONS CR-D-1). No source code, build or tests yet. `apps/`, `packages/`, `src/`, `contracts/`, `migrations/` and `fixtures/` are the target layout (`plans/00-overview.md` §5.2); they do not exist yet.

## Where things are

| Path | Content |
|---|---|
| `DECISIONS.md` | Name, locked architecture (A1–A10), stack + versions (§4), safe-healing rules (§5), verified facts (§6), open owner decisions (§8), review deltas vs the source (§9) |
| `RECOMMENDATIONS.md` | Working recommendations for open decisions (O1–O10) and plan §11 questions; not decisions |
| `plans/00-overview.md` | Shared contracts: glossary, milestones, architecture, IDs, state machines, API, NFRs, feature-plan process, estimate roll-up |
| `plans/01`–`12` | High-level area plans (epics → stories → tasks with sizes); 502 tasks, 560.25 raw / 672.3 buffered person-days (00 §14, after CR-D-1) |
| `plans/features/` | TL review + binding rulings (`README.md` §3, T1–T96), findings register (`REVIEW-2026-09-25.md`), one detailed feature plan per story in `<NN>-<area>/` (00 §12) |
| `docs/source/` | Original Vietnamese system design (working name "ReleaseHub") and its .docx |

## Authority and editing rules

- Precedence: `DECISIONS.md` (why, facts) and `plans/00-overview.md` (contracts) win over `plans/01`–`12`; a feature plan refines, never silently contradicts, its area plan.
- A contract change goes to 00 first and is logged in 00 §15; then affected plans are updated.
- Plan files are **English**. The owner often writes Vietnamese in chat.
- Legend in every file: ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Never state an external fact (version, price, law, API behaviour) without a source or a ⚠️.
- Estimates: XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 person-days; epic totals equal the sum of task rows; each plan adds +20 % buffer; the roll-up is 00 §14. Re-check sums after every edit.
- IDs: stories `NN.E.S`, tasks `NN.E.S-Tn` where `NN.E.S` is the story the row sits under; reference stories/tasks, not page numbers. Every referenced id must exist.
- Open before M1: owner decisions O1 and O2 (DECISIONS §8). O8 is decided: milestones are sequence + exit gates, not dates.

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
