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
  NULL, and `0050` is F55's administrative session-revoke audit — `evidgate_resolve.session_revoke_one`
  re-created with the same signature so that the revoke and its `audit_log` row (actor the admin,
  action `auth.session_revoke_admin`, object the revoked `session`, metadata `target_user_id`,
  `org_id = p_org_id`) commit or roll back in ONE transaction, the row chained in SQL under the
  application writer's `hashtext('audit:' || org)` advisory lock and 2000 ms `lock_timeout`, while a
  self revoke and every refusal write nothing (the rows carry `request_id`/`trace_id` as NULL — a
  declared, non-blocking loss: a definition cannot see the caller's `Activity`), and `0051` is F58's
  closure of that loss — `session_revoke_one` re-created as the SIX-argument definition
  (`p_cookie_hmacs, p_session_id, p_org_id, p_now, p_request_id default null, p_trace_id default null`),
  the two ids validated in SQL (`^[!-~]{1,128}$` for the request id, `^[0-9a-f]{32}$` and not the
  all-zero value for the trace id — anything else is NULLED, never refused) and then stored AND hashed
  as the escaped RFC 8785 string the C# writer produces; **deploy order is migrate before app** (an old
  four-argument caller still binds through the defaults, a new six-argument caller needs the file
  first), and the 0050 rows keep their NULL ids and still verify, and `0052` is 08.6.3-T1's
  `healing_switch` — one kill-switch row per scope (`system`/`tenant`/`project`), three partial unique
  indexes carrying the identity, the `(org_id, project_id) → project (org_id, id)` composite FK, the
  closed `mode in ('enabled','shadow','disabled')` vocabulary (`unavailable` is a runtime observation,
  never a stored mode), the `catalogue` read policy that lets every tenant read the tenant-less system
  row while the write policy keeps that row unreachable from a tenant transaction, and the
  `evidgate.healing_switch_set_system` definer the operator route writes the system row through —
  which itself refuses a call made inside a tenant transaction (`23514`, nothing written), because a
  definer runs as the owner and row level security cannot confine it
  (`contracts/rls-exceptions.txt` lists the table because of its NULL-org row);
  and `0053` is 11.5.1-T5's `security_event` (shape and emitter contract in its own notes below);
  and `0054` is 08.5.1-T1.1's LLM gateway tables — `healing_llm_call` (one append-only row per provider
  attempt: the `(org_id, project_id) → project (org_id, id)` composite FK, the closed
  `purpose`/`status` vocabularies and the authoring-versus-incident attribution CHECKs) and
  `healing_llm_budget` (one counter row per project, day and period, whose unique key is the atomic
  consume target, with both caps deliberately outside the app role's grant and an owner/definer
  provisioning path owed to 08.5.1-T1.4/T1.7) — shape, grants and the deferred incident/candidate FKs
  in its own notes below; and `0055` is the tenant-policy role fix — `tenant_isolation` re-created
  `TO evidgate_app` on `security_event` (`0053`) and on `healing_llm_call`/`healing_llm_budget` (`0054`),
  whose policy was declared without a `TO` clause and therefore applied to PUBLIC, plus the
  `evidgate_operator` `select` revoked on all three tables (notes at the end of this file);
  and `0056` is the reconciler's plan-coverage reason — `evidence_revision_missing_check` re-created over
  the `0049` list with `missing_result` appended (added `not valid`, then validated), so a planned
  execution that never produced an attempt can be recorded instead of the revision insert being refused —
  plus the partial index `run_collecting_idx` on `public.run (org_id, id)` `where state = 'collecting'`
  and the collection-deadline scan `evidgate.reconcile_overdue_runs`, a read-only `security definer`
  projection of `(org_id, run_id, collection_deadline_at)` for the overdue and still-collectable runs of
  every tenant, which `evidgate_worker_sched` alone may execute: `public.run` stays ungranted to the sweep
  role, so a platform sweep can name the runs to reconcile without reading a tenant's row
  (notes at the end of this file); and `0057` is 06.5.1's parse-state indexes — `task_parse_artifact_idx` on
  `public.task` and `outbox_parse_artifact_idx` on `public.outbox`, each `(org_id, (payload ->>
'artifact_id'))` and partial on the rows the freeze's "is this artifact's parse verdict still owed"
  anti-joins ask about, so neither lookup is a scan of a mechanism table once per ready artifact
  (notes at the end of this file); and `0058` is the overdue-run half of the same sweep — `evidgate.reconcile_overdue_runs`
  re-created to name `queued` and `running` runs past `collection_deadline_at` beside `collecting` (same signature,
  definer posture and keyset cursor), with the partial index `run_overdue_idx` over those three states replacing
  `run_collecting_idx`, which it subsumes, so a run whose producers never started or never finalized is finally decided
  `incomplete` instead of staying open forever (notes at the end of this file); and `0059` is the stored
  evaluation — the Policy-owned `evaluation` table with its idempotency keys (one row per
  `(org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode)`, at most one `live` row
  per frozen evidence revision), its closed token CHECKs, the composite tenant keys to `run` and
  `policy_revision`, the immutability trigger and SELECT/INSERT-only app grant (notes at the end of this
  file); the next free number is one above the highest file in this folder (`ls migrations/*.sql`), and this README does not
  track it; a gap means two branches invented the same sequence and neither history can be trusted.
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
- T80's "every call audited" for `evidgate.purge_expired_idempotency` is met
  without `audit_log`, which is per-org (`org_id` NOT NULL and a per-org hash
  chain, 0015) while this purge is cross-tenant platform work: naming one org
  would take N per-org chain locks for a delete that spans N orgs. `0088` adds
  the platform-scope append-only `platform_sweep_audit` and the
  `evidgate.record_sweep_audit` definer that `evidgate_worker_sched` alone may
  execute; one row per fire carries the action, the number removed, the batch
  limit and whether the time budget stopped the drain. The handler still logs
  the same facts at Information (EventId 2322).

## Notes on the platform sweep audit (01.5.1-T6.2, `0088`)

- `platform_sweep_audit` is the platform-scope counterpart of `audit_log`, not a
  tenant audit table. It carries no `org_id` at all and is listed as `platform`
  in `contracts/rls-exceptions.txt`, next to `organization`, the platform
  identity tables and `ai_settings_template`. Per-org `audit_log` rows were
  rejected for this record: its `org_id` is NOT NULL and every row is
  hash-chained per org (0015), so a purge that deletes across every tenant would
  have to name one org and take that org's chain lock once per org touched.
- Append-only is enforced at two layers, and neither of them alone covers every
  statement. A guard trigger (`evidgate.platform_sweep_audit_append_only`)
  raises 23514 for UPDATE and DELETE of a row, the same guard 0064, 0072 and 0081
  ship. Row level security is the other layer, and it is where this table differs
  from those three: they also hand the owner a `for all … using (true)` policy, so
  the trigger is what refuses the owner there, while here the owner holds `insert`
  and `select` policies only — an UPDATE or DELETE as the owner changes zero rows
  and no row trigger ever fires (`0015`'s `audit_log` has the same owner
  read/insert shape). The trigger therefore covers the statements row level
  security does not filter: the superuser, and any future role granted an UPDATE
  or DELETE policy. Every runtime role's DML is revoked except the operator's
  `select`. TRUNCATE is not subject to row triggers; the only statement that
  empties the table is the test harness's reset, which runs as the table owner
  (`tests/Shared/RespawnCheckpoint.cs` truncates every table it finds and seeds
  nothing here, which is correct for an append-only audit table).
- There is exactly one writer: `evidgate.record_sweep_audit`, a `SECURITY
DEFINER` function owned by the migration role with a pinned empty
  `search_path`. It stamps `actor = 'evidgate_worker_sched'` itself and returns
  the new row's id; `evidgate_worker_sched` holds EXECUTE on it and no table
  privilege, `evidgate_app` holds nothing and has no policy, and
  `evidgate_operator` holds the `select` grant with no policy — the grant-gated
  read posture 0052:196-199 records for `healing_switch`, so the operator reads
  zero rows until 02.8.1's operator scope opens the grant.
  `scripts/check-rls.py` therefore needs no `DEFINER_AUDIT_EXEMPT` entry: the
  function's body inserts into a table whose name ends in `audit`, which is the
  lint's own per-call-audit signal.
