# ADR-0009 — Event-class table partitioning: key, dedupe key and retention windows

Legend: ✅ verified with link · ⚠️ unverified · `[INFERENCE]` — mark every external claim with one of these.

## Status

proposed

Owner confirmation pending (O-06.4.6-1: `metadata_days` cap and event-class ceiling; O-06.4.6-2: 00 §8/§11 and plan 04 §8 text). Nothing here is accepted until the owner confirms.

## Date

2026-10-27

## Context

06.4.6 (`plans/features/06-evidence-artifacts/06.4.6-retention-and-partitioning-for-high-volume-tables.md`) must decide, before the first production row exists, how `event`, `event_batch`, `case_attempt` and `evaluation` are retained and whether they are partitioned, without losing the A10 at-least-once dedupe guarantee (00 §9.4 dedupe by `event_id`; ruling T50 one-statement insert; review D4).

Facts about the code on `develop` at 036e536 (verified through the migrations and the Ingestion module, not from the plan text):

- `run` (`migrations/0031_orchestration_run.sql:88`) has `primary key (id)`, `unique (org_id, id)` (`:26` of the file's table body) and `created_at timestamptz not null default now()`; no `unique (org_id, id, created_at)` yet. `run.late_window_minutes` is checked `between 1 and 525600` (365 d).
- `case_attempt` (`0032_orchestration_case_attempt.sql:87`) keys: `primary key (id)`, `unique (org_id, id)`, `unique (org_id, run_id, execution_key, attempt_no)`, FK `(org_id, run_id)` → `run (org_id, id)`. The migration itself says 06.4.6 owns the later repartition (`0032…:45-47`). `artifact.artifact_attempt_fk (org_id, attempt_id)` → `case_attempt (org_id, id)` (`0032…:164-168`) is the only FK that references it.
- `event` (`0034_ingestion_event_and_batch.sql:95`) keys: `primary key (id)`, `unique (org_id, id)`, `unique (org_id, run_id, producer_id, sequence)`, `unique (org_id, run_id, event_id)` (line 121), `received_at timestamptz not null default now()`; `event_batch` (`:180`) keys `primary key (id)`, `unique (org_id, id)`, FK `(org_id, run_id)` → `run`, `created_at`. Nothing references `event` or `event_batch` by FK.
- The hot-path insert uses `on conflict do nothing` **with no conflict target** (`src/Modules/Evidgate.Modules.Ingestion/Application/Commands/IngestEventBatch.cs:380` comment, `:664` statement) so that either unique key absorbs a duplicate or a reused sequence without raising 23505. The plan text's "ON CONFLICT target gains the column" is therefore moot: the target list does not exist. What changes is that the insert must supply `run_created_at`.
- No `evaluation` table exists yet (no `create table` in `migrations/`); story 07.2 owns it.
- `IEvidenceFreeze` joins `case_attempt` by `(org_id, run_id, execution_key, attempt_no)` in `src/Modules/Evidgate.Modules.Evidence/Infrastructure/Persistence/FreezeSql.cs:46`.

Expected volume is a pilot-scale estimate (≈ 100k results/day, 00 §11), not a measurement `[INFERENCE]` (OI-06-12, 10.6.1).

## Decision

**Default C [proposed]: range-partition `event`, `event_batch` and `case_attempt` monthly by `run_created_at`, a denormalised, immutable copy of `run.created_at`.** `evaluation` is not partitioned (see Retention). Specifics:

1. **Keys.** Every primary key and unique constraint on a partitioned table includes `run_created_at`: `primary key (id, run_created_at)` and `unique (org_id, id, run_created_at)` replace the current `(id)` / `(org_id, id)` keys; the dedupe keys are `unique (org_id, run_id, event_id, run_created_at)`, `unique (org_id, run_id, producer_id, sequence, run_created_at)` and, for `case_attempt`, `unique (org_id, run_id, execution_key, attempt_no, run_created_at)`. A composite FK `(org_id, run_id, run_created_at)` → `run (org_id, id, created_at)` pins the copy to the run row, so for one `run_id` there is exactly one possible `run_created_at` and `(org_id, run_id, event_id)` stays unique globally. `run` gains `unique (org_id, id, created_at)` (additive). The FK's `ON UPDATE NO ACTION` also stops any later change of `run.created_at` while children exist; `run.created_at` stays immutable.
2. **Partitions.** Monthly, UTC bounds, names `<table>_y<YYYY>m<MM>`. The job keeps the current month + 3 ahead (06.4.6 AC5). A `DEFAULT` partition is a safety net: a row with no partition lands there, ingest never fails, and `partition_default_nonempty` alerts.
3. **Create and drop without long locks [DECISION D-S113-Q1, proposed].** New partitions are built standalone with a matching `CHECK` and attached with `ATTACH PARTITION` (SHARE UPDATE EXCLUSIVE on the parent), not `CREATE TABLE … PARTITION OF` (ACCESS EXCLUSIVE). Because the table has a `DEFAULT` partition, **`DETACH PARTITION … CONCURRENTLY` is not available** (✅ [ALTER TABLE, DETACH PARTITION](https://www.postgresql.org/docs/18/sql-altertable.html#SQL-ALTERTABLE-DETACH-PARTITION): "not allowed if the partitioned table contains a default partition"; also "cannot be run in a transaction block"). The job therefore uses plain `ALTER TABLE … DETACH PARTITION` followed by `DROP TABLE` of the detached table, with `SET lock_timeout` ≤ 5 s and retry on the next cycle; the detach holds ACCESS EXCLUSIVE on the parent only for a metadata change. The job runs outside a request transaction (its own connection, maintenance role). 06.4.6-T2.2 MUST expose the lock wait: metric `partition_detach_lock_wait_seconds{table}` and counter `partition_detach_lock_timeout_total{table}`, with an alert when a partition past its ceiling has hit the timeout on 2 consecutive cycles. The 06.4.6 plan text that assumes `DETACH CONCURRENTLY` plus a `DEFAULT` partition is corrected by this ADR (plan Notes line).
4. **Drop rule.** A partition is dropped only when its **upper bound** is older than the table's retention ceiling, so the newest run in it has had the full window; no non-terminal run lives in it; 06.4.2 reports artifact deletion complete for those runs (`artifact_attempt_fk` makes the artifact-first order mandatory); no open legal-hold or deletion job. Otherwise `skipped` + reason + alert after 2 cycles (06.4.6 AC6).
5. **Retention is counted from run creation, not from event receipt.** A late event (late window up to 365 d, 0031 check) for an old run lands in that run's partition and expires with it.

**Fallbacks.** B (unpartitioned `event_dedupe` side table) if C cannot be built; D (no partitioning, batched `DELETE` by age) as the zero-risk fallback if the maintenance job is not ready, keeping retention enforced.

### Retention windows (proposed, owner confirmation pending)

| Table | Partitioned | Window | Rule |
|---|---|---|---|
| `event`, `event_batch`, `case_attempt` | yes, monthly by `run_created_at` | ceiling = the platform cap on `metadata_days` | partition dropped when its upper bound is older than the ceiling; a project with a shorter `metadata_days` keeps rows until the ceiling (never deleted early) |
| `evaluation` | no | `max(project metadata_days, life of any release record that references it)` | row-level delete by the 06.4.2 handler, child rows first; never while referenced by a release decision (AC8) |
| `evidence_revision`, `run`, artifacts | unchanged | 05 / 06.4.1 | not touched by this ADR |

**`metadata_days` cap [proposed, owner confirmation pending]: 90 days in V1, equal to the event-class ceiling.** The plan text proposes a 365-day cap beside a 90-day ceiling, which is inconsistent (a project allowed 365 d would lose events at 90 d). The cap and the ceiling must be one number. 90 d is the default already recommended by OI-06-10 and bounds storage at about cap + one month. Raising both to 365 d later is a configuration change plus ≈ 4× event-table storage; no schema change. If the owner prefers 365 d, only the ceiling changes. Recorded in OI-06-10 (`[OPEN]`); no value is accepted.

### Knock-on list (consumers of the key change; each is a 06.4.6-T2 sub-task unless named)

1. 04.1.1 `IngestEventBatch` insert (`IngestEventBatch.cs:664`) and the duplicate read-back after it supply `run_created_at` (read from the run row the handler already loads/locks, so no extra query; T91/T101). The conflict clause stays `on conflict do nothing`.
2. EF Core mappings: in `src/Modules/Evidgate.Modules.Ingestion/Infrastructure/Persistence/Event.cs` the `HasKey(row => row.Id).HasName("event_pkey")` call and the two unique `HasIndex(...)` calls (the `(OrgId, RunId, ProducerId, Sequence)` index and `event_run_event_id_key`, `:159`) change to the widened keys; same for `event_batch` and `case_attempt`.
3. `artifact.artifact_attempt_fk (org_id, attempt_id)` must reference a unique key that includes the partition key, so `artifact` gains `attempt_run_created_at` and the FK becomes `(org_id, attempt_id, attempt_run_created_at)`; owner of the change: 06.4.6-T2 with 06.1.x.
4. `FreezeSql.cs:46` left join to `case_attempt` adds `run_created_at` (06.5.1 hand-over, 06.4.6 plan Notes).
5. 02.9.1-T2 composite-FK lint and the 02.6.1 RLS assertions need a documented rule for partitioned parents: the referenced unique key must contain the partition key, and partitions inherit RLS from the parent only when queried through it, so `evidgate_app` gets grants on the parent only. `contracts/rls-exceptions.txt` is not the place. `[INFERENCE]` pending confirmation by the 02.9.1 owners (OI-06-11).
6. New behaviour, no new endpoint: event-level reads past retention answer 410 `evidence_expired` (or an empty page with `expired: true`), never 500; registered in 00 §9.2 at DoR (OI-06-11).
7. Contract change requests, logged `[OPEN]`, not applied here: CR to 00 §8/§11 ("event-class tables are range-partitioned by `run_created_at`; the dedupe key includes it; retention ceiling per table") and to plan 04 §8 risk row (which already says dedupe keys must be redesigned first). Receiver: the 06.4.6 DoR pass; recorded as OI-06-11 and O-06.4.6-2/-3.

## Alternatives rejected

| Option | Verdict | Reason |
|---|---|---|
| A. Partition by `received_at`, key `(org_id, run_id, event_id, received_at)` | rejected | ✅ the unique key must contain every partition-key column ([DDL, Limitations](https://www.postgresql.org/docs/18/ddl-partitioning.html#DDL-PARTITIONING-DECLARATIVE-LIMITATIONS)); a retry received after a month boundary then inserts a second row, so dedupe fails exactly when retries are slowest (spool replay) |
| B. Unpartitioned `event_dedupe(org_id, run_id, event_id)` side table | fallback | keeps dedupe but adds a second write to the T50 one-statement hot path, and the side table never drops with a partition, so it needs row deletes |
| **C. Partition by `run_created_at`** | **chosen default (proposed)** | dedupe survives because the key's extra column is pinned to the run by FK; drops align with run age |
| D. No partitioning, batched `DELETE` by age | fallback | bloat, vacuum load and long locks at scale (✅ dropping a partition avoids the `VACUUM` cost of a bulk `DELETE`, [DDL overview](https://www.postgresql.org/docs/18/ddl-partitioning.html#DDL-PARTITIONING-OVERVIEW)); acceptable only until the job is ready |
| E. Hash partition by `run_id` | rejected | a key containing `run_id` dedupes, but there is no cheap time-based drop, so it does not solve D4 |
| C′. Option C without the `DEFAULT` partition, to allow `DETACH CONCURRENTLY` (option (2) of D-S113-Q1) | rejected | an insert for a month with no partition would then raise an error and fail ingest (✅ [CREATE TABLE](https://www.postgresql.org/docs/18/sql-createtable.html): "If no suitable partition exists, an error will occur"); a brief ACCESS EXCLUSIVE with `lock_timeout` once per month per table is cheaper than losing events; it would also need `FINALIZE` crash recovery |

## Consequences

- The migration (06.4.6-T2) is a recreate, not an `ALTER`: ✅ a regular table cannot be turned into a partitioned one ([DDL, Declarative Partitioning](https://www.postgresql.org/docs/18/ddl-partitioning.html#DDL-PARTITIONING-DECLARATIVE)). `event`, `event_batch` and `case_attempt` already exist in migrations 0032/0034 with no production data, so the data move is dev seed only; after production data exists the same change is a data move, so this ADR gates the first production row.
- One extra 8-byte column and one wider key per table; `run` gains one additive unique key.
- A `DEFAULT` partition holding rows blocks later partition creation for overlapping bounds unless scanned or excluded by a `CHECK` (✅ [CREATE TABLE](https://www.postgresql.org/docs/18/sql-createtable.html): the default partition "must be scanned" when a partition is added unless a constraint proves otherwise). Runway 3 months + the alert make that rare; `partition_default_nonempty` is a paging alert.
- Plain `DETACH` takes ACCESS EXCLUSIVE on the parent briefly and also takes a SHARE lock on tables that reference it ([ALTER TABLE](https://www.postgresql.org/docs/18/sql-altertable.html#SQL-ALTERTABLE-DETACH-PARTITION)); `lock_timeout` ≤ 5 s keeps ingest from stalling.
- `DETACH … CONCURRENTLY` crash recovery (`FINALIZE`) does not apply with option 1 above. If a future ADR drops the `DEFAULT` partition, `FINALIZE` and "at most one partition pending detach at a time" become requirements.
- Event-class data is deleted by age of the run, not age of the event; a late event can have less than the full window left. Documented in the runbook; acceptable because late evidence creates a new revision and the decision stays readable (AC7).
- Unblocks the design of 06.4.6-T2 (migration + maintenance job) and T3 (boundary tests); unblocks 02.9.1-T2 lint-rule wording. Costs: 06.4.6-T2 grows the knock-on edits above (the plan's size S + M + M is unchanged: they sit in T2.1).
- Owner decisions still open: O-06.4.6-1 (cap 90 vs 365 d), O-06.4.6-2 (00/04 CR text), O-06.4.6-3 (knock-ons confirmed with 02.9.1 owners).

## Evidence

- ✅ Unique/primary key on a partitioned table must include all partition-key columns, no expressions: [PostgreSQL 18 §5.12.2.3 Limitations](https://www.postgresql.org/docs/18/ddl-partitioning.html#DDL-PARTITIONING-DECLARATIVE-LIMITATIONS); multi-level rule and PK sharing the restriction: [CREATE TABLE](https://www.postgresql.org/docs/18/sql-createtable.html).
- ✅ `DETACH PARTITION … CONCURRENTLY`: SHARE UPDATE EXCLUSIVE on parent and partition, two transactions, cannot run in a transaction block, not allowed with a `DEFAULT` partition; `FINALIZE` completes an interrupted detach, at most one partition pending at a time: [ALTER TABLE](https://www.postgresql.org/docs/18/sql-altertable.html#SQL-ALTERTABLE-DETACH-PARTITION). The plan's earlier ⚠️ ("re-verify against PG 18 docs") is resolved by these two pages; the "interrupted detach leaves a detached partition the next cycle drops" wording in plan §6 is **not** what the docs say (a pending detach needs `FINALIZE`) and is corrected by a Notes line.
- ✅ Detach/drop locking and `ATTACH PARTITION` with a pre-built `CHECK`: [Partition Maintenance](https://www.postgresql.org/docs/18/ddl-partitioning.html#DDL-PARTITIONING-DECLARATIVE-MAINTENANCE); `CREATE TABLE … PARTITION OF` and `DROP TABLE` need ACCESS EXCLUSIVE on the parent ([CREATE TABLE](https://www.postgresql.org/docs/18/sql-createtable.html)).
- ✅ Repo facts: `migrations/0031_orchestration_run.sql`, `0032_orchestration_case_attempt.sql:45-47,164-168`, `0034_ingestion_event_and_batch.sql:95-121,180`, `IngestEventBatch.cs:380,664`, `FreezeSql.cs:46` (read at base 036e536).
- Prior art (RULES.md; approach taken, no text copied): [pg_partman](https://github.com/pgpartman/pg_partman/blob/master/doc/pg_partman.md) (a) always creates a default partition and ships `check_default()` plus a move-data helper ("better than throwing an error and losing the data") → taken: `DEFAULT` + `partition_default_nonempty`; (b) premake default 4 → taken: runway of 3 ahead and an alert below 2; (c) retention by upper bound, detach vs drop configurable, never drops the last child → taken: upper-bound rule; not taken: its extension/BGW packaging (we use our Cronos task queue and the maintenance role) and keeping detached tables. PostgreSQL's own partition-maintenance example (attach a pre-checked table) → taken for `ATTACH`.
- ⚠️ Not measured: real event-table growth and whether managed-PostgreSQL PITR treats a detached-then-dropped partition as dropped at the restore point (OI-06-12; 06.4.6-T3.2, 10.5.1 drill). The 365-day alternative storage multiple (≈ 4×) is `[INFERENCE]` from 90 d vs 365 d.

## Revisit trigger

Re-open when: the owner decides O-06.4.6-1 differently; measured growth (10.6.1) shows monthly partitions are too large or too small; the maintenance job cannot meet `lock_timeout` ≤ 5 s without stalling ingest (then drop the `DEFAULT` partition and use `DETACH … CONCURRENTLY`/`FINALIZE`, or move to weekly partitions); PostgreSQL lifts the `DEFAULT`-partition restriction on `DETACH CONCURRENTLY`; or a PITR drill shows a dropped partition is resurrected on restore.
