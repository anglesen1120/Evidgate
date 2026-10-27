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
  `evidgate_resolve` resolvers, `0015` is 02.7.1-T1's `audit_log`, `0026` is 04.7.1-T2's
  `adapter_capability`, `0027` is 07.3.1-T1's `waiver`, `0028` is 06.1.1-T1's
  `artifact`/`artifact_kind_policy`, `0029` is 04.1.2-T4's `producer_slot`, `0030` is 07.3.1-T2's
  `quarantine_entry` plus its `quarantine:write` catalogue permission, `0031` is 05.2.1-T1's
  `run`/`planned_execution` plus the deferred `artifact`/`producer_slot` run keys and `0032` is
  05.3.3-T1's `case_attempt` plus the deferred `artifact` attempt key, `0033` is 07.1.1-T4's
  `policy_activation` (the project's policy pointer) plus the `policy_revision` state grant, its
  one-active partial unique index and the widened `run.late_window_minutes` bound, `0034` is
  04.1.1-T1's `event`/`event_batch` plus the `producer_slot.last_sequence` column grant, and `0035` is
  04.1.2-T5's `producer_slot` declared-values columns (`declared_last_sequence`/`declared_count`) plus
  their non-negative CHECKs, the all-or-none `closed_at`/declared-values CHECK and the `closed_at`/declared-values column grant, and `0036` is
  05.3.1-T1's `run.current_evidence_revision_id` nullable pointer column (no FK yet: `evidence_revision`
  arrives with 05.3.2-T5, and `0049` then adds the composite FK) plus the `update (state)` column grant on `run`, and `0037` is 05.2.2-T1's
  `run` branch/PR/commit identity columns (`ref_kind`, `ref_name`, `pr_number`, `head_sha`, `base_sha`,
  `trigger_event`, `is_fork`, `unverified_ref`) with their bound CHECKs, the `run_pr_requires_number`
  cross-column rule and the `run_identity_immutable` BEFORE UPDATE trigger that refuses any change to them,
  plus the `ci_token_issue_run_idx` index on the Identity-owned `ci_token_issue`
  (`project_id`, `repository_id`, `run_id`, `run_attempt`, `created_at desc`) that the verified-caller
  check reads — it keys on the minted token's run reference, not on the credential `jti`, which the
  exchange never stores (D-S45-Q2, 2026-10-13)
  and `0038` is 05.2.2-T2.2's `run_project_ref_idx (project_id, ref_name, created_at, id)` on `run`, the
  keyset index of the run list's `ref_name` filter (plain `create index`, one transaction, no
  `concurrently`: see the file header), and `0039` is 13.1.1-T1.1's authoring tree — `feature_node`,
  `checklist_item`, `manual_test_case`, `authoring_code_counter` and the append-only
  `authoring_version` (FORCE RLS, the tenant and owner policies, the `nulls not distinct` partial
  sibling-position index) plus `project.case_prefix` with `evidgate.case_prefix_stem()`, the
  dedupe backfill, `project_org_case_prefix_uq` and the `project_case_prefix_default` insert
  trigger, and `0040` is 13.1.1-T2.1's authoring permission seed — the four 00 §2:60 strings
  (`authoring:read`, `authoring:write`, `authoring:generate`, `script:generate`) with their default
  built-in role grants, no DDL (the file header records why), `0041` is 02.3.1-T1's `user`,
  `user_identity` and `session` (platform scope, D12: FORCE ROW LEVEL SECURITY with an owner-only
  policy and no privilege for the app role; access is through `SECURITY DEFINER` `evidgate_resolve`
  functions added by 02.3.1-T1.4 and 02.3.2), and `0042` is 02.3.1-T1.4's login resolvers — the
  `session.cookie_hmac`/`session.pepper_version` credential columns with their unique lookup index and
  `evidgate_resolve.login_upsert_identity`/`session_create`, the two `SECURITY DEFINER` writers of the
  platform identity tables (a cookie is stored only as an HMAC, the login upsert is race-safe on
  `(provider, subject)`, and a disabled account is a returned status rather than an exception), and `0043`
  is 02.1.2-T2's invite flow — `membership.invite_pepper_version` with the `membership_invite_token_all_or_none`
  CHECK (the three token columns plus the expiry move together), `organization.invite_autobind_verified_domain`,
  the `evidgate_resolve.invite_by_secret`/`invite_autobind_candidates` lookups of T75 and the
  `evidgate.expire_pending_invites` sweep (T80: `evidgate_worker_sched` only), and `0044` is
  02.3.1-T2's login binding and linking schema — the deferred `membership_user_fk`
  (`membership.user_id → public."user"`, added `not valid`, orphan-checked, then validated, with the
  `membership_user_idx` parent-side index), the read-only `evidgate_resolve.session_user` session
  lookup and the `evidgate_resolve.login_link_identity` writer that inserts, re-confirms or moves one
  `(provider, subject)` identity and answers `conflict` rather than merging accounts, and `0045` is
  02.3.2-T1's session hardening — `session.rotation_required_at` (a privilege change marks the live
  sessions and the next authenticated request rotates, then clears the mark), the fillfactor-80 rewrite
  and the index swap (`session_user_revoked_expires_idx` becomes `(user_id) where revoked_at is null` so
  the sliding `expires_at` is not indexed; the full unique `session_cookie_hmac_key` stays the hot-path
  index because a partial one could not tell `session_revoked` from `unauthenticated`), the
  `session_authenticate`/`session_rotate`/`session_revoke_all_for_user`/`session_require_rotation`
  functions, the `session_create` predecessor parameter (the forwarded cookie's row is revoked in the
  same statement) together with a `p_now` clock parameter (`created_at` and `last_seen_at` are stamped
  from the caller's clock, so the idle slide compares one clock — the 9-arg signature is revoked and
  dropped, not left behind), the `login_link_identity` cookie input, and the drop of the read-only
  `evidgate_resolve.session_user` lookup the cookie path no longer uses, `0046` is 12.3.1-T1.1's
  `usage_ledger_entry` — the append-only, tenant-scoped usage ledger every metered metric writes one
  deduplicated row into (`unique (org_id, billing_key)`, T43/AC1; the composite `adjustment_of`
  self-reference plus the reason CHECK that makes a correction a new row, AC2; the quantity CHECK
  that allows a negative quantity only on an adjustment and the 1-32 character `unit` CHECK;
  `revoke update, delete` for the app role, the SQL half of C6/AC3), and `0047` is 12.3.1-T1.2's
  `usage_rollup` — the per-`(org, project, period, metric)` aggregate whose coalesce-project unique
  index is the idempotent recompute's upsert target (AC6, NFR-12-1), created `with (fillfactor =