- Known limit of the trail: each purge batch autocommits (every function call is
  its own transaction) and the audit row is written after the loop ends, so a
  hard process kill between a committed batch and that write leaves the removed
  rows unaudited — no in-process path can write that row. Failure and
  cancellation inside the handler are covered on a best-effort basis: the recovery
  write records what the fire had already removed, on a fresh connection within
  5 seconds, and is only logged if that write itself fails.
- Rollback is recorded in the file header. Dropping the table loses the sweep
  history only; no tenant row references it.

## Notes on the usage tables (12.3.1-T1, `0046`/`0047`)

- `0046`'s `usage_ledger_entry` is append-only by grant, not by trigger, and
  the grant binds the app role only: `evidgate_app` holds `select, insert` and
  nothing else, so a correction is an adjustment row (`adjustment_of` → the
  corrected row's `(org_id, id)`, the reason CHECK requiring a 1-2000 character
  reason) and the original row is never rewritten. The owner/migration role
  keeps `update` and `delete` for periods that hold no statement; `0092`
  refuses both for an issued period, for every role. `DELETE` is owner-path
  only; the `usage_ledger_entry_owner_backfill`
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
- Per-call audit rows for the other three routes (`session_list`, `session_revoke_current`,
  `session_revoke_others`) are NOT written, and that is now a recorded decision rather than a
  pending deferral: `0048` deferred them to 02.7.1-T2.2 with the other definers (its
  `lint: audit-deferred` line, D-S75-Q4), that receiver task is merged, and S122/F58 re-pointed
  the whole session family into `scripts/check-rls.py`'s `DEFINER_AUDIT_EXEMPT` — one exact
  qualified name each, with its reason: pre-tenant `auth.login`/`auth.link` written by
  `IdentityAuditWriter`; the lifecycle and privilege-change definers (`session_revoke_all_for_user`,
  `session_require_rotation`) because the calling org-scoped command's own `[AuditedAction]` row
  (`membership.remove`, `membership.update` or a role change) already covers the effect they
  cause; a `security_event` for self-service revokes under D-S102-Q3 (the table exists since `0053`;
  its emitter is an open receiver); or a T80 sweep observed via the task row). The ADMIN revoke is
  not exempt: `0050` has
  `session_revoke_one` append its own `auth.session_revoke_admin` row inside the same
  transaction, so the revocation and its record commit or roll back together, and `0051` keeps
  that while adding the ids. The "a definer
  cannot extend a hash chain" reasoning of `0048`'s header and of its `lint: audit-deferred`
  entry holds only where the RFC 8785 canonical fields cannot be reproduced in SQL; `0050`'s
  header names the three C# sources it mirrors (`AuditHashing.CanonicalFields`,
  `JsonCanonicalizer`, `AuditSql.AdvisoryLock`/`LockTimeout`) and is verified by the existing
  `AuditChainVerifier`, never by a second hashing implementation.

## Notes on the revoke's correlation ids (F58, `0051`)

- `session_revoke_one` is now the SIX-argument definition: 0050's four arguments plus
  `p_request_id text default null` and `p_trace_id text default null`. `drop function`
  (required — a signature change cannot be `create or replace`) discards the ACL, so `0051`
  re-states it: `revoke all ... from public`, `grant execute ... to evidgate_app`. Exactly one
  `session_revoke_one` exists in the catalogue; the four-argument one is dropped, not left as an
  overload.
- **Deploy order: migrate before app.** The defaults keep the old four-argument call binding, so
  an already-running API keeps working the moment the file is applied. The new build binds six
  arguments, so deploying it before the migration answers `42883` on every administrative revoke:
  apply `0051`, then deploy.
- The ids are untrusted input and are validated inside the definer: the request id only in
  `CorrelationNames.IsSafeRequestId`'s shape (`^[!-~]{1,128}$`, visible ASCII, 1-128 —
  `CorrelationNames.cs:53,70-88`), the trace id only as 32 lowercase hex digits that are not the
  all-zero (W3C-invalid) value. An unacceptable value is NULLED — never an error, never a
  refusal — so a malformed correlation id can neither block a revocation nor steer the chain.
