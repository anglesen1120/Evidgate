# Migrator timeouts runbook

Status: **written with the CR-27 fix; no drill on a real deploy yet ⚠️.**

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Line cites are on this worktree's branch (`feature/migrator-timeouts-s366`).

## 1. What the two settings do

Every migration script runs in its own transaction (`WithTransactionPerScript`, `src/Evidgate.Migrator/MigratorRunner.cs:177`). Before the script body, `ScriptTimeoutPreprocessor` prepends one line to the script's first line, so PostgreSQL line numbers stay equal to the file's: `set local lock_timeout = '<n>ms'; set local statement_timeout = '<n>ms'; ` (`src/Evidgate.Migrator/ScriptTimeoutPreprocessor.cs:15-22`). `set local` scopes both to that script's transaction: nothing leaks to the next script, to a later command on the session, or to another session.

- `lock_timeout` bounds how long one statement **waits to acquire** a lock. It never bounds the hold: once the statement has the lock it runs to completion, and every reader or writer queued behind it waits that long. A wait that times out is SQLSTATE `55P03`.
- `statement_timeout` bounds one **statement's** execution (PostgreSQL measures it per statement, not per transaction ⚠️ — read from the PostgreSQL documentation, not re-checked for this page). A statement that outruns it is SQLSTATE `57014`. The timeout that fires first wins, so a DDL statement in a tight lock queue usually hits `lock_timeout`.
- Defaults (`src/Evidgate.Migrator/MigratorOptions.cs:33-49,74-78`): **`lock_timeout` 5 s**, **`statement_timeout` 900 s**.
- Overrides: `EVIDGATE_MIGRATOR__LOCKTIMEOUTSECONDS` and `EVIDGATE_MIGRATOR__STATEMENTTIMEOUTSECONDS` (`MigratorOptions.cs:23-24,187-203`). Values are whole seconds in `[0, 2147483]` (`int.MaxValue / 1000`, the largest millisecond value the server accepts); `0` disables that timeout explicitly. Unset takes the default; unparsable, negative or too large fails start-up: `E invalid EVIDGATE_MIGRATOR__LOCKTIMEOUTSECONDS: '<value>' is not a whole number of seconds between 0 and 2147483 (0 = disabled)`, followed by the usage text and **exit 1** (`MigratorHost.cs:88-97`, `MigratorExitCodes.InvalidInvocation`).
- A third timeout exists and is separate: `EVIDGATE_MIGRATOR__COMMANDTIMEOUTSECONDS` is the **driver** (DbUp/Npgsql) command timeout, default `0` = no driver timeout (`MigratorOptions.cs:26-33`). It fires client-side; it is not `statement_timeout`.
- Both settings are declared once in `infra/config-matrix.json` and per environment in `infra/<env>/environment.json`. All three environments take the defaults today.

## 2. Exit codes and the transcript line

| Exit | Meaning | What to do |
|---|---|---|
| `0` | every pending script applied (or nothing pending) | none |
| `1` | invalid invocation, invalid migration set, or invalid migrator configuration | fix the named variable/argument |
| `2` | checksum drift: an applied file was edited or removed; nothing applied | restore the file, add a new migration |
| `3` | a migration failed for another reason (including the driver command timeout); rolled back, not journalled | read the error, fix, rerun |
| `4` | database unreachable, or the journal could not be read/prepared | check connectivity and the owner credential |
| **`5`** | a script could not take a lock within `lock_timeout` (`55P03`); rolled back, not journalled | §3 |
| **`6`** | a statement exceeded `statement_timeout` (`57014`); rolled back, not journalled | §4 |

Codes are the process contract (`src/Evidgate.Migrator/MigratorExitCodes.cs:8-32`); the deploy gate decides on them.

On `5` or `6` the transcript carries the failing script, the reason, then the rollback line (`MigratorRunner.cs:187-204,249-263`):

```
migration failed in 0033_orchestration_collection.sql: canceling statement due to lock timeout
0033_orchestration_collection.sql hit lock_timeout of 5 s (relation run); another session holds a conflicting lock: find it in pg_stat_activity / pg_locks, end or wait for it, then rerun (EVIDGATE_MIGRATOR__LOCKTIMEOUTSECONDS; exit 5)
the failed script's transaction was rolled back and it was not journalled
```

Exit `6` prints the same shape with `statement_timeout`, the configured seconds, and `... raise the limit or split the migration (EVIDGATE_MIGRATOR__STATEMENTTIMEOUTSECONDS; exit 6)`. The relation or `where` clause is appended when PostgreSQL reports one; either may be absent.

## 3. A lock timeout during a deploy