80` and granted a column-scoped app-role UPDATE, and `0048` is
  02.3.2-T2.1's session ROUTES — `session.expired_at` plus the `SECURITY DEFINER`
  `evidgate_resolve.session_list` / `session_revoke_current` / `session_revoke_one` /
  `session_revoke_others` functions the four `/v1/auth` session endpoints call and the
  bounded `evidgate.session_cleanup` sweep the `session-cleanup` task loops (the
  acting user resolved from the presented cookie hash INSIDE every definer, and EXECUTE
  on the sweep granted to `evidgate_worker_sched` alone), and `0049` is 05.3.2-T5's
  `evidence_revision` — one immutable row per frozen evidence set of a run (`unique (org_id, run_id,
revision_no)`, the closed `missing[]` code CHECK of 00 §8.5 including `setup_error`, the
  `completeness`/`outcome`/watermark CHECKs, `revoke update, delete` for the app role, the SQL half of
  C6/AC9), a BEFORE INSERT trigger that makes `revision_no` gap-free by taking the run row first, and
  the pointer wiring 0036 deferred — the composite FK `run_current_evidence_revision_fk`
  (`(org_id, current_evidence_revision_id) → evidence_revision (org_id, id)`), the
  `update (current_evidence_revision_id)` column grant on `run`, the `run_evidence_pointer_guard`
  trigger that only lets the pointer move to the latest revision of the same run (an owner-only clear
  for erasure), and the `run_evidence_pointer_insert_guard` trigger that keeps a fresh run's pointer
  NULL;
  the next free number is `0050`;
  a gap means two branches invented the same sequence and neither history can be trusted.
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

## Notes on the usage tables (12.3.1-T1, `0046`/`0047`)

- `0046`'s `usage_ledger_entry` is append-only by grant, not by trigger, and
  the grant binds the app role only: `evidgate_app` holds `select, insert` and
  nothing else, so a correction is an adjustment row (`adjustment_of` → the
  corrected row's `(org_id, id)`, the reason CHECK requiring a 1-2000 character
  reason) and the original row is never rewritten. The owner/migration role
  keeps `update` and `delete` (an erasure sweep or a backfill is a real UPDATE),
  which is why the immutability claim is about the app role and not about the
  table. `DELETE` is owner-path only; the `usage_ledger_entry_owner_backfill`
  policy is what lets that owner path (and a backfill) see the rows under FORCE
  row level security. That policy is the accepted `0004`/`0028` precedent —
  `for all to evidgate_owner using (true) with check (true)` — because FORCE row
  level security applies to the table owner too and the alternative,
  `BYPASSRLS`, is what DECISIONS F5 rejects.
- `0046` bounds the two numeric-ish columns the emitters cannot police alone:
  `usage_ledger_entry_quantity_check` (`adjustment_of is not null or quantity >=