- The validated ids reach BOTH the row's `request_id`/`trace_id` columns and the canonical string
  the chain hashes, in the spelling the C# writer produces: a JSON string (`to_jsonb(<text>)::text`,
  which escapes `"` and `\` per RFC 8785 for the accepted ASCII) or the literal `null`. So the
  rows an upgraded definer writes verify with `AuditChainVerifier` next to the application's own
  rows, and the 0050 rows keep `null` ids and keep verifying.
- `0051` is idempotent (`drop function if exists` + `create or replace function` + re-stated
  grants) and touches no data. Rollback is the reverse file: drop the six-argument definition and
  re-state 0050's four-argument one (`0050:108-290`), with the OLD application build in place
  first; rows already written keep their ids.

## Notes on `healing_switch` (08.6.3-T1, `0052`)

- One row per scope, and the scope IS the identity: `healing_switch_system_key (scope) where scope =
'system'`, `healing_switch_tenant_key (org_id) where scope = 'tenant'` and
  `healing_switch_project_key (org_id, project_id) where scope = 'project'`. The resolver reads
  `where scope = 'system' or (org_id = <org> and scope in ('tenant','project'))` and takes the most
  restrictive row (`disabled` > `shadow` > `enabled`); with no row at any scope it resolves to
  `shadow` (the V1 posture: shadow healing that never applies), which is a resolver rule, not a
  stored value — this file inserts nothing.
- `mode` is a closed vocabulary and `'unavailable'` is deliberately NOT in it: availability is a
  runtime observation of a dependency (`dependency_health`, 08.6.3 §5), never something an operator
  stores. `healing_switch_mode_check` refuses the value at the database, so a caller cannot widen the
  vocabulary by skipping the route's validation.
- The tenant-less `scope = 'system'` row (`org_id IS NULL`) is why the table is listed in
  `contracts/rls-exceptions.txt` as `catalogue`: its read policy is the documented all-org read shape
  (`org_id IS NULL OR org_id = evidgate.current_org_id()`), which the boot self-check
  (`permissive_policy_unscoped`) accepts only for that kind. The write policy is the plain tenant
  predicate, so a tenant transaction reads the system row but cannot touch it: an app-role UPDATE of
  it affects zero rows, a DELETE is a privilege error (no DELETE grant exists), and an INSERT with
  `org_id` NULL fails the WITH CHECK (`42501`).
- The system row's only writer is `evidgate.healing_switch_set_system` (`SECURITY DEFINER`, pinned
  `search_path = ''`, EXECUTE to `evidgate_app` alone, and exempt from the per-call audit row because
  `audit_log` is chained per org and this row has no org). It upserts and returns the stored row,
  refuses a mode outside the three with `22023`, survives a concurrent first write by re-running its
  update on the `unique_violation`, and — because it runs with `evidgate_owner`'s rights, which row
  level security does not confine — refuses a call made from inside a tenant transaction with `23514`
  before it touches a row. What attests a system write instead of an audit row is the operator route
  `POST /internal/operator/healing/switch` (behind 02.8.1's fail-closed `RequireOperatorFilter`),
  which writes a structured operator log event (mode, set_by, version — never the reason text) on
  every write, plus the `platform_security_event` row that is still owed: `0053` ships
  `security_event`, and its `org_id` is NOT NULL, so a platform-scope (no-tenant) write has no
  row — D-S137-OWNER (DECISIONS §8.2, decided 2026-10-29) puts such an event in that separate
  table, never a nullable `org_id`; table and emitter are still owed — neither exists yet.
- The app role holds `select`, `insert (id, scope, org_id, project_id, mode, reason, set_by, at)` and
  `update (mode, reason, set_by, at, updated_at, version)`: INSERT is column-level on purpose, so a
  caller cannot name `created_at`, `updated_at` or `version` and backdate a switch change or pin its
  concurrency token. A switch's identity (`id`, `scope`, `org_id`, `project_id`) is unwritable and
  there is no DELETE at all, so a tenant can move its own switch between
  `enabled`/`shadow`/`disabled` and nothing else.
- `reason` is a free text of at most 2000 characters, counted by `char_length` — code points, the
  same unit the routes count — and an empty string passes that CHECK (the routes refuse an empty
  `reason` themselves, 422 `request_invalid` `reason: empty`, so nothing empty arrives through them).
  The column is a stored justification, not a control input: the resolver never reads it.
- Rollback: `drop function if exists evidgate.healing_switch_set_system(text, text, uuid); drop table
if exists public.healing_switch;` plus the `contracts/rls-exceptions.txt` line, in the same change.
  The table holds configuration only.

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

## Notes on `security_event` (11.5.1-T5, `0053`)

- Shape: `id uuid primary key` (a UUIDv7 the emitter picks), `org_id uuid not null`, `project_id uuid`
  (null = the event belongs to no project), `type text not null`, `severity text not null`,
  `actor jsonb not null`, `details jsonb not null default '{}'`, `request_id text`,
  `created_at`/`updated_at timestamptz not null default now()` and `version int not null default 1`.
  `unique (org_id, id)` is the target of any later composite tenant FK; nothing references the table
  yet, so `0053` adds no foreign key.
- `type` and `severity` are closed vocabularies — `auth_failure`, `cross_tenant_probe`,
  `secret_scanner_alert`, `kill_switch_used`, `support_access`; `info`, `warning`, `critical` — owned
  by `docs/security/security-event-baseline.md` and mirrored by the route's `Types`/`Severities`
  arrays and the OpenAPI description, and deliberately NOT by the database: no CHECK, no enum. A new
  type is a taxonomy change first (11.1.1-T4.1), and a CHECK would turn every addition into a
  migration; the cost is that the database cannot refuse a value no reader knows.
- `actor` is the closed `{kind, id}` object of whoever caused the event. `details` carries ids, hashes
  and counts only: never a raw payload, a secret or free-text reason. Neither rule is enforced in
  SQL, so an emitter that writes a payload is a defect its own review must catch (11.5.1 §8, S3).
- RLS is the ordinary tenant shape: `ENABLE` + `FORCE ROW LEVEL SECURITY`. `tenant_isolation` is the
  tenant policy — `org_id = evidgate.current_org_id()` in `USING`, applied as `WITH CHECK` too, and with
  no `TO` clause it applies to PUBLIC, so the app role reaches only its own scope. That PUBLIC scope is
  closed by `0055`, which re-creates the policy `TO evidgate_app` (notes at the end of this file); the
  predicate and the app role's behaviour are unchanged. `security_event_owner_backfill`
  is the house second policy (`FOR ALL TO evidgate_owner USING (true) WITH CHECK (true)`,
  `0004`/`0005`/`0028`/`0046`/`0049`/`0052` precedent): `FORCE` applies to the owner as well, so a
  migration or fixture write would otherwise be refused. It names `evidgate_owner` only and grants the
  app role nothing. The table is absent from `contracts/rls-exceptions.txt` on purpose: `org_id` is NOT
  NULL, so there is no NULL-org read row, and the boot self-check sees a plain scoped tenant policy.
- Two indexes, one per read shape. `security_event_org_created_idx (org_id, created_at desc, id desc)`
  serves the unfiltered page and the `from`/`to` window: those reads filter on `org_id` alone and
  keyset on `(created_at, id)` descending, so the page is index-ordered (P3/P4).
  `security_event_list_idx (org_id, project_id, type, severity, created_at desc, id desc)` narrows a
  read that binds all three filter columns: those equalities come first and the cursor tuple follows,
  so an index scan returns the page already ordered. Two planner choices still sort, legitimately: a
  bitmap scan (cheaper for a small selective page, and it keeps no order) and a partial bind on the
  three filters (an unbound column precedes `created_at` in the key). The plan test explains the
  ordered path. An emitter inserts `insert into public.security_event (id, org_id, project_id, type, severity, actor, details, request_id)` with `org_id` from the tenant scope, inside its own tenant transaction, and needs no definer, no trigger and no outbox row.
- Append-only by grant, the way `0049`'s `evidence_revision` is: the table rides `0001`'s default
  privileges, so `evidgate_app` starts with full DML, and `0053` closes with
  `revoke update, delete, truncate on public.security_event from evidgate_app;`. `select` and `insert`
  remain — exactly what an emitter needs: the application role can record an event and read it back, but
  cannot rewrite or remove one after the fact. The reason: a security record an ordinary tenant request
  could edit is not evidence, and no retention sweep exists yet, so nothing legitimate updates or deletes
  a row today. (A future sweep, if any, is a definer task of its own, like `0015`'s purge.)
  `evidgate_operator` kept `0001`'s default `select` until `0055` revoked it: with the policy applying
  to PUBLIC, that grant let an operator session set `evidgate.org_id` and read any tenant's events
  (notes at the end of this file).
- `updated_at` and `version` belong to the database: `security_event_set_updated_at_and_version` is the
  `0004` convention trigger (`BEFORE UPDATE … FOR EACH ROW`, `public.set_updated_at_and_version()`), so a
  privileged backfill stamps the time and increments `version`. No route writes them — the app role may
  not UPDATE at all (the revoke above) — and a reader must never treat `version` as a concurrency token.
- Platform-scope events do NOT fit: `org_id` is NOT NULL, so neither the NULL-org system healing
  switch (`0052`, 08.6.3-T1) nor the verifier fault (G8, 02.7.1 AC6) has a row here. D-S137-OWNER
  (DECISIONS §8.2, decided 2026-10-29) settles where they go: a separate `platform_security_event`
  table, never a nullable `org_id` here; that table and its emitter are still owed and recorded in
  the 11.5.1 plan Notes and the carried row "Platform security event table (D-S137-OWNER)".
- Rollback: `drop table if exists public.security_event;` — the two policies, both indexes and the trigger
  go with it, and nothing else references the table.

## Notes on `healing_llm_call` / `healing_llm_budget` (08.5.1-T1.1, `0054`)

- Two tables in one file, because the recorder and the budget gate are one command's transaction
  (08.5.1-T1.4): `healing_llm_call` is the ledger of every provider ATTEMPT and `healing_llm_budget`
  is the rolling daily cap a project consumes before a call is placed. Both are new in this story, so
  nothing binds to them before the file is applied (**migrate before app**).
- `healing_llm_call` shape: `id uuid primary key`, `org_id uuid not null`, `project_id uuid not null`,
  `incident_id`/`candidate_id uuid` (nullable), `purpose text not null`, `model text not null`,
  `prompt_version text not null`, `tokens int not null default 0`,
  `cost numeric(12,6) not null default 0`, `status text not null`, `attempt int not null default 1`,
  `redaction_report_hash text`, `created_at timestamptz not null default now()`, plus the 00 §7
  convention columns `updated_at timestamptz not null default now()` and
  `version int not null default 1` (with `healing_llm_call_version_check`, the shape `0052` gives
  `healing_switch`). One row per ATTEMPT, not per invocation: AC1's schema-violation retry and AC2's
  timeout abort are facts about one provider call, so attempt 1 and attempt 2 of a failing
  invocation are two rows. `attempt` is the ordinal inside one invocation and is in no unique key
  (two invocations of one incident both have an attempt 1).
- The ledger is append-only by GRANT, not by shape: no UPDATE and no DELETE for the application role,
  and `created_at`/`updated_at`/`version` are all outside its column-level INSERT. The 00 §7 audit
  columns and the shared `set_updated_at_and_version` trigger exist because §7 requires them on every
  table (the convention scan enforces it), and they are inert here: no app statement names them, so
  they stay at their defaults and the trigger only fires if a privileged backfill ever moves a row. A
  row is written once, with the status the attempt ended in (the recorder inserts after the attempt
  ends), so nothing has to be corrected in place. `tokens`/`cost` are the loss-free replacement for
  the payload, and the budget and the cost report sum them.
- AC6 (S3) is a column-count rule here: there is NO secret, API-key, prompt-text, model-response or
  payload column at all, only `redaction_report_hash` (a hash of the 08.5.3 report, nullable until
  that task lands) and the model/prompt-version labels. The schema test pins the exact column list,
  so an added content column fails a test instead of passing review.
- Attribution (AC4, AC7, T30): `project_id` is NOT NULL on both tables — every call is attributable
  to a project and therefore to a tenant, and it is the column the budget and the cost report read.
  `purpose` is closed: `authoring_generation` (13.3.1-T2's generation, a project generation and not
  an incident), `triage_hypothesis` (08.2.2) and `candidate_generation` (08.5.4).
  `healing_llm_call_authoring_attribution_check` keeps an authoring row free of incident and
  candidate; `healing_llm_call_incident_required_check` makes every other purpose name its incident;
  `healing_llm_call_candidate_incident_check` keeps a candidate inside an incident-scoped call (it is
  implied by the other two and kept so a future purpose cannot introduce the shape by omission).
- `status` is closed: `ok`, `timeout`, `llm_schema_violation`, `budget_exceeded`, `error` — the same
  five values 08.5.1-T1.2's seam constants carry. The plan §4 shorthand `schema_violation` is not
  storable; the retry outcome is `llm_schema_violation`.
- DEFERRED FOREIGN KEYS: `incident_id` and `candidate_id` carry NO key yet, because `incident` and
  `healing_candidate` do not exist. The migration that creates each table owes the composite
  `(org_id, incident_id) → incident (org_id, id)` and `(org_id, candidate_id) → healing_candidate
