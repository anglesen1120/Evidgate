# Repository Guidelines

## Project Overview

Evidgate collects automated test results (web, API, mobile, unit) from customer CI or agents, keeps them as immutable evidence bound to exact builds, and turns that evidence into a release decision (PASS / FAIL / INCONCLUSIVE) through versioned policies. It adds **safe healing**: repairing test mechanics (for example a changed locator) without changing test intent, shadow first, never hiding a product bug.

**Current state (2026-10-26).** Stack decided in DECISIONS CR-D-1 (§4). Task-level execution state lives in `STATUS.md` (§1 snapshot, §2 done, §5 ready next, §6 blocked), not here.

- Landed on `develop`: 163 of 655 planned tasks = 169.25 of 702.75 raw person-days (≈25 % of tasks, ≈24 % of effort); 492 tasks / 533.5 d remain.
- Landed by CR-D-2 phase (tasks / landed person-days, of the phase's planned raw): G1 120 / 132.25 of 281.25 · G2 37 / 31 of 195.5 · G3 0 / 0 of 106.75 · G4 6 / 6 of 119.25.
- Landed by area plan (tasks / planned): 01 45/53 · 02 28/48 · 03 7/40 · 04 21/53 · 05 16/53 · 06 11/36 · 07 6/42 · 08 0/69 · 09 17/72 · 10 3/54 · 11 5/51 · 12 0/30 · 13 4/54.
- Delivered substrate: M1 foundation (toolchain pins, contracts pipeline + generated SDK drift gate, CI running `scripts/verify.sh`, dev stack, licence/SBOM/action-pin gates) and the Stage A waves (FORCE-RLS tenancy, orgs/projects/memberships, permissions, audit chain, CI tokens).
- Delivered modules: catalog case/suite identity and revisions (03.1.1/03.1.3/03.2.1), runs manifest upload (05.1.1), 05.3.2-T1 only, evidence upload + safe parsing (06.1/06.3.1–06.3.2), policy revisions and waiver/quarantine lifecycles (07.1.1/07.3.1), and the Angular 22 web app with scaffold, app shell, shared state components and generated-SDK data layer (09.1.1-T1/T3/T4/T7/T8/T10/T12).
- Migrations `0001`–`0045` are contiguous; highest is `0045_identity_session_hardening.sql`, the next free number is `0046`.
- Not yet started: plan 08 (safe healing/learning) and plan 12 (pilot/commercial) have no landed task at all.
- Not yet inside started areas: 07 evaluator and its decisions (07.2), waiver/quarantine effects + expiry (07.3.2), flaky detection (07.4), late evidence + release decisions (07.5), CI delivery + webhooks (07.6); 06 sensitivity/redaction/secret masking (06.2) and the isolated viewer (06.3.3–06.3.4); most web product screens; real `platform_operator` auth (02.8.1).
- Local gate: `scripts/verify.sh` (`--stage` for partial runs); estimates, buffer and roll-up: `plans/00-overview.md` §14.

## Where things are

| Path | Content |
|---|---|
| `DECISIONS.md` | Name, locked architecture (A1–A10), stack + versions (§4), safe-healing rules (§5), verified facts (§6), open owner decisions (§8), review deltas vs the source (§9) |
| `RECOMMENDATIONS.md` | Working recommendations for open decisions (O1–O10) and plan §11 questions; not decisions |
| `plans/00-overview.md` | Shared contracts: glossary, milestones, architecture, IDs, state machines, API, NFRs, feature-plan process, estimate roll-up |
| `plans/01`–`13` | High-level area plans (epics → stories → tasks with sizes); 655 tasks, 702.75 raw / 843.3 buffered person-days (00 §14; story 13.2.2 added 8 tasks / 9.5 d on 2026-10-26). `13-product-phases.md` is the CR-D-2 master plan (authoring, script generation, element memory, drift classification); the Milestone column in every plan holds the CR-D-2 phase G1–G4 (13 §4.4) |
| `plans/open-items/` | Register of every `[OPEN]`/⚠️ with a recommended default, the information needed and the task it must precede |
| `plans/features/` | TL review + binding rulings (`README.md` §3, T1–T101), findings register (`REVIEW-2026-09-25.md`), one detailed feature plan per story in `<NN>-<area>/` (00 §12) |
| `docs/source/` | Original Vietnamese system design (working name "ReleaseHub") and its .docx |

## Authority and editing rules

- Precedence: `DECISIONS.md` (why, facts) and `plans/00-overview.md` (contracts) win over `plans/01`–`13`; a feature plan refines, never silently contradicts, its area plan.
- A contract change goes to 00 first and is logged in 00 §15; then affected plans are updated.
- Plan files are **English**. The owner often writes Vietnamese in chat.
- Legend in every file: ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Never state an external fact (version, price, law, API behaviour) without a source or a ⚠️.
- Estimates: XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 person-days; epic totals equal the sum of task rows; each plan adds +20 % buffer; the roll-up is 00 §14. Re-check sums after every edit.
- IDs: stories `NN.E.S`, tasks `NN.E.S-Tn` where `NN.E.S` is the story the row sits under; reference stories/tasks, not page numbers. Every referenced id must exist.
- Owner decisions (DECISIONS §8): O1 decided 2026-10-02 (interviews waived; pilot profile `docs/decisions/O1-pilot-profile.md`), O2 decided (the owner alone; delivery order `plans/features/SOLO-PATH.md`, reviews from the spec package), O4 decided (Evidgate-provided AI credit via an Evidgate gateway, 08.5.5), O10 decided (a), O8 decided (milestones are sequence + exit gates). O3 (cloud provider) deferred to after the Stage B decision point and before any real customer data. Playwright is the UI engine; customer CI is per-project input. Every remaining `[OPEN]`/⚠️ has a recommendation in `plans/open-items/`.

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
