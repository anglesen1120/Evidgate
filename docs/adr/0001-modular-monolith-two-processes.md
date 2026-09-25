# ADR-0001 — One modular monolith, two processes

## Status

accepted (2026-09-25)

## Date

2026-09-25

## Context

DECISIONS.md §3 A1 locks the shape: one modular-monolith backend, and API and worker as separate processes built from one codebase. Plan `plans/01-foundation.md` §3 (first key decision) turns that lock into the concrete layout: `src/Evidgate.Api` (ASP.NET Core minimal APIs) and `src/Evidgate.Worker` (Generic Host: outbox dispatcher, task-queue consumers, schedules) both reference the same `src/Modules/Evidgate.Modules.<Name>` projects. The platform is multi-tenant with tenant isolation enforced by PostgreSQL RLS (00 §5.4), so module boundaries must be enforceable mechanically — not by convention alone — or isolation and the CQRS rules in 00 §5.5 erode silently.

## Decision

- One .NET solution; business code lives in `src/Modules/Evidgate.Modules.<Name>` plus a public `Evidgate.Modules.<Name>.Contracts` project per module (00 §5.2, §5.5).
- A module project may reference only other modules' `.Contracts` projects; `Architecture.Tests` fails the build on any other module-to-module reference, on `Domain` referencing `Infrastructure`, and on `public` types outside `.Contracts`/`Endpoints` (ruling T47).
- Two long-running processes from the same codebase: `Evidgate.Api` for HTTP, `Evidgate.Worker` for background work (outbox dispatch, `task` queue consumers, cron fires). `Evidgate.Migrator` is a third, short-lived console process (ADR-0003).
- No microservices in V1–V2 (A1). Cross-module communication is a call on the `.Contracts` interface inside one transaction; work that must outlive the request goes through the outbox (00 §5.5 C7, A10).

## Alternatives rejected

- **Split services (microservices or per-module services)** — premature distribution: RLS tenant context, one-transaction-per-request (00 §5.4.2) and the outbox-in-the-same-transaction rule (C7) are cheap in-process and hard across a network; A1 forbids them in V1–V2.
- **One process (API and worker in a single host)** — a slow webhook or evaluation task would block ingest and HTTP health; separate processes give independent restart, scaling and failure domains for work the plans already split into `Evidgate.Api` / `Evidgate.Worker` (01 §3).
- **ESLint-style or code-review-only boundary enforcement** — module rules are enforced by project references plus `Architecture.Tests` for server code (T47); lint and review alone cannot fail a build on a forbidden reference.
- **Shared-kernel-free modules (fully duplicated primitives)** — `src/BuildingBlocks/` holds the shared primitives (SharedKernel ids/errors, Persistence tenant scope, outbox, task queue, Canonicalization, Observability) so each module does not re-implement them (00 §5.2).

## Consequences

- A module extraction to a service is deferred to a post-V2 decision; the `.Contracts`-only rule keeps that path open without paying for distribution today.
- `Architecture.Tests` becomes a required gate (01.2.1-T1, 01.9.1 wiring via `scripts/verify.sh`); every handler also carries a permission attribute (00 §5.7 S2, T49).
- The worker must implement everything the API defers: dispatcher, consumers, schedule fires (01.5.1); a crash between commit and dispatch is covered by at-least-once re-reads (A10, 01 §5).
- Unblocks all module stories (02–08); blocks nothing outside them.

## Evidence

- ✅ DECISIONS.md §3 A1 (locked architecture table) — repo, read 2026-09-25.
- ✅ plans/00-overview.md §5.1–§5.2 (components, layout), §5.5 (module shape, C1–C9) — repo, read 2026-09-25.
- ✅ plans/01-foundation.md §3 first decision row; rulings T47/T49 in `plans/features/README.md` §3 — repo, read 2026-09-25.
- No external dependencies introduced by this decision — nothing to licence-check.

## Revisit trigger

- A module needs independent scaling or a hard resource boundary that in-process isolation cannot give, OR a second consumer of `.Contracts` appears that must live outside the process (e.g. a satellite service) → re-open; until V3 the default stays the monolith.