(org_id, id)` keys, so a call can only ever name an incident/candidate of its OWN org — the same
  reason `(org_id, project_id) → project (org_id, id)` is composite today. Until then the
  attribution CHECKs still bound the row and only application code could write a dangling id.
- Keys and indexes: `unique (org_id, id)` is the composite tenant-FK target every business table
  declares (TL T3). `healing_llm_call_project_created_idx (org_id, project_id, created_at)` serves
  the budget-window sum and `healing_llm_call_project_purpose_created_idx (org_id, project_id,
purpose, created_at)` the per-purpose cost report; both are range scans over a window, not keyset
  pages, so `created_at` is ascending. The project reference cascades on delete, as 0052 does: a
  project row is hard-deleted only by the tenant-erasure path (06.4.4, and the app role holds no
  DELETE), and a ledger or budget row must not block that delete with a `23503`.
- `healing_llm_budget` shape: `id`, `org_id`, `project_id`, `period text not null`, `period_start
date not null`, `period_cap_cost numeric(12,6)`, `period_cap_tokens int`, `used_cost numeric(12,6)
default 0`, `used_tokens int default 0`, `created_at`/`updated_at`/`version`, all NOT NULL.
  `healing_llm_budget_period_key unique (org_id, project_id, period, period_start)` IS the
  atomic-consume target of AC3: T1.4's hard stop is one `update … set used_cost = used_cost + @cost,
used_tokens = used_tokens + @tokens where org_id = … and project_id = … and period = 'daily' and
period_start = … and used_cost + @cost <= period_cap_cost and used_tokens + @tokens <=
period_cap_tokens returning id`, whose zero-row result means no call is placed (the `healing_quota`
  T32 pattern). No consumption code ships in this slice — the key and the column grants are the plan
  that statement needs. `period` is `'daily'` alone: a second value is a new value in the vocabulary
  and a migration, which is what T1.7's reset schedule and the consume statement both spell.
  `period_start` is a `date` because the boundary is a calendar day the caller resolves.
- RLS is the ordinary tenant shape on both tables: `ENABLE` + `FORCE ROW LEVEL SECURITY`,
  `tenant_isolation` as `org_id = evidgate.current_org_id()` in USING and as WITH CHECK, declared
  without `TO` so it applies to PUBLIC — `0055` re-creates both policies `TO evidgate_app`, with the
  same predicate and the same app-role result (notes at the end of this file) — plus the house
  `_owner_backfill` policy (`FOR ALL TO
