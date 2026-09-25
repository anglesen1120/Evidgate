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
  `0001`–`0003` are the foundation files, so the next story starts at `0004`); a
  gap means two branches invented the same sequence and neither history can be
  trusted.
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

## Tests and fixtures

Test migration directories live with their test project
(`tests/Migrator.IntegrationTests/Fixtures/`), never here: the repository's
`migrations/` is the production history and must contain only real changes.
`tests/Isolation.Tests` proves the tenancy contract by applying these files
through the real runner against a throwaway PostgreSQL 18 container.
