# migrations/

Plain SQL, forward-only, expand/contract migrations applied by
`src/Evidgate.Migrator` as the `evidgate_owner` role via DbUp 7 (00 §5.2,
DECISIONS §4.1, [ADR-0003](../docs/adr/0003-migrator-and-db-role-split.md)).
The playbook for changing a table without breaking the running version is
[`docs/migrations-expand-contract.md`](../docs/migrations-expand-contract.md).

## File contract

- Name: `NNNN_<module>_<change>.sql` — exactly four digits, then a lowercase
  snake_case slug (`[a-z0-9]+(_[a-z0-9]+)*`), then `.sql`. A name that does not
  match stops the runner before it touches the database (exit `1`).
- The rule is case-sensitive and enforced on every platform: the runner looks at
  every file whose extension is `.sql` **case-insensitively**, then validates the
  whole name, so `0002_b.SQL` is rejected (exit `1`) instead of silently skipped
  on a case-sensitive filesystem. Files with another extension (this README, notes)
  are ignored.
- Numbering is a **contiguous run from `0001`**: no gaps, no duplicates, and the
  lexical order is the apply order. A module claims the next free block (today
  `0001`–`0003` are the foundation files from 01, `0004`–`0005` are 02.1.1's
  `organization`/`project` tables, and `0006`–`0008` are 01.5.1's mechanism
  tables `outbox`, `idempotency_record` and `task`, `0009` is 01.5.1-T3's
  dispatcher grant on `outbox`, `0010` is 02.2.1-T1's permission
  catalogue — 01.5.1-T4 shipped its operator read path without a migration, so
  its `0010` reservation lapsed — and `0011` is 02.1.2-T1's `membership` /
  `membership_role` tables, `0012` is 02.1.1-T2's slug grammar (DECISIONS §8.1
  D-A1), `0013` is 02.1.1-T3's tenant limit columns and `0014` is 02.6.1-T3's first
  `evidgate_resolve` resolvers); a gap means two branches
  invented the same sequence and neither history can be trusted.
- One file runs in one transaction (`WithTransactionPerScript`), so PostgreSQL
  DDL is atomic with the journal row: a failed file leaves nothing behind and
  writes no journal row.
- An applied file is immutable. Editing it makes the runner refuse to start with
  a checksum mismatch (exit `2`) before it applies anything; `--status` names the
  drifted file. A fix is a new file, never an edit.
- Statements that cannot run inside a transaction (`create index concurrently`,
  `vacuum`) do not belong here — see the playbook's "Known limits".
- A statement has **no command timeout by default** (`0`): a long index build or
  backfill must not be cut in half by a driver default. `EVIDGATE_MIGRATOR__COMMANDTIMEOUTSECONDS=<n>`
  bounds every statement the runner issues, and a statement that outruns it is
  reported as a timeout, rolled back and left unjournalled (exit `3`).
- Only **one apply runs at a time per database**: the apply path takes a
  session-level advisory lock (`pg_advisory_lock`, key `0x6576696467617465`), so a
  second runner waits — and says so — instead of applying the same file twice.
  `--status` and `--dry-run` never take the lock: a read-only check must not queue
  behind a migration.

## Journal

The runner records every applied file in `public.schema_migration`
(`name text primary key`, `checksum text not null` = lowercase hex SHA-256 of the
file bytes, `applied_at timestamptz not null default now()`, `duration_ms int`).
`name` is the file name **including `.sql`**; the head is the lexically greatest
`name` and is what `Evidgate.Api`/`Evidgate.Worker` compare against at boot
(01.1.2-T1.3). The Migrator is the only writer: `evidgate_app` and
`evidgate_operator` hold `SELECT` on it and nothing else.

Files created by `evidgate_owner` in schema `public` inherit the default
privileges granted by `0001` (DML for `evidgate_app`, `SELECT` for
`evidgate_operator`); a module creating its own schema repeats those two
`alter default privileges` statements for it.

## Roles and credentials

`0001_foundation_roles.sql` creates the four roles **without LOGIN** and without
passwords: a credential is environment state, and a password in a committed file
would stay in git history forever. The split is:

