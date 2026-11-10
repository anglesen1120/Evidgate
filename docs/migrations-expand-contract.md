# Migration playbook: expand / contract

How schema changes reach an environment without breaking the version that is
already running. The runner itself is described in
[`migrations/README.md`](../migrations/README.md); the role split it depends on is
[ADR-0003](adr/0003-migrator-and-db-role-split.md).

## The rule

Migrations are **forward-only** and **expand/contract**. A schema change never
assumes that the application code deployed at the same moment is the only code
running: `Evidgate.Api` and `Evidgate.Worker` run as separate processes, a
deploy of one is not a deploy of the other, and a rollback keeps the new schema
with the old code.

Consequences:

- A migration that removes something the *previous* code reads is a breaking
  change. It is rejected in review, not discovered in production.
- An already-applied file is immutable. Editing it makes `--status` report drift
  and makes the runner refuse to start (exit `2`) — history is evidence, and the
  checksum in `public.schema_migration` is what proves it.
- The runner has **no destructive mode**: there is no flag that drops, truncates
  or skips anything.

## The sequence

The example is a column rename on `public.rls_probe`
(`label` → `title`); `tests/Migrator.IntegrationTests/Fixtures/playbook_expand.sql`
and `playbook_contract.sql` are exactly this pair and
`MigrationPlaybookTests.Rename_Pair_Zero_Downtime` runs it through the real
Migrator.

| Step | Artifact | What changes | What must keep working |
|---|---|---|---|
| 1. Expand | migration `NNNN_*_expand.sql` | Add the new column **nullable**; no production code reads it yet | The running (old) code: it still writes and reads the old column |
| 2. Dual-write | application code, deployed after step 1 | New code writes **both** columns and reads the new one, falling back to the old | Old code keeps writing only the old column; both are readable |
| 3. Backfill | migration `NNNN_*_backfill.sql` (or the contract file, see below) | `update … set new = old where new is null` for rows the old code wrote | Reads of the new column must tolerate NULL until the backfill commits |
| 4. Contract | migration `NNNN+1_*_contract.sql`, a **later release** | Backfill once more, `alter column … set not null`, then `drop column old` | Nothing: this is the cutover. Only step 2 code may run against it |

Step 3 may live in the expand file when it is cheap, or in its own file when it
is batched. Whatever the split, the contract file repeats the backfill before it
enforces `not null`, because rows written by the old code after the expand still
have a NULL there — that is why the fixture pair does it this way.

Rules that make the sequence real:

- **Additive first.** New columns, new tables and new indexes are added before
  code uses them; they are dropped only after the last code that reads them is
  gone from every environment.
- **`not null` last.** A new column is nullable while both code versions exist;
  it becomes `not null` in the contract step, after the backfill.
- **No renames in one step.** `alter table … rename column` breaks the old code
  path instantly (column not found) and is rejected for the same reason as a
  drop.
- **Backfills run as `evidgate_owner`** and therefore need the migration role's
  policy on the table. With `force row level security` and no policy for that
  role, a backfill **silently updates zero rows** and does not fail — no role
  bypasses RLS, so the permission is explicit per table (see the copyable header
  in `migrations/0003_foundation_rls_probe.sql`).
- **Verification is part of the change.** The expand file's assertion is "the old
  path still works after it is applied", the contract file's is "the new path is
  the only one left". A migration whose effect cannot be observed in a test is a
  migration nobody can review.

## Rejected example (do not do this)

One file, one release:

```sql
-- 0042_catalog_rename_locator.sql  — REJECTED
alter table public.test_step rename column locator to selector;
```

Why it is rejected:

- The previous `Evidgate.Api` version (still serving traffic during a rolling
  deploy, and still the running version if the deploy is rolled back) issues
  `select locator …`. After this file it fails with `42703: column "locator" does
  not exist` — a production outage caused by a schema change that is not
  reversible by redeploying the old build.
