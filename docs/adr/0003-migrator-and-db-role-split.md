# ADR-0003 — Migrator process and database role split

## Status

accepted (2026-09-25)

## Date

2026-09-25

## Context

DECISIONS.md §4.1 picks plain SQL migrations run by DbUp 7 inside `Evidgate.Migrator`, and F5 (PostgreSQL RLS docs) shows table owners and `BYPASSRLS` roles bypass row-security policies unless `FORCE ROW LEVEL SECURITY` is set. Multi-tenancy therefore requires that the role applying DDL can never be the role serving application traffic. 00 §5.4.3 fixes four roles — `evidgate_owner`, `evidgate_app`, `evidgate_operator`, `evidgate_worker_sched` — and 01 FR-01-5 requires forward-only, expand/contract, plain-SQL migrations with a journal storing name + checksum, applied as the owner role while application traffic runs as a non-owner role without `BYPASSRLS`.

## Decision

- `src/Evidgate.Migrator` is a short-lived console process running `migrations/*.sql` through **DbUp 7** (`dbup-postgresql` 7.0.1) connected as `evidgate_owner`; one transaction per file; a custom `IJournal` writes `schema_migration` rows with `name` + `checksum` (DbUp's default journal stores names only ⚠️ — confirmed in 01.4.1-T1).
- Application traffic connects as `evidgate_app`: not table owner, no `BYPASSRLS`; every business table gets `FORCE ROW LEVEL SECURITY` so even the owner path is policy-bound (00 §5.4.3, F5).
- `evidgate_operator` is a separate audited role for operator/admin paths (TL T76); `evidgate_worker_sched` is the only role allowed to call owner `SECURITY DEFINER` functions for cross-tenant sweeps (TL T80). `evidgate_resolve` holds pre-tenant `SECURITY DEFINER` resolvers on which `evidgate_app` has `EXECUTE` and nothing else outside RLS (00 §5.4.8, TL T1).
- Migrations are forward-only and expand/contract; a change that breaks the previous app version is rejected in review. `--status`/`--dry-run` exist on the runner; a checksum mismatch on an already-applied file makes the runner refuse to start (01 §5).

## Alternatives rejected

- **EF Core migrations** — generates C# migration classes, not the plain SQL files DECISIONS §4.1 requires; EF Core is the write model only and performs no DDL (00 §5.5 C4).
- **Running migrations as `evidgate_app`, or giving the app role `BYPASSRLS`/owner for convenience** — F5 ✅: owner and `BYPASSRLS` bypass policies, which would defeat tenant isolation the moment a policy is wrong; the split makes an RLS mistake fail locally and in the isolation suite (01 §7).
- **Flyway / Liquibase (JVM)** — would add a Java runtime to an otherwise .NET/Node toolchain and duplicate the journal concept we must customise anyway for checksums `[INFERENCE]`; DbUp is a library inside the existing .NET process model.
- **Raw `psql` scripts in CI** — no journal, no checksum, no `--status`/`--dry-run`; FR-01-5 requires all three.

## Consequences

- Deploy order matters: migrator runs before `Evidgate.Api`/`Evidgate.Worker`, which verify the journal head equals the build's expected head and refuse to start on mismatch (01 §4 boot flow).
- Role objects must exist before migrations apply them — the bootstrap migration that creates `evidgate_app`/`evidgate_operator`/`evidgate_worker_sched` runs as `evidgate_owner` (01.4.1).
- Dev roles mirror the production split so an RLS mistake fails locally first (01 §6).
- The checksum journal is custom code (`IJournal`); its correctness is covered by unit tests and the deliberate-failure CI test (edited applied migration fails `Evidgate.Migrator --status`).

## Evidence

- ✅ DbUp: `dbup-postgresql` 7.0.1, MIT licence, nuspec dep groups net8.0/netstandard2.0 — https://api.nuget.org/v3-flatcontainer/dbup-postgresql/7.0.1/dbup-postgresql.nuspec (also ADR-0002 row, fetched 2026-09-25).
- ✅ PostgreSQL RLS bypass semantics (owner/`BYPASSRLS`/`FORCE ROW LEVEL SECURITY`) — https://www.postgresql.org/docs/current/ddl-rowsecurity.html (DECISIONS F5).
- ✅ DECISIONS.md §4.1 migrations row; plans/00-overview.md §5.4.1–§5.4.3, §5.4.8; plans/01-foundation.md FR-01-5, §4 tables (`schema_migration`), §5 failure modes — repo, read 2026-09-25.

## Revisit trigger

- A migration shape DbUp cannot express safely (e.g. needing online `CONCURRENTLY` index builds in the same transaction-per-file model) or a second datastore appears → re-open; licence change on DbUp is handled per ADR-0005.
