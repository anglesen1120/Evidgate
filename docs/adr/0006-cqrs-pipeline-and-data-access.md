# ADR-0006 — CQRS pipeline and data access

## Status

accepted (2026-09-25)

## Date

2026-09-25

## Context

CR-D-1 (DECISIONS §10, 2026-09-25) moved the server to .NET 10 + CQRS; 00 §5.5 C1–C9 define the rules this decision codifies and 01 FR-01-8a/§5.3 require one pipeline with a fixed behaviour order and one durable-task primitive. Tenant isolation (00 §5.4) must sit *inside* the transaction that authorizes, so the pipeline order is a correctness requirement, not style. Choices needing an ADR: dispatch library, write/read data access split, and how durable background work is stored and scheduled.

## Decision

- **Dispatch: `Mediator` 3.0.2** (martinothamar/Mediator; `Mediator.Abstractions` + `Mediator.SourceGenerator`, source-generated registration — no reflection on the hot path). Pipeline order is fixed (00 §5.5 C2): telemetry → validation (FluentValidation) → tenant transaction → authorization → idempotency → handler → outbox flush → commit; asserted by `Architecture.Tests` (T47, T49).
- **Write model: EF Core 10** + `Npgsql.EntityFrameworkCore.PostgreSQL` 10 on the tenant-scoped connection/transaction (C3/C4); EF migrations are never used (ADR-0003).
- **Read model + hot paths: Dapper 2.1 over Npgsql 10** (C5) — module-owned SQL views/read tables, keyset pagination, one-round-trip `unnest` batch inserts, `FOR UPDATE SKIP LOCKED` claims.
- **Durable work: transactional outbox + own `task` queue table** (00 §5.3): outbox row committed with the business change → dispatcher moves it into `task` (`FOR UPDATE SKIP LOCKED`, `available_at` backoff, retry cap, `dead_letter`); cron via **Cronos** with unique `(schedule_name, fire_at)` making fires idempotent — the constraint, not a leader election, guarantees one task per fire. Runner leases reuse the same claim primitive on their own `job`/`job_lease` tables (plan 05).
- Queries are read-only by construction: `IQuery` handlers run `BEGIN READ ONLY` (C1, TL T95); a query never enqueues outbox rows — any lazy state change is computed in the SELECT, never written (see 00 §8.x lazy-expiry precedent).

## Alternatives rejected

- **MediatR 14.2.0** — RPL-1.5 or paid commercial licence ✅ (LICENSE.md in nupkg): fails the DECISIONS §4 licence rule (ADR-0002, Alternatives rejected).
- **WolverineFx 6.40.0** — MIT ✅ but rejected on scope: runtime code generation plus its own envelope/outbox tables duplicate machinery the own-queue design already provides (DECISIONS §4.1, ADR-0002).
- **Hangfire 1.8.25** — LGPL-3.0 ✅, needs an ADR exception per ADR-0005 and adds an extra storage schema; the `task` table + `SKIP LOCKED` covers the same jobs without either cost.
- **Quartz.NET 4.1.1** — Apache-2.0 ✅ but a second scheduler model where Cronos + unique `(schedule_name, fire_at)` suffices (ADR-0002).
- **pg-boss (Node library)** — superseded by CR-D-1: the durable queue must live inside the .NET worker and share the tenant transaction; R9 in DECISIONS §9 records the replacement.
- **EF-only data access** — hot-path SQL through `FromSql` loses clarity and the single-round-trip/`SKIP LOCKED` primitives; **Dapper-only** — hand-written change tracking and optimistic concurrency for aggregates (01 §3).

## Consequences

- One pipeline order is enforceable in review: a behaviour out of order fails `Architecture.Tests` (01.2.1/01.4.2).
- The `task`/`outbox` tables are platform tables owned by 01; every module's deferrable work uses them — no module may add its own queue table.
- Retry semantics are uniform: at-least-once + idempotent handlers by natural key (A10, 00 §5.3); `dead_letter` is the operator-visible terminal state (10.3.1-T5).
- EF Core's migrations feature is deliberately unused — schema changes are exclusively ADR-0003 plain SQL.

## Evidence

- ✅ `Mediator.Abstractions`/`Mediator.SourceGenerator` 3.0.2 MIT — https://api.nuget.org/v3-flatcontainer/mediator.abstractions/3.0.2/mediator.abstractions.nuspec (ADR-0002 row; LICENSE in nupkg, fetched 2026-09-25).
- ✅ `Microsoft.EntityFrameworkCore` 10.0.12 MIT; `Npgsql` + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 PostgreSQL licence; `Dapper` 2.1.89 Apache-2.0; `Cronos` 0.13.0 MIT — ADR-0002 rows, each with its flat-container nuspec URL (fetched 2026-09-25).
- ✅ Rejected licences: MediatR 14.2.0 RPL-1.5, Hangfire 1.8.25 LGPL-3.0 — ADR-0002 "Alternatives rejected" (LICENSE/OSMFEULA evidence in nupkg, fetched 2026-09-25).
- ✅ `FOR UPDATE SKIP LOCKED` queue semantics — https://www.postgresql.org/docs/current/sql-select.html (DECISIONS F6).
- ✅ DECISIONS.md §4.1 (dispatch/write/read/queue rows), §9 R9; plans/00-overview.md §5.3, §5.5 C1–C9; plans/01-foundation.md §3, §4 (`outbox`, `task` tables), TL T49 — repo, read 2026-09-25.

## Revisit trigger

- The `task` table outgrows its scope (needs priorities/fan-out not in 00 §5.3) or a Mediator licence/version change; a licence re-check happens automatically at every bump via ADR-0002's re-verification rule.