1. **Do not edit the `.sql`.** An applied file is immutable; editing one turns the next run into checksum drift (exit `2`). A timeout is not a defect in the file.
2. **Find the blocker** with a read-only session (as `evidgate_owner` or a DBA read session — the app role cannot see other sessions' details):

   ```sql
   select pid, usename, state, wait_event_type, wait_event,
          now() - xact_start as xact_age, left(query, 120) as query
   from pg_stat_activity
   where datname = current_database() and pid <> pg_backend_pid()
   order by xact_start nulls last;
   ```

   Then the blocking pair, which is the pid the migration waited on:

   ```sql
   select pid, pg_blocking_pids(pid) as blocker_pids, wait_event_type, wait_event,
          left(query, 120) as query
   from pg_stat_activity
   where cardinality(pg_blocking_pids(pid)) > 0;
   ```

   An `idle in transaction` session holding the lock is the usual cause: it takes no `lock_timeout` of its own and can sit there indefinitely.
3. **Decide, do not improvise.** Wait if the blocker is a short-lived transaction that is still doing work. Terminate only with the owner's approval and only when the blocker is stuck: `select pg_terminate_backend(<pid>);` kills that session's transaction and everything it did.
4. **Rerun the migrator** once the blocker is gone. The failed script's transaction was rolled back and it was **not** journalled, scripts that ran before it stay applied (one transaction per file), and scripts after it were never attempted. Rerunning the same command applies the remaining pending files; nothing is applied twice (the journal is compared before each run).
5. **If it times out repeatedly**, apply in a quiet window — the deploy gate's maintenance slot — rather than stretching the wait. Raising `EVIDGATE_MIGRATOR__LOCKTIMEOUTSECONDS` for one deploy job is allowed, but it only moves the failure later: every query queued behind the DDL keeps waiting that much longer, which is the traffic stall the setting exists to prevent.

## 4. When to raise `statement_timeout`

- Raise it when a script **legitimately** needs longer than 900 s (a large backfill or index build) and the block is a statement duration, not a lock queue: the transcript names `statement_timeout`, not `lock_timeout`.
- Set the variable **for the deploy job** (`EVIDGATE_MIGRATOR__STATEMENTTIMEOUTSECONDS=<n>`, or `0` for that one run) and record why in the deploy log. Never edit an already-applied script to add `set statement_timeout`, and never raise the default for an environment just to get one migration through.
- Raising it does not shorten a lock **hold**: a long statement under `ACCESS EXCLUSIVE` blocks every reader and writer for its whole duration. The hold is bounded by the statement itself and by the deploy window, not by these settings.
- A script that is too long to run inside a transaction belongs in a separate operator step, not in `migrations/` (`migrations/README.md` "Known limits": `create index concurrently` and `vacuum` cannot run in the per-script transaction).

## 5. Scripts that set their own timeout

The prefix runs first, so a script that sets its own `set local lock_timeout` **overrides** the default for that script and keeps it. That is deliberate where the file's header documents the wait it wants:

- `0045`, `0048`, `0095` set `set local lock_timeout = '5s'` explicitly (the 5 s equals the default, but the file states it).
- `0050`/`0051` set `set lock_timeout = '2000'` in the `SECURITY DEFINER` function header, which bounds the waits that function takes whenever it is called, not just during the migration.
- Every script that sets nothing takes the configured defaults.

Do not remove a script's own value to "centralise" it: the value is part of that file's contract, and an applied file is immutable anyway.

## 6. The advisory migration lock is not bounded by these settings

The apply path takes a session-level advisory lock (`pg_advisory_lock`, key `0x6576696467617465`) on a dedicated connection (`src/Evidgate.Migrator/MigrationLock.cs:20-73`). Before it waits, it resets both server timeouts **to 0 on that session** (`set lock_timeout = 0; set statement_timeout = 0`, `MigrationLock.cs:47-53`), so a role- or database-level default cannot turn "another Migrator is running" into an error. A second runner therefore waits for the first one as long as it takes, printing that it is waiting; the deploy job's own timeout is the bound. `--status` and `--dry-run` never take the lock, so a read-only probe does not queue behind a long migration.

## 7. Prior art

Approach taken, no text copied. ⚠️ All three external statements below are from each project's documentation as understood at the time of the change, not re-read for this page:

- ⚠️ Flyway and golang-migrate expose no per-migration `lock_timeout`: their PostgreSQL support retries or waits on the migration-table lock, but does not set a server `lock_timeout` around each script's DDL.
- ⚠️ Rails `strong_migrations` recommends a short `lock_timeout` around DDL and retrying the interrupted migration, because the DDL only waits and the retry is cheap once the blocker is gone.
- Taken from those: a short default `lock_timeout` set **inside the same transaction** as the DDL (so it disappears with the script), a generous `statement_timeout` so a normal index build is not cut, and a distinct exit code per timeout so a deploy gate can see the difference without parsing text.
- Deliberately **not** followed: an automatic retry loop. A rerun here is an explicit operator/deploy-gate decision — the same session that timed out may be the one holding the lock, and a retry loop would hide a table that is permanently contended instead of failing the deploy.

## 8. Evidence

- `src/Evidgate.Migrator/MigratorOptions.cs:23-49,74-78,187-203` — variables, defaults, validation (`0` disables, `2147483` ceiling, fail closed).
- `src/Evidgate.Migrator/ScriptTimeoutPreprocessor.cs:15-22` — the prepended `set local` line.
- `src/Evidgate.Migrator/MigratorRunner.cs:172-204,227-263` — DbUp wiring (`WithTransactionPerScript`, preprocessor), `55P03`/`57014` mapping, transcript line, rollback line.
- `src/Evidgate.Migrator/MigrationLock.cs:38-73` — advisory lock and the reset to `0` before it waits.
- `src/Evidgate.Migrator/MigratorExitCodes.cs:28-32` — exit `5` and `6`.
- `infra/config-matrix.json` (`EVIDGATE_MIGRATOR__LOCKTIMEOUTSECONDS`, `EVIDGATE_MIGRATOR__STATEMENTTIMEOUTSECONDS`) and `infra/{dev,staging,prod}/environment.json` — the keys and their defaults per environment.