evidgate_owner USING (true) WITH CHECK (true)`) that `FORCE` makes necessary for a migration or
  fixture write. Neither table is in `contracts/rls-exceptions.txt`: `org_id` is NOT NULL on both, so
  there is no NULL-org read row and the boot self-check sees a plain scoped tenant policy. There is
  no `SECURITY DEFINER` on either table — the tenant write path is a plain app-role statement inside
  the caller's tenant transaction (00 §5.5 C6 prefers a grant to a definer).
- Grants (the revoke first, because `0001`'s default privileges hand `evidgate_app` full DML):
  `healing_llm_call` = `select` + `insert` of exactly its thirteen writable columns (no `created_at`,
  no `updated_at`, no `version`, so an attempt can neither be backdated nor carry a caller-chosen
  token) and nothing else. `healing_llm_budget` = `select` + `insert` of
  the five identity/period columns (`id`, `org_id`, `project_id`, `period`, `period_start`) + `update`
  of exactly the two columns the consume and reset statements move (`used_cost`, `used_tokens`) — two
  columns, not the four-plus-two an earlier revision of this note counted. The identity columns and
  the period stay unwritable, so a consume cannot re-target or backdate the row it matched, and
  neither table has a DELETE. `evidgate_operator` kept a read-only `select` (TL T76) until `0055`
  revoked it — that grant under a PUBLIC policy could read any tenant's ledger and budget rows — and
  `evidgate_worker_sched` holds nothing — the gateway runs in a tenant context, never as a sweep.
- THE CAP WRITER IS OWED, NOT GRANTED: `period_cap_cost`/`period_cap_tokens` are in neither the
  app-role INSERT nor its UPDATE grant, because a tenant transaction able to write either one could
  raise its own ceiling. Both caps are NOT NULL, so the residual app-role INSERT (the five
  identity/period columns) cannot complete a row either: it fails `23502` on `period_cap_cost`, which
  is the intended state and not a gap. The row is provisioned by the OWNER/definer path owed to
  `08.5.1-T1.4` (the budget gate and its atomic hard stop) and `T1.7` (the reset schedule), tracked as
  the carried row `healing_llm_budget cap writer owed (S145)`. Until that path lands no budget row
  exists, so nothing can be consumed — the hard stop itself is T1.4's to implement, not this
  migration's.
- `updated_at`/`version` are outside the app-role grant on purpose and the trigger still runs:
  `set_updated_at_and_version()` (`0004`) is invoker-rights plpgsql (no `SECURITY DEFINER`) with
  EXECUTE granted to `evidgate_app`, and PostgreSQL checks column privileges against the columns a
  statement NAMES, never against the columns a trigger assigns to NEW. `0039`'s `feature_node` is the
  exact precedent — it carries both the `version` column and this trigger while its app-role grant
  (`update (parent_id, kind, title, position, deleted_at)`) leaves `updated_at`/`version` out, as
  `0027`'s `waiver` and `0028`'s `artifact` also do. A consume that sets only
  `used_cost`/`used_tokens` therefore still stamps `updated_at` and increments `version`, while the
  caller itself can never write the token it is validated against.
- Both tables carry the trigger, and on `healing_llm_call` it is INERT by design: 00 §7 (`M1W3-01`)
  requires it on every table with a `version` column, so the convention scan cannot be satisfied by
  the columns alone, but no statement any role may issue updates a call row (there is no UPDATE grant
  at all). Its `updated_at`/`version` therefore stay at the defaults `0054` gives them; the trigger
  exists for the shape and only fires if a privileged backfill ever moves a row.
- Rollback: `drop table if exists public.healing_llm_budget;` then
  `drop table if exists public.healing_llm_call;` — the policies, indexes, the trigger and the grants
  go with them, and both tables hold derived telemetry (calls made, budget consumed), so a rollback
  loses cost attribution history and nothing that the provider's own billing cannot re-derive.

## Tests and fixtures

Test migration directories live with their test project
(`tests/Migrator.IntegrationTests/Fixtures/`), never here: the repository's
`migrations/` is the production history and must contain only real changes.
`tests/Isolation.Tests` proves the tenancy contract by applying these files
through the real runner against a throwaway PostgreSQL 18 container.

## Notes on tenant policy roles (`0055`)

- Why the file exists: `tenant_isolation` reached public.security_event (`0053`) and
  public.healing_llm_call / public.healing_llm_budget (`0054`) without a `TO` clause, so PostgreSQL
  applied it to PUBLIC — every role. `evidgate_operator` held `SELECT` on all three (from `0001`'s
  default privileges and, for the two LLM tables, explicitly at the end of `0054`) and a role may
  `set evidgate.org_id` itself, so an operator session could read any tenant's security events, call
  ledger and budget counters. No application path read these tables as the operator — the tenant
  routes and the recorder run as `evidgate_app` — so the grant bought no read path, only the exposure.
- What changed: all three policies are dropped and re-created as `FOR ALL TO evidgate_app` with the
  same predicate (`org_id = evidgate.current_org_id()` in `USING`, the same expression in
  `WITH CHECK`; `0053` declared `USING` only, and a policy's `WITH CHECK` defaults to its `USING`, so
  the app role's behaviour is identical), and `revoke all on public.<table> from evidgate_operator`
  runs on each. The table-level revoke is the whole of it because no column-level grant to the
  operator exists on those tables. The statements are `drop policy if exists` + `create policy` +
  a repeatable `revoke`, and the file has no down migration. The policy keeps its `tenant_isolation`
  name — the house policy naming rule's `<table>_app_tenant` is the template for a table this
  repository creates from now on — because this file corrects the role clause of one existing policy,
  and a rename would split that policy's history in two and break the lint's exact
  `(file, policy, table)` exemption keys.
- What did NOT change: `0053` and `0054` are applied files and stay byte-identical (the runner refuses
  a checksum mismatch); their notes above are kept as the history of what those files did, with the
  operator sentences marked as superseded here. `ENABLE`/`FORCE ROW LEVEL SECURITY`, the
  `_owner_backfill` policies, the app-role grants, `contracts/rls-exceptions.txt` and `0001`'s role
  definitions are untouched, and no privilege is granted by this file.
- The default privileges still hand `SELECT` to `evidgate_operator` on every table `evidgate_owner`
  creates in `public`; `0055` deliberately does not change them, because some tables are granted to
  the operator on purpose — `schema_migration` (`0001`), and `0003`'s probe table carries an
  `rls_probe_operator_read` policy — and a default-privilege change would silently rewrite that
  decision for every future table instead of deciding it once per table. A new tenant table is safe
  only when its own file names the app role in its policy and decides the operator's grant
  explicitly — revoke it, or add a `TO evidgate_operator` read policy whose predicate compares
  `org_id` to something the session cannot set (`0003`'s `rls_probe_operator_read` reads the session
  on purpose: the probe table exists to show that RLS filters a role that picks its own tenant).
  `scripts/check-rls.py` now enforces both halves: `policy_no_role`
  rejects a `create policy` (or `alter policy`) whose roles are absent, `public`, or a session
  pseudo-role such as `current_user`, and `operator_select_no_policy` rejects a business table whose
  effective policy admits the operator (not role-scoped, or scoped to a role the operator is a member
  of) while the operator still holds `SELECT` and no `TO evidgate_operator` policy confines it. The only
  exemptions are the three superseded `tenant_isolation` statements of `0053`/`0054` (exact
  `(file, policy, table)` entries in `POLICY_TO_EXEMPT`), and an exemption is honoured only while the
  replayed schema's effective policy for that table and name is a later definition that names
  `evidgate_app` — which is exactly what `0055` supplies, so deleting its `create policy` statements
  turns the exemptions back into findings. The second list, `OPERATOR_GATE_EXEMPT`, accepts one operator predicate the text lint cannot
  judge — keyed by the exact `(file, policy, table)` the replay leaves in place, and only while
  that statement is not an open `using (true)`: `0003`'s `rls_probe_operator_read`, for the
  reason it carries.
- Deployment: migrate before app, and the file takes an exclusive lock on three tables that live
  traffic writes, so it sets `lock_timeout = '5s'`; that timeout bounds each lock wait, not the file
  as a whole, and the runner applies the file in one transaction, so its three table locks are held
  together until it commits — apply it while Healing traffic is idle. Exit 3 means "apply again
  while the table is quiet", not a defect. Rollback restores nothing and needs to restore nothing:
  re-granting `SELECT` to `evidgate_operator` without a `TO evidgate_operator` policy still reads
  zero rows, and a file that re-created the policies without `TO` would fail the lint.

- How far `check-rls` reads a migration: a policy, grant, revoke or table statement written inside
  `do $body$ … $body$` (any tag) or an `execute '…'` string runs at migration time, so the lint replays
  it in execution order and reads an `execute` argument written as `'…'`, `E'…'` or `$tag$…$tag$`.
  SQL it cannot read from the file is a finding, not a pass: `embedded_dynamic` reports an `execute`
  of a variable and an `execute format(…)` whose template builds DDL, because the arguments could
  change the table or the role.
- Conservative by design, in both rules: a column-level `revoke` removes no table privilege and a
  `revoke grant option for` removes no `SELECT`, so both leave the operator's table `SELECT` counted;
  an operator policy's predicate is read as text, so a predicate that never compares `org_id`
  (`org_id is not null`, a role check) is a finding, while one that compares it to a value, a
  subquery or a function call counts as a gate (the lint cannot tell whether that table is
  operator-writable or that function checks a scope); and a `do` body nested past three levels is
  reported rather than recursed.

## Notes on the plan-coverage reason (`0056`)

- Why the file exists: `0049` closed the `missing[]` vocabulary at the seven codes the event
  comparison and the artifact freeze can name. The reconciler also decides completeness from the
  run's plan — a `planned_execution` with no recorded `case_attempt` never reported a status — and
  the code for it, `missing_result`, had to enter the CHECK or the revision insert would fail 23514
  and the attempt would be retried until the queue dead-lettered it.
- What changed: `evidence_revision_missing_check` is dropped and re-created with the same predicate
  and the same `CASE`/`array_position` shape, over an array literal with `missing_result` appended.
  The dimension guard and the null-element guard are copied verbatim: a CHECK's term order is not
  guaranteed, and `array_position` raises `0A000` on a multi-dimensional array, so the dimension test
  must run and short-circuit first.
- Why `not valid` + `validate`: the row scan becomes its own statement, so a failure names the phase
  that failed, and the shape matches the three-step form `0031` used for its deferred foreign keys.
  It buys **no** lock relief here: the migrator applies one transaction per script
  (`WithTransactionPerScript`, `Src/Evidgate.Migrator/MigratorRunner.cs`), so the ACCESS EXCLUSIVE the
  `drop`/`add constraint` takes is held until the file commits and the `validate constraint` scan runs
  under it — readers of `evidence_revision` wait for the scan, and `lock_timeout = '5s'` bounds only
  the wait to acquire, never the hold. Both phases sit inside that one transaction, so no reader ever
  sees an unvalidated constraint. Every row of the old list is a member of the new one, so the
  validation cannot fail on existing history.
- What did NOT change: `0049` stays byte-identical (the runner refuses a checksum mismatch) and its
  table, trigger, pointer FK, grants and index are untouched — this file only replaces one CHECK.
  Its notes above keep describing `0049`; the code list they quote is superseded here.
- Deployment: migrate before app. The literal is additive in both directions — an old application
  never writes `missing_result` and the widened CHECK accepts every code it does write — so the only
  order that fails is an application writing the new code before this file has run. The file holds an
  exclusive lock on one live table from its first `alter table` to its commit — acquisition is bounded
  by `lock_timeout = '5s'` (exit 3 means "apply again while the table is quiet", not a defect), the
  hold is not, so apply it in a quiet window rather than relying on the timeout to shorten it. Rollback needs to restore nothing, but a code rollback after rows
  name `missing_result` would refuse those rows under a narrower CHECK, which is why there is no down
  migration.

## Notes on the collection-deadline scan (`0056`)

- Why the function exists: a reconcile is enqueued only by the finalize that closes a producer slot,
  and the attempt itself compares `collection_deadline_at` with the database clock. A run whose last
  shard never finalizes therefore gets no attempt and stays `collecting` past its deadline, with no
  outbox row and no platform path that looks at it again. The missing piece is a periodic scan, and the
  platform sweeps run as `evidgate_worker_sched`, which `0031` grants no privilege on `public.run`
  (and which has no system GUC to read around row level security). `evidgate.audit_org_ids` (`0015`) is
  the same shape for the audit walk: the sweep role reaches one owner `security definer` function that
  projects only the identifiers it needs, and every write it then performs runs in the tenant's own
  scope as `evidgate_app`.
- What it exposes: `(org_id, run_id, collection_deadline_at)` for the rows of `public.run` whose
  `state` is `collecting` and whose `collection_deadline_at` is at or before `now()` — nothing else, no
  business column, no event, no plan. `running` is excluded on purpose: the reconcile the sweep queues
  refuses every state but `collecting` (it gates on the state before it reads the deadline), and only a
  finalize moves `running -> collecting`, so a queued attempt for a `running` run could never do
  anything while its key — one per run — would mark that run's deadline attempt as spent. The run this
  sweep exists for is the one whose shard never finalizes, and that run is stuck in `collecting`.
  Four properties are part of the contract:
  the deadline is compared by the database (a caller cannot widen the window, and the clock is the one
  the triggered attempt will use); the scan is keyset-paged on `(org_id, id)`, ordered on the primary
  key's tenant prefix then the id, so a page boundary can neither repeat nor skip a run whatever else
  commits meanwhile; the row limit is clamped inside the body (`1..1000`, default `100`), so a caller
  cannot ask for the whole table; and the body is `stable`, one snapshot per call. `set search_path = ''`
  and a schema-qualified relation keep the definer body immune to a caller's `search_path`.
- The index: `run_collecting_idx` on `public.run (org_id, id)` `where state = 'collecting'`. The scan seeks
  and orders on exactly that pair inside one state, while the table keeps every finished run forever, so a
  partial index keeps the seek proportional to the runs a sweep can name instead of the run history. It is
  reached through the definer function alone, so it needs no grant; `create index` runs inside the migrator's
  per-script transaction, which is why it is not `concurrently` — the SHARE lock it takes lets readers
  continue and queues inserts and updates behind it, under the file's `lock_timeout`.
- Grants: `security definer` on `evidgate_owner` (the posture of every sweep helper — the owner is the
  only role with a policy-free view, and `scripts/check-rls.py` audits the body), `public` revoked,
  `EXECUTE` granted to `evidgate_worker_sched` alone. `evidgate_app` and `evidgate_operator` cannot call
  it; their path to a run is the tenant routes. The function is read-only, so it is exempt from the
  role-scoped-policy and operator-gate rules the same lint applies to a table: it takes no lock on
  `public.run` beyond the reader's and writes nothing.
- How `check-rls` reads it: the new entry in `DEFINER_AUDIT_EXEMPT` names this function and the reason
  its unqualified body reference is not a finding (the relation is `public.`-qualified and the function
  names no `join`/`update` target at all); `tests/Isolation.Tests/Rls/ResolverTests.cs` pins the same
  decision in its owner-definer allow-list, so deleting either the function or its grant turns the
  other list into a finding rather than a silent pass.
- Deployment: migrate before app. Nothing existing calls the function — it is inert until the Worker
  that scans is rolled out — and the statements are catalog writes on a function no other session has
  executed yet, so `lock_timeout = '5s'` is the only contended wait. `create or replace` + repeatable
  `revoke`/`grant`, so a re-run is a no-op in substance, and there is no down migration: dropping the
  function would make the sweep fail loudly instead of silently skipping every run.

## Notes on the parse-state indexes (`0057`)

- Why it exists: 06.5.1's freeze refuses to count a ready raw report until its parse verdict exists, and
  reads two rows for that: an `artifact_parse` task row that has not reached `done`, and the completion's
  undispatched `artifact_parse` outbox row — the window between the upload commit and the dispatcher's
  task insert, where no task row exists yet. The queue keeps a finished row only until `task.sweep`
  deletes it (`state in ('done','dead_letter')` past the retention setting), so a dead-lettered parse
  older than that leaves its report looking unparsed again, and that window is the freeze's documented
  bound. The freeze evaluates both anti-joins once per ready artifact, up to
  `FreezeRequest.MaxReadyArtifacts` (50 000) in one call, so each lookup must be an index probe and never
  a scan of a mechanism table: 00 §5.7 P4 forbids a per-row path that reads a table the caller does not
  need.
- The shape: two partial expression indexes over `(org_id, (payload ->> 'artifact_id'))`, on
  `public.task` `where task_type = 'artifact_parse' and state <> 'done'` and on `public.outbox`
  `where task_type = 'artifact_parse' and dispatched_at is null`. `org_id` is a column because both
  tables carry no policy, so the anti-join's org equality has to come from the index too; the expression
  is `jsonb_object_field_text`, which is immutable and therefore legal, and the value is the lowercase
  `D` GUID `CompleteArtifactUpload` serialises, the same form `artifact.id::text` gives. The partial
  predicates spell the freeze's terms verbatim, and a statement that spells a filter differently does
  not match the predicate and simply keeps its own plan instead of reading a subset the predicate never
  promised.
- Locks and deployment: the file runs in the migrator's per-script transaction, so each index is built
  with a plain `create index` (SHARE lock: readers continue, writes queue) and the file's
  `set local lock_timeout = '5s'` bounds the wait for that lock — a busy table fails the migration for a
  retry instead of stalling every task write. `if not exists` makes a re-run a no-op.
- Grants and policy: none change. An index is not granted on, `evidgate_app` already holds `select` on
  both tables, and neither deliberately carries a row-level-security policy (`0008`/`0006`: the claim
  and the dispatcher scan every org by design), so tenant separation is the explicit
  `t.org_id = a.org_id` / `o.org_id = a.org_id` predicate every statement on them must keep — the
  freeze's anti-joins included. The index repeats `org_id` as a column, it never replaces the predicate,
  and a reader must not treat the missing policy as covered by an index. No `security definer` function is involved, so neither
  `scripts/check-rls.py`'s `DEFINER_AUDIT_EXEMPT` nor the isolation suite's owner-definer allow-list
  gains an entry.
- No down migration: dropping the index would not roll back behaviour, it would silently return the
  freeze to a sequential scan of the queue.

## Notes on the overdue `queued`/`running` runs (`0058`)

- Why the predicate widens: `0056`'s scan named `collecting` runs only, on the reasoning that the reconcile it
  queues refused every other state, so an attempt on one would only spend the run's single deadline key. The
  stall that leaves is the state before collecting: a reconcile is enqueued only by the finalize that closes a
  producer slot, so a run whose producers never started (`queued`) or died before their first finalize
  (`running`) is never named by anything and stays open past `collection_deadline_at` forever. The deadline is
  the run's promise, so past it the run is closed from the evidence that arrived — never as a pass, because
  missing evidence never reads as one.
- What it exposes: the same `(org_id, run_id, collection_deadline_at)` projection with the same signature,
  return shape, `security definer` posture, `search_path = ''`, database-clock comparison, keyset cursor on
  `(org_id, id)` and clamped limit (`1..1000`, default `100`) as `0056`; the predicate becomes
  `state in ('queued', 'running', 'collecting')`. Those three are exactly the states the run machine declares
  an edge to `incomplete` from. `created` stays out: no shard plan is committed for it and
  `created -> incomplete` is not a declared edge, so a deadline verdict there would invent a transition; the
  terminal states have no outgoing edge, so their verdict stands and the scan never names them.
- The index: `run_overdue_idx` on `public.run (org_id, id)` with the scan's own predicate. `run_collecting_idx`
  (`0056`) is dropped in the same file because the wider predicate covers it — a statement written as
  `state = 'collecting'` implies the partial predicate, so the planner reads the new index — and keeping both
  would write a second entry per collecting run on every insert and state update, for no reader. The new
  index is created before the old one is dropped, so no statement runs without a usable one.
- Grants: unchanged, and repeated in the file so it is self-contained — `security definer` on
  `evidgate_owner`, `public` revoked, `EXECUTE` for `evidgate_worker_sched` alone.
- How `check-rls` reads it: `DEFINER_AUDIT_EXEMPT` carries the new create-site cite
  (`0058_reconcile_overdue_running.sql:65`) and `tests/Isolation.Tests/Rls/ResolverTests.cs` pins the same
  decision in its owner-definer allow-list, so a moved or deleted definition turns one of the two into a
  finding instead of a silent pass.
- Deployment: migrate before app, in the same deploy. The widened scan is what lets the sweep queue a
  `queued` or `running` run at all, and only the reconciler shipped with this file decides one; an older
  application answers `NotCollectable` for such an attempt and spends that run's deadline key, which would
  leave the run open exactly as before. The rest is additive for an older application: it never sees a row
  the old scan did not also return.
- Locks: `create index` takes a SHARE lock on `public.run` (readers continue, writes queue) and `drop index`
  an ACCESS EXCLUSIVE lock for the catalog change alone — no rewrite — both under the file's
  `set local lock_timeout = '5s'`; a busy table fails the migration for a retry instead of stalling run
  writes. `create or replace` and the repeatable `revoke`/`grant` are catalog writes on the function.
- No down migration: restoring the collecting-only predicate would not roll back a behaviour, it would
  return the platform to a run past its deadline that nothing ever decides.

## Notes on the stored evaluation (`0059`)

- Why it exists: the evaluator's verdict has to survive the process that computed it, and the reconcile that
  freezes evidence can be redelivered — so the same question arrives twice and must leave one row. The table
  is the arbiter: the unique key `evaluation_result_key