- The same file cannot be fixed by "just deploying the new code first", because
  the new code cannot be written until the column exists: the expand step is
  missing.
- It edits history if it replaces an already-applied file — the runner refuses to
  start (exit `2`) and `--status` names it.

The sanctioned version is three changes across two releases: add `selector`
nullable (expand) → deploy code that writes both and reads `selector` with a
fallback to `locator` (dual-write) → backfill, `set not null`, drop `locator`
(contract). Slower in calendar time, never in outage time.

## Operating the runner

| Exit code | Meaning | What to do |
|---|---|---|
| `0` | At head, or the read-only command succeeded | Continue the deploy |
| `1` | Invalid invocation, unreadable directory, or an invalid migration set (bad file name, duplicate number, gap in the numbering) | Fix the set; nothing touched the database |
| `2` | Checksum drift: an applied file was edited or deleted, or `--status` found one | Never edit history — restore the file and add a new migration |
| `3` | A migration failed; its transaction rolled back and nothing was journalled | Fix the file (or add a new one), re-run; the journal is still at the previous head |
| `4` | The database could not be reached or the journal could not be prepared | Check connectivity and the owner credential, then re-run |
| `5` | A script could not take a lock within `lock_timeout` (SQLSTATE `55P03`); its transaction rolled back and nothing was journalled | Find the blocker, end it or wait for it, then re-run — see the [Migrator timeouts runbook](runbooks/migrator-timeouts.md) |
| `6` | A statement exceeded `statement_timeout` (SQLSTATE `57014`); its transaction rolled back and nothing was journalled | Raise the limit for that deploy or split the statement — see the [Migrator timeouts runbook](runbooks/migrator-timeouts.md) |

Order in a deploy: the Migrator runs **before** `Evidgate.Api`/`Evidgate.Worker`,
which compare the journal head with the head they were built against and refuse
to start on a mismatch (01 §4).

Two properties an operator should know before the first long migration:

- **A statement has no driver command timeout by default, but it does have a
  server-side `statement_timeout`.** Every script runs in its own transaction
  under `set local lock_timeout` (default 5 s) and `set local statement_timeout`
  (default 900 s), set from `EVIDGATE_MIGRATOR__LOCKTIMEOUTSECONDS` and
  `EVIDGATE_MIGRATOR__STATEMENTTIMEOUTSECONDS` (`0` disables either). A backfill
  or index build is not cut off by the client — `EVIDGATE_MIGRATOR__COMMANDTIMEOUTSECONDS`
  stays `0` unless set — but a statement that outruns 900 s fails as exit `6`.
  A script that legitimately needs longer takes the variable for that deploy job.
  A lock wait is bounded separately and fails as exit `5`; both roll the file
  back and journal nothing, and the [runbook](runbooks/migrator-timeouts.md)
  covers the rerun.
- **Applies serialise per database.** The apply path holds a session advisory lock,
  so a second runner waits (printing that it is waiting) and then re-reads the
  journal, which is why a rerun never applies the same file twice. `--status` and
  `--dry-run` do not take the lock and never write — a read-only probe is safe to
  run while a migration is in flight, and it is the right thing for a deploy gate
  that must fail fast (dispatchers, readiness checks).

## Known limits

- **One transaction per file**, which is what makes a failed file atomic. A
  statement that cannot run inside a transaction — `create index concurrently`,
  `vacuum`, `alter type … add value` on older servers — is therefore unavailable
  in a migration; build those indexes with the blocking form, or add a
  maintenance step outside the runner. Supporting it means extending the runner,
  which is the revisit trigger recorded in ADR-0003.
- **Forward-only.** There is no down-migration; a mistake is corrected by a new
  migration that moves forward again.
- **The migration role is not a superuser.** It cannot change `SUPERUSER`,
  `BYPASSRLS` or `CREATEDB` on any role; those attributes are set where the role
  is created, by the superuser bootstrap (see `migrations/README.md`).