| Role                    | Created by                                               | LOGIN + password granted by                                            | Needs                                                                                                                       |
| ----------------------- | -------------------------------------------------------- | ---------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| `evidgate_owner`        | the superuser bootstrap, before the first migration runs | the superuser bootstrap (`scripts/dev/db-init.sh`, deploy gate 10.2.2) | LOGIN (it connects to migrate), CREATEROLE (it creates the other three roles in `0001`), never SUPERUSER/BYPASSRLS/CREATEDB |
| `evidgate_app`          | `0001`                                                   | the superuser bootstrap                                                | DML on business tables, `EXECUTE` on the `evidgate` helpers, not the owner, no `BYPASSRLS`                                  |
| `evidgate_operator`     | `0001`                                                   | the superuser bootstrap                                                | `SELECT` only (audited read path, TL T76)                                                                                   |
| `evidgate_worker_sched` | `0001`                                                   | the superuser bootstrap                                                | `EXECUTE` per function on owner `SECURITY DEFINER` sweeps (TL T80); no table privileges                                     |

Two consequences worth knowing before writing a new migration:

- A non-superuser role cannot grant or revoke `SUPERUSER`/`BYPASSRLS`, so the
  migration role **re-asserts nothing** on an existing role; the bootstrap owns
  those attributes. `0001` does fail loudly when one of the four roles carries an
  attribute that would break isolation.
- `ALTER DEFAULT PRIVILEGES` entries are **additive** to the built-in defaults, so
  a per-schema `revoke execute on functions from public` does nothing. `0001`
  revokes it with the schema-less form, which is why a new function created by
  `evidgate_owner` starts owner-only and must be granted explicitly.

The `evidgate.current_org_id()` / `current_project_id()` helpers (0002) return
NULL when the tenant setting is unset or empty — that case yields zero rows, no
error and no unfiltered rows (00 §5.4.7). A _malformed_ value is a different
case: the `::uuid` cast raises 22P02 inside the policy, so the statement fails
closed with an error instead of comparing to NULL. Accepted because the setting
is written by the tenant-scope primitive, never from request input.

## Notes on the current head (01.5.1 mechanism tables)

- `0006` says "`created_at` is the only time column the mechanism needs" — the
  mechanism also uses `dispatched_at` (the `dispatched_at is null` predicate and
  the dispatch-lag metric). And `0007` calls the `version` column the thing that
  "keeps that write honest": `CompleteSql` has no `version` predicate, so a
  second `CompleteAsync` for the same record silently overwrites (version still
  advances 1→2→3). Both files are checksum-locked once applied; the corrections
  live here, not in edits (QA wave 4).
- `public.rls_probe` (`0003`) predates the audit-column convention: it has
  `created_at` but no `updated_at`/`version` and no
  `set_updated_at_and_version` trigger, and the schema-convention scan exempts
  it. Do not read `0003` as a template for new tenant tables.
- `0008` revoked `evidgate_operator` SELECT on `task` (same posture as
  `outbox`): without an RLS policy a standing SELECT grant reads every org's
  payloads. 01.5.1-T4.2's operator dead-letter list therefore needs an owner
  `SECURITY DEFINER` read function (with the T76 audit trail) or a deliberate
  grant — an open decision for that task, not something to grant early.
- `0009` (01.5.1-T3.1) pays the grant `0006`'s header promised the dispatcher:
  `select` on `public.outbox` plus `update (dispatched_at)` only — column-scoped
  on purpose, so `evidgate_app` can claim and stamp rows but still cannot
  rewrite `payload`, `task_type`, `org_id` or `idempotency_key` of a committed
  row. With that file applied, `has_table_privilege('evidgate_app',
'public.outbox','UPDATE')` stays false while the dispatcher runs as the app
  role; `select … for update skip locked` needs `select`, not table-level
  `update` (measured on the pinned postgres:18.6).
- T80's "every call audited" for `evidgate.purge_expired_idempotency` is
  deferred to 02.7.1 (`audit_log` does not exist in M1); the scheduled handler
  logs function/limit/row-count at Information instead. Accepted as a carried
  item in STATUS.md.

## Tests and fixtures

Test migration directories live with their test project
(`tests/Migrator.IntegrationTests/Fixtures/`), never here: the repository's
`migrations/` is the production history and must contain only real changes.
`tests/Isolation.Tests` proves the tenancy contract by applying these files
through the real runner against a throwaway PostgreSQL 18 container.