0`) makes a negative quantity legal only on an adjustment row, and
  `usage_ledger_entry_unit_check` (`char_length(unit) between 1 and 32`) keeps
  `unit` a nonempty short label (PostgreSQL `char_length` counts characters, so
  a 32-character multibyte unit is inside the bound). Both are backstops for the
  domain rules of `RecordUsageBatch`, which answer 422 first, and the 32 is the
  contract's `IUsageLedger.MaxUnitLength`. Both bounds are additive to the
  feature plan §4, whose column list gives `quantity numeric not null` and
  `unit text not null` with no CHECK on either — recorded here and in the
  migration header, not read from the plan.
- `0047`'s `usage_rollup` is the recompute's upsert target, and it is the one
  place where the plan's `unique (org_id, project_id, period, metric)` needed a
  correction: a plain unique key treats every NULL `project_id` as distinct, so
  an org-level aggregate (support hours, tenant-wide limits) would be appended
  again on every recompute instead of updated. The index is therefore
  `usage_rollup_scope_key` over
  `(org_id, coalesce(project_id, '00000000-0000-0000-0000-000000000000'), period,
metric)` and the recompute declares exactly that expression as its `on
conflict` arbiter. The all-zero UUID cannot collide with a real project: ids
  are UUIDv7 (00 §7). `nulls not distinct` (0007/0039) would carry the same
  equality; the coalesce form is used so the arbiter names the null rule
  explicitly in the same expression the index is built on. `evidgate_app` may
  `update` only the derived columns
  (`total_quantity`, `total_amount`, `currency`, `row_count`, `gap_flag`,
  `computed_at`) — never the key it belongs to — and may not `delete`. The table
  is created `with (fillfactor = 80)` (P-2), additive to the plan §4, which gives
  no storage parameters: every row is an upsert target that is rewritten once per
  recompute, so leaving headroom in each page keeps the new tuple on the same page
  instead of fanning the table out over HOT chains.
- **Known gap, 12.3.1-T2.2 (T60).** `usage_ledger_entry.quantity` is
  `numeric not null`, the literal of the feature plan §4. T60 and the same plan's
  §7/§9 say `egress_gb` rows must carry `quantity null` with a reason until
  `IObjectStore` reports egress ("unavailable, never 0"). Both cannot hold for
  one column. `0046` keeps the plan's NOT NULL and records the conflict in its
  header; 12.3.1-T2.2 owns the resolution (a follow-up migration relaxing the
  column with a reason-required CHECK, or the plan dropping the null-quantity
  rule). Until then no null-quantity `egress_gb` row can exist — the column
  refuses it — while a numeric one can (a measured egress byte count is an
  ordinary ledger row), so AC8's "unavailable, never 0" is a read-path decision
  of the summary query (12.3.1-T6 / 09.7.1), not something this schema can
  express.

## Notes on the session routes (02.3.2-T2, `0048`)

- `session.expired_at` is the reporting half of the `active → revoked | expired` machine
  (00 §8.9) and nothing reads it on the request path: expiry stays DERIVED from
  `expires_at`/`absolute_expires_at` (`0045:381`), so the column is nullable with no
  default and no backfill, and a revoked row keeps it null. `session-cleanup` stamps it
  once per dead row and never re-stamps.
- The sweep deletes LEAF FIRST, and that is a data rule: `session.rotated_from` is the
  self-FK of `0041:146` with no `on delete` action, so a row a remaining row still
  references cannot be deleted at all. The delete skips any row with a referrer, which
  unwinds a rotation chain from its tip across calls instead of failing; the task loops
  bounded batches until a call comes back EMPTY (a short batch is not the stop signal,
  because one chain layer per call is normal), and `MaxBatchesPerFire` still bounds the fire.
- `session_cleanup` clamps its `p_now` to `clock_timestamp()`, so a caller can look back but
  never ahead: a future instant cannot stamp or delete a live row, and only moves the
  30-day cutoff forward.
- EXECUTE is scoped per function: `evidgate_app` for the four route statements,
  `evidgate_worker_sched` alone for `session_cleanup` (the `0043:208-211`
  `expire_pending_invites` posture). `SessionRoutesStore.CleanupAsync` therefore takes
  `IWorkerSchedDataSource`, not the app-role data source, and `AddSessions` registers that
  building block so a host without the task mechanism (the Api) can still resolve the
  store — there it is the fail-closed placeholder, and no Api path calls the sweep.
- No new index was added; `0048`'s header records the measurement reasoning (a partial
  index on a deadline, or on the nullable `expired_at`, would be rewritten by every idle
  slide or stamp, moving cost from a bounded sweep onto every authenticated request).
- Per-call audit rows for the routes are deferred to 02.7.1-T2.2 with the other definers
  (`0048`'s `lint: audit-deferred` line, D-S75-Q4): `audit_log` is org-scoped and its writer
  extends a hash chain, which a definer cannot do. The route-level row an actor's request
  can carry is the application's `IIdentityAuditWriter`, the `LinkEndpoints.ConfirmAsync`
  precedent.

## Notes on `evidence_revision` (05.3.2-T5, `0049`)

- `0049`'s `evidence_revision` is append-only by grant, not by trigger:
  `evidgate_app` holds `select, insert`, and the `revoke update, delete`
  sits in the same file, so a rewrite is `42501` before any trigger or
  policy sees the row (`0028`/`0034`/`0046` precedent). `evidgate_operator`
  keeps `select` only (no policy until 02.8.1, TL T76/D1) and
  `evidgate_worker_sched` holds nothing.
- `revision_no` is gap-free, not merely unique: the BEFORE INSERT trigger
  `guard_evidence_revision_insert` takes the run row `for no key update` and
  refuses any value other than `max + 1` (1 for the first) with `23514`, so
  two concurrent inserts of one run serialise on the run row instead of
  racing into a unique violation.
- Lock order is run first, in the blocking mode: every writer takes the `run`
  row `for no key update`, THEN inserts the `evidence_revision` row, then
  moves the pointer, all in one transaction. The insert trigger re-takes that
  same lock, which is a no-op only for a holder already in that mode: a caller
  holding `for share` alone is upgraded, and two such callers can deadlock
  (`40P01`), so the 05.3.2-T2/T3/T4 reconciler and `LateEvidenceRevisionTask`
  must take `for no key update` on the run before any other read of it.
  `FinalizeShardCommand` (`OrchestratorSql.ShareLockRunForFinalize`) and
  `RunEventScopeReader` read the run `for share` today and must not be chained
  with an evidence insert in one transaction. With the run taken first, an
  inserter cannot deadlock against 05.3.1's transition, which also locks `run`
  first; the reconciler must not lock an `evidence_revision` row before the
  run.
- The pointer is four pieces: `run_current_evidence_revision_fk` (the
  composite FK `0036` deferred — NULL stays legal, MATCH SIMPLE, so another
  tenant's revision is structurally impossible), the
  `update (current_evidence_revision_id)` column grant so the reconciler can
  move it, `run_evidence_pointer_guard`, which accepts only the LATEST
  revision of the SAME run, and `run_evidence_pointer_insert_guard`, which
  refuses a non-NULL pointer on INSERT that resolves to a revision visible in
  the caller's org (`23514`: a fresh run has no revisions, and the composite FK
  alone would accept another run's revision of the same org). A pointer that
  resolves to nothing visible (a missing id or another org's revision, hidden
  by RLS) is left to the composite FK's `23503`, which is the structural
  backstop for the cross-org case. Clearing a set pointer is refused (`23514`)
  except for `current_user = 'evidgate_owner'`: the erasure sweep must clear
  the pointer, then delete the revisions, then delete the runs, because the
  `run` <-> `evidence_revision` FK cycle has no ON DELETE action. Both guards
  run with invoker rights, so their reads go through the caller's RLS scope;
  `GetRun` already selects the column and needs no change.
- The pointer is the latest revision only WHEN IT IS MOVED. The guards check
  the value they are given, so a writer that commits the insert and the
  pointer move in two transactions leaves the pointer one revision behind in
  between, and indefinitely if the second transaction never runs. Keeping both
  in one transaction closes that window: they commit or roll back together, so
  a crash leaves neither a revision without its pointer move nor a stale
  pointer behind.
- `missing[]`'s code list is a CHECK over the closed 00 §8.5 list (`<@`,
  `setup_error` included), so a new reason code is a migration, not a code
  change; a multi-dimensional array is refused (`array_ndims(missing) is null
or 1`, wrapped in a `CASE` because `array_position` raises `0A000` on a
  nested array and CHECK term order is not guaranteed; duplicates are
  tolerated); `completeness = 'complete'` requires
  `missing = '{}'` (AC4), while the converse is the reconciler's rule. An
  incomplete row may carry an empty `missing[]`, and `outcome` is not tied to
  `completeness` (only its value set is constrained), so a reader keys on
  `completeness` alone and never infers it from `missing` or `outcome`.
- Idempotent like `0025`/`0027`/`0028`/`0046`: `if not exists` / `drop … if
exists` and a catalog check around the FK add, so a re-run is a no-op; one
  file runs in one transaction.

## Tests and fixtures

Test migration directories live with their test project
(`tests/Migrator.IntegrationTests/Fixtures/`), never here: the repository's
`migrations/` is the production history and must contain only real changes.
`tests/Isolation.Tests` proves the tenancy contract by applying these files
through the real runner against a throwaway PostgreSQL 18 container.