(org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode)` makes a second identical
  `INSERT` fail 23505, and the handler reads the winner back instead of writing a second verdict.
- Why two keys: `shadow` is a second opinion about the same evidence, not a later state of the `live` row, so
  the pair is legal while two `live` rows for one evidence revision are not. `evaluation_live_key` is a
  partial unique index on `(org_id, run_id, evidence_revision) where mode = 'live'`; the wider key carries
  `mode` as its last column. Neither is a candidate for an `UPDATE`: a rerun writes a new row.
- Why every value is a CHECK: the columns store the evaluator's own wire tokens (`complete`/`incomplete`,
  `all_passed`/`has_failures`/`no_results`, `PASS`/`FAIL`/`INCONCLUSIVE`, `live`/`shadow`), the closed
  `missing[]` and `reasons[]` code sets and the `sha256:<64 hex>` shape of both content addresses, so a
  stored row reads back into the contract without a mapping layer. A new token is a new migration. The
  array checks are a `CASE` on `array_ndims` first, because `array_position` raises on a multi-dimensional
  array and CHECK term order is not guaranteed.
- Why the keys are composite: `(org_id, run_id)` and `(org_id, policy_revision_id)` reference
  `run`/`policy_revision` on their `(org_id, id)` keys, so a row cannot name another tenant's run or policy
  revision even if a bug let the policy pass (D3). No `ON DELETE`: a run with a stored verdict is erased
  through the owner's one erasure path, never by a cascade that would delete the evidence of a decision.
- Immutability: `evidgate_app` holds `SELECT, INSERT` only (the revoke precedes the grant because 0001's
  default privileges would otherwise leave full DML in place), so an `UPDATE`/`DELETE` is refused 42501 by the
  ACL; the `evaluation_immutable` trigger raises 23514 for every role, which is the backstop for the owner and
  for any future grant. `evaluation_run_idx (org_id, run_id, created_at desc)` serves the newest-first read of
  a run's evaluations, which the partial unique index cannot.
- Row security: one `for all to evidgate_app` tenant policy plus the owner backfill policy, like 0025/0049.
  `evaluation` carries `org_id` and is therefore not an entry in `contracts/rls-exceptions.txt`; no operator
  policy, and the operator keeps `SELECT` only (D1); `evidgate_worker_sched` holds nothing.
- Deployment and locks: additive for every existing reader, so migrate before the worker that writes rows.
  Everything is a catalog write or a `create index` under the file's `set local lock_timeout = '5s'`; a busy
  table fails the migration for a retry instead of stalling writes, and `if not exists` / `create or replace`
  / `drop … if exists` make a re-run a no-op. No down migration: dropping the table would delete every stored
  verdict, which no rollback restores.

## Notes on the CI token purge ACL (`0089`)

- `0019` rewrote `evidgate.purge_expired_ci_token_issue` with `create or replace` and no `revoke`/`grant`, so the
  worker-only EXECUTE it runs under is still the one `0017` set. `0089` restates it (PUBLIC and every runtime role
  revoked, then EXECUTE to `evidgate_worker_sched`) and changes no function body. It is a no-op on a database that
  already carries the `0017` grant. The other function `0019` touches, `ingest_token_keep_version_on_touch`,
  restates its own ACL, so nothing else needed restating.

## Notes on the session revoke helpers (`0090`)

- `0085` `session_revoke_all_admin` had copied the `membership:write` join and the audit-chain append of `0051`
  `session_revoke_one`. `0090` moves both into two `evidgate` helpers, `session_admin_may_revoke(actor, org,
target)` and `session_revoke_audit_append(org, actor, object_type, object_id, metadata, request_id, trace_id)`,
  and re-creates both definers (same signatures, `security definer`, pinned `search_path`, `lock_timeout`) so they
  call them. Results, error codes, audit payloads and entry hashes are unchanged.
- The helpers are invoker-rights functions like `session_user_lock` (`0065`): EXECUTE is revoked from PUBLIC,
  `evidgate_app`, `evidgate_operator` and `evidgate_worker_sched`, so only the owner-run definers reach them (a
  tenant-facing role must never be able to append a chain row for any org). `DefinerExecuteScopeTests` pins the
  exact grantee list.
- Behaviour fix: `session_revoke_all_admin` now proves the caller and the target again after it holds the
  target's user lock, so a removal that commits during the lock wait refuses the call (`not_found`, nothing
  revoked or recorded). This narrows the window to the statements after the check; it does not make the check
  and the update one atomic step.
- Deploy: any order; the helpers and the definers change in one transaction. Rollback: re-state the `0051` and
  `0085` bodies with `create or replace`, then drop the two helpers.

## Notes on the revoke-all hierarchy (`0091`)

- `session_revoke_all_admin` let any `membership:write` holder end every session of any active member, a
  `tenant_admin` included. `0091` adds `session_admin_may_revoke_all` (the old check, the caller is not the target,
  and `session_admin_rank(org, actor) >= session_admin_rank(org, target)`), and the definer asks it before and
  after the target's user lock. Every refusal stays the one `not_found`.
- Rank has one source, `evidgate.session_admin_rank`: 2 = built-in `tenant_admin` in the acting org, 1 = `membership:write`
  through another role, 0 = otherwise. Another org's grants never count. The `membership:write` join of `0090` now
  lives in `session_admin_holds_write`; `session_admin_may_revoke` (used by the single revoke) answers as before.
- The audit row stays in the acting org's chain only. `session_admin_may_revoke`, `session_admin_may_revoke_all` and
  `session_revoke_audit_append` stay owner-only (`DefinerExecuteScopeTests`); `0093` later grants the app role
  EXECUTE on `session_admin_rank` and `session_admin_holds_write`, the two the member commands ask (see its note
  below).
- Deploy: any order, one transaction. Rollback: re-state the `0090` bodies of `session_revoke_all_admin` and
  `session_admin_may_revoke`, then drop the three new helpers.
- Residual, accepted pending an owner decision: sessions are per user, so an administrator of any org the target
  belongs to at a lower rank can still end the target's sessions in every org. `RemoveMember`, `UpdateMembership`
  and `RevokeRole` are no longer in this residual: they compare actor and target with the same
  `session_admin_rank`, which `0093` makes reachable from the app role. `session_revoke_one` stays outside the
  hierarchy on purpose: it ends ONE session of an addressed target under `session_admin_may_revoke`, and the
  hierarchy exists for the revoke-all path, whose blast radius is every session the person holds.

## Notes on the issued-period mutation guard (`0092`)

- `0087` refused INSERTs into an issued billing period; `0092` refuses UPDATE and DELETE too, for every role, the owner included, with plain row triggers (`usage_ledger_entry_issued_mutation`, `billing_statement_issued_mutation`), not grants or policies. A statement row exists only once its period is issued, so every statement UPDATE/DELETE is refused; a ledger row is refused when its (org_id, period) has a statement, or when an UPDATE moves it into such a period. SQLSTATE `23514`, constraint label = trigger name.
- No code path mutates either table (the app role holds no UPDATE/DELETE grant; issue and re-issue only insert and read). Existing rows in issued periods are not backfilled. Accepted residuals: the table owner or a superuser can still `DROP`/`DISABLE TRIGGER`, `TRUNCATE` (row triggers do not fire) or set `session_replication_role = replica`. An owner repair of an issued period is one transaction: `alter table ... disable trigger`, repair, `enable trigger`, commit (SHARE ROW EXCLUSIVE lock). A bulk owner statement takes one shared advisory lock per distinct (org_id, period): batch by org.
- The ledger guard takes the `0087` shared advisory lock on the old and the new period key and refuses non-READ COMMITTED writers, like the insert guard.
- Deploy: any order, one transaction. Rollback: drop the two triggers, then the two `evidgate.*_issued_mutation_guard` functions.

## Notes on the member rank grant (`0093`)

- The member commands (`RemoveMember`, `UpdateMembership`, `RevokeRole`) refuse an actor whose admin rank in the
  org is below the target's, and the rank has one source, `0091`'s `evidgate.session_admin_rank(org, user)` (2 =
  the built-in `tenant_admin` of that org, 1 = `membership:write` through another role, 0 = otherwise), which
  delegates its write probe to `evidgate.session_admin_holds_write`. Both are invoker-rights, so the command runs
  them under its own privileges and `evidgate_app` needs EXECUTE on both; `0093` grants exactly that, on those two
  functions. No function body, table, column, row or policy changes. Read the signatures before calling either
  directly: `session_admin_rank(org, user)` takes the org first, `session_admin_holds_write(actor, org)` the actor.
- Invoker rights is also the isolation: the answer is computed under the request's `evidgate.org_id`, so the
  policies of `membership`, `membership_role`, `role` and `role_permission` apply as usual. Another org's
  administrator ranks 0 here even when that org's id is passed explicitly — the rows are hidden, not filtered after
  the fact. `MemberRankGrantTests` pins own-org owner 2, another org's owner 0 under this org, another org's owner 0
  when its own id is named, and the no-scope answer 0.
- The app role is the only grantee added: PUBLIC, `evidgate_operator` and `evidgate_worker_sched` are restated as
  EXECUTE-less in the same file, and `DefinerExecuteScopeTests` pins each grantee list to the owner and
  `evidgate_app`. `session_admin_may_revoke`, `session_admin_may_revoke_all` and `session_revoke_audit_append` stay
  owner-only: they are reached through the owner-run definers.
- Deploy the migration first, then the application: the commands call both functions, so an application
  deployed ahead of `0093` fails every member mutation on the missing EXECUTE. Rollback in the other order — roll
  the application back before `revoke execute` on both functions from `evidgate_app` (the `0091` ACL), because a
  host that still calls them answers 500. The file only widens an ACL, so it is a no-op on a database that already
  carries it.
- Residual, accepted: the commands compare the rank twice, before and after the last-admin guard, which narrows the
  window in which a concurrent demotion of the actor could land between the check and the write. It is not atomic:
  the guard's `for update` covers the org's active admin rows, while the insert or delete that changes a role grant
  does not lock the membership row the comparison reads.
- Residual, accepted: rank counts active memberships only, so a removed or pending target ranks 0 whatever grants
  its row still carries.

## Notes on the late-usage re-date (`0094`)

- `0087` refused a ledger INSERT into an issued billing period, so a source that metered after month-end dead-lettered instead of billing. Owner decision D-S314-M1 (B): the insert is re-dated into the next open period, keeps the period it was metered for in the new `original_period` column (null when nothing moved), and is audited as `usage.entry_redated` in the same transaction. No row is dropped, none enters an issued period, and `billing_key` still arbitrates dedupe, so a batch whose keys are all on file answers `Duplicates` and writes nothing — except at the exhausted end of the calendar, where the walk's refusal comes first (next bullet).
- The decision stays inside `evidgate.usage_ledger_entry_period_issued_guard()` (`0087`, replaced by `0087`'s body plus the walk). The trigger holds the requested period's shared advisory lock as before, then takes that lock for each candidate month before probing `billing_statement`; an issue takes the matching exclusive lock, so a month the walk found open cannot be issued before this transaction commits, and statements are never deleted (`0092`), so an issued month cannot reopen. The candidate month is incremented as text (`substr` and `lpad`, month 12 → next year's `01`), never through `to_date`/`to_char`: `to_date('0000-01', 'YYYY-MM-DD')` is 1 BC, so a round trip through `date` would rewrite year 0000 as 0001. A walk that finds every remaining month from the requested one through `9999-12` already issued refuses with SQLSTATE `23514` and constraint `usage_ledger_entry_no_open_period`; an INSERT that supplies `original_period` itself is refused before the probe with `usage_ledger_entry_original_period_supplied`, for an open month as well as an issued one, and the message never repeats the supplied value, because the marker is the trigger's decision alone. The trigger runs before `ON CONFLICT … DO NOTHING`, so a replay of a key already on file answers `Duplicates` only while the walk can name a month; a replay whose requested month's chain is exhausted at `9999-12` meets the same named refusal, and nothing is written either way. Accepted cost: one shared lock and one probe per issued month per row, bounded by the calendar rather than by a step count, so a long issued chain makes a late write slower by design.
- `original_period` carries the same `YYYY-MM` shape check as `period` and a forward-only check (`original_period < period`); both compare as text, which is chronological in that form. Both checks are added `not valid` and validated in the same file: the add writes the catalogue only, so a data failure surfaces in the named `validate` rather than inside the add, and an operator applying the pair to a large live table outside the runner can add and commit, then run `validate constraint` alone under SHARE UPDATE EXCLUSIVE. Inside the runner the lock is the same either way — one file is one transaction (`WithTransactionPerScript`, this README), so the column add's and the two adds' ACCESS EXCLUSIVE lock on the ledger is held until the file commits and both scans run under it, blocking reads and writes for the file's duration; each scan reads the ledger once and rejects nothing, because every row's `original_period` is null when it runs. The column is nullable with no backfill, so every existing row keeps null and existing statement digests are unchanged.
- Deploy: any order, one transaction. `lock_timeout` bounds only the wait for each lock, not a statement's duration; the budget section above covers the rest of the runner's behaviour. Rollback: drop the two constraints and the column, then re-state the `0087` body of the guard function.

## Notes on the artifact state guard (`0095`)

- `0095` adds `evidgate.artifact_guard_transition()` and the `artifact_guard_transition_trg` BEFORE UPDATE OF `state` trigger on `artifact`: the 00 §8.4 state machine as an allow-list (`pending_upload` → `uploaded|ready|rejected`, `uploaded` → `ready|rejected`, `ready` → `rejected|pending_delete`, `pending_delete` → `deleted`; same-state writes pass). Anything else raises SQLSTATE `23514` `artifact_illegal_transition`, for every role including the owner, and the row (`version`, `updated_at` too) stays untouched because the trigger sorts before `artifact_set_updated_at_and_version`. Accepted residuals: the table owner can still `DROP`/`DISABLE` the trigger, and a superuser can set `session_replication_role = replica`. It guards UPDATE transitions only, so an `INSERT` that names a non-initial state is outside it.
- `rejected` and `deleted` are terminal. `ready → rejected` is the parse verdict of 06.3.6 (recorded in 00 §8.4 and §15). The C# aggregate (`ArtifactState.IsAllowedMove`) carries the same list; `CompleteArtifactUpload` maps the trigger's refusal to `409 artifact_state_conflict`.
- Deploy: any order, one transaction; the trigger only refuses writes the aggregate never issues, so an older app version is unaffected. Rollback: drop the trigger and the function; no data is transformed.
