-- 0034_ingestion_event_and_batch.sql
--
-- Owner: 04.1.1-T1.1. `event` and `event_batch`: the append-only evidence of one accepted ingest
-- batch (feature plan 04.1.1 §4, area plan 04 §4, 00 §5.4/§7/§9.3/§9.4, rulings T3/T50/T81/T90/T91).
-- `POST /v1/runs/{r}/events` commits one batch and its per-event acks in ONE tenant transaction, so
-- an `event` row and its `event_batch` row commit or not at all. `event` is the producer's envelope
-- (one row per `event_id`, `payload_hash` over the RFC 8785 canonical event); `event_batch` is the
-- batch receipt the response's `batch_id` names and the usage outbox row meters.
--
-- Append-only, and that is the whole immutability mechanism: `revoke update, delete` below, no
-- trigger (00 §5.5 C6, task file §4). A correction is a NEW `event_id` / `attempt_no`, never an edit.
-- Consequently neither table carries `updated_at`/`version` and neither carries the shared
-- `set_updated_at_and_version` trigger: `event` keeps `received_at`, `event_batch` keeps `created_at`
-- only — the same posture as `planned_execution` (0031) and `case_attempt` (0032), and both are listed
-- in the Persistence schema-convention scan's allow-list for `AuditColumns | VersionTrigger`.
--
-- Two composite tenant keys, both plain `add constraint` (no 0031 three-step form):
--   * `event_slot_fk (org_id, run_id, producer_id) -> producer_slot (org_id, run_id, producer_id)`
--   * `event_batch_run_fk (org_id, run_id) -> run (org_id, id)`
-- `run` exists since 0031 and `producer_slot` since 0029, so neither key is deferred, and 0031's
-- `not valid` -> orphan-check -> `validate` sequence is not needed: both tables here are created by
-- this file in the same transaction (`WithTransactionPerScript`), so they are provably empty at every
-- `add constraint` and the three-step form would be a ceremony whose orphan count can only be zero.
-- `event_slot_fk` is the 0029 hand-over: `producer_slot` is the run's producer registry (04.1.2-T4),
-- so an event whose `producer_id` was never registered for that run is refused 23503 here — the
-- database half of the T9 `unknown_producer` refusal.
--
-- [DECISION] The plan's D7 also names a direct `event_run_fk (org_id, run_id) -> run (org_id, id)`;
-- it is deliberately NOT created, following the task file instead (§3 and §4: "The composite FK
-- `(org_id, run_id) -> run` stays deferred to 05.2.1"). The reason is mechanical, not scope:
-- `event_slot_fk` already spans `run_id`, so with both keys the composite-FK isolation suite
-- (`tests/Isolation.Tests/CompositeFkTests.Insert_ForeignOrgId_Rejected`) cannot attribute a
-- cross-org corruption — corrupting either key alone yields the SAME row (org B, org A's run,
-- `shard-1`), because the other key rewrites `run_id` from its own org's parent, so the suite would
-- report one fixed constraint name while it asserts each key in turn. The integrity the run key
-- would add is already guaranteed transitively: `event_slot_fk -> producer_slot`, and 0031 validates
-- `producer_slot_run_fk (org_id, run_id) -> run (org_id, id)`, so no `event` row can name a run that
-- does not exist or belongs to another org. 05.2.1 owns the deferral and can still add the shorter
-- reference; `event_batch` has no slot key, so its `event_batch_run_fk` is created here.
--
-- Bound CHECKs (the 0006-0032 `char_length`/regex style), each mirroring the source it copies rather
-- than inventing a stricter shape:
--   * `producer_id` — the envelope `producer_id` (`contracts/schemas/event-envelope.v1.json`,
--     `maxLength: 128`, no `minLength`) widened from the manifest `shard_id` grammar, i.e. exactly
--     0029's `producer_slot_producer_id_check` and `ProducerSlotLimits.ProducerIdPattern`.
--   * `execution_key` — the 00 §7 `sha256(case_id ‖ variant_hash ‖ target_env)` hex, lowercase, the
--     envelope pattern `^[0-9a-f]{64}$` (0031/0032 write the same bound).
--   * `payload_hash` — the 00 §7 RFC 8785 content address, `sha256:<64 hex>` (03.2.1's shape).
--   * `type` — the envelope `type` (`maxLength: 64`) AND its dotted lower_snake pattern; the closed
--     five-value vocabulary (`test.started|test.step|test.finished|run.log|producer.heartbeat`) is
--     enforced by the handler's validation, not here (D1), exactly as 0029 widened the manifest
--     `shard_id` grammar to the wire's own limit instead of pinning a stricter server shape.
--   * `raw_status` — `0..64`, the 0032 decision verbatim: the envelope declares `status` with
--     `maxLength: 64` and NO `minLength`, so an empty producer status is a value the producer may
--     legally send and must be storable; `normalized_status` enumerates the full 00 §8.3 vocabulary
--     (nullable — 04.1.1-T1 leaves it NULL, 05.3.3 owns normalisation, D8).
--   * `sequence >= 0`, `attempt_no >= 1`, `epoch`/`duration_ms` non-negative when set, `payload` a
--     jsonb object (0031's `components` bound), and `event_batch`'s counts/bytes non-negative with a
--     1..128 `request_id`.
--
-- Column grant on `producer_slot`: `grant update (last_sequence) on public.producer_slot to
-- evidgate_app` below. 0029 granted the app role `SELECT, INSERT` plus column UPDATE on
-- `bound_token_jti`/`bound_ci_run_ref` only and explicitly left `last_sequence` to "the ingest handler
-- (04.1.1-T1/T5)". The handler's `UPDATE producer_slot SET last_sequence = ...` needs exactly this one
-- column, so this migration adds it (0029's own header names this task as the owner). No grant on
-- `version`/`updated_at` is needed: PostgreSQL checks UPDATE privilege on the columns the statement
-- NAMES, and a BEFORE UPDATE trigger that assigns `new.updated_at`/`new.version` is not such a column
-- — 0029 already relies on precisely that, since its trigger is in place while only its two binding
-- columns are granted. `closed_at` stays ungranted (04.1.2-T5's `CloseSlotAsync` adds its own).
--
-- Column grant on `run`: `grant update (updated_at) on public.run to evidgate_app` below, so the T91
-- run read can take the row lock (`FOR SHARE`) the ingest transaction needs; the rationale, the
-- narrowest-column choice and the [DECISION] marker are on Part 3 at the bottom of this file.
--
-- Row security: both tenant policies are `for all to evidgate_app`, exactly like 0005/0020-0033. A
-- policy with no `to` clause applies to PUBLIC, which would let `evidgate_operator` (it holds the
-- SELECT grant below) set `evidgate.org_id` and read any org. No `TO evidgate_operator` policy is
-- created here (TL T76, D1): the grant-gated operator policy is 02.8.1's
-- `evidgate.operator_grant_ok` form, and until it lands the operator's SELECT grant reads zero rows.
--
-- Grants (why not the plain `select, insert, update` of 0024): both tables are insert-only in every
-- story that touches them. `0001`'s default privileges grant `evidgate_app` full DML on every table
-- `evidgate_owner` creates in `public`, so the table-level `revoke update, delete` below is required
-- before the minimal `grant select, insert` (0027/0028/0031/0032 precedent). No DELETE on either
-- table: an event is never deleted, and a batch receipt is the audit of the batch that wrote it.
--
-- Idempotent like 0025/0027-0033: `create table if not exists` / `create index if not exists` /
-- `drop ... if exists` / guarded `do` blocks, so a re-run is a no-op.

-- ---------------------------------------------------------------------------------------------------
-- Part 1: `event` (04.1.1 §4, 00 §9.4).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.event (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  event_id uuid not null,
  producer_id text not null,
  sequence bigint not null,
  job_id uuid,
  epoch bigint,
  execution_key text not null,
  attempt_no integer not null,
  type text not null,
  raw_status text,
  normalized_status text,
  duration_ms bigint,
  occurred_at timestamptz,
  received_at timestamptz not null default now(),
  payload_hash text not null,
  payload jsonb not null default '{}'::jsonb,
  artifact_ids uuid[] not null default '{}',
  after_cancel boolean not null default false,
  late boolean not null default false,
  stale boolean not null default false,
  constraint event_pkey primary key (id),
  constraint event_org_id_key unique (org_id, id),
  constraint event_run_producer_sequence_key unique (org_id, run_id, producer_id, sequence),
  constraint event_run_event_id_key unique (org_id, run_id, event_id),
  -- No direct `(org_id, run_id) -> run` key: see the [DECISION] in the header. `event_slot_fk`
  -- below spans `run_id` and `producer_slot` carries the validated `producer_slot_run_fk`, so the
  -- run of every event is guaranteed transitively (05.2.1 owns the deferral).
  constraint event_slot_fk foreign key (org_id, run_id, producer_id)
    references public.producer_slot (org_id, run_id, producer_id),
  constraint event_producer_id_check check (
    char_length(producer_id) between 1 and 128
    and producer_id ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$'),
  constraint event_sequence_check check (sequence >= 0),
  constraint event_epoch_check check (epoch is null or epoch >= 0),
  constraint event_execution_key_check check (execution_key ~ '^[0-9a-f]{64}$'),
  constraint event_attempt_no_check check (attempt_no >= 1),
  constraint event_type_check check (
    char_length(type) between 1 and 64 and type ~ '^[a-z][a-z0-9_]*([.][a-z][a-z0-9_]*)*$'),
  constraint event_raw_status_check check (raw_status is null or char_length(raw_status) between 0 and 64),
  constraint event_normalized_status_check check (normalized_status is null or normalized_status in
    ('passed', 'failed', 'skipped', 'timed_out', 'error', 'blocked')),
  constraint event_duration_ms_check check (duration_ms is null or duration_ms >= 0),
  constraint event_payload_hash_check check (payload_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint event_payload_check check (jsonb_typeof(payload) = 'object')
);

-- No standalone `(org_id, run_id)` index: the unique constraint
-- `event_run_producer_sequence_key` above is left-prefixed by `(org_id, run_id)`, so it already
-- serves the 05.3.2 reconciler's per-run read (P4) and the `(producer_id, sequence)` ordering
-- (04.1.1-T4.1); a second index on that prefix would only add write cost to every batch insert (F2).

alter table public.event enable row level security;
alter table public.event force row level security;

drop policy if exists event_tenant on public.event;
create policy event_tenant on public.event
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1): the base migration does not grant
-- the operator standing read access. The operator reaches this table only through 02.8.1's
-- grant-gated `evidgate.operator_grant_ok` policy; until that lands the SELECT grant below reads
-- zero rows.

-- The migration role needs its own policy: `force row level security` applies to the table owner too,
-- so an expand/contract backfill or an owner SECURITY DEFINER sweep would otherwise update zero rows
-- without failing (0004/0005 precedent). Explicit and permissive on purpose — the alternative would
-- be BYPASSRLS, which DECISIONS F5 rejects.
drop policy if exists event_owner_backfill on public.event;
create policy event_owner_backfill on public.event
  for all to evidgate_owner using (true) with check (true);

revoke update, delete on public.event from evidgate_app;
grant select, insert on public.event to evidgate_app;
grant select on public.event to evidgate_operator;
revoke all on public.event from evidgate_worker_sched;

-- ---------------------------------------------------------------------------------------------------
-- Part 2: `event_batch` (04.1.1 §4, C5).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.event_batch (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  producer_id text not null,
  event_count integer not null,
  bytes_in bigint not null,
  bytes_expanded bigint not null,
  accepted integer not null,
  duplicate integer not null,
  request_id text not null,
  created_at timestamptz not null default now(),
  constraint event_batch_pkey primary key (id),
  constraint event_batch_org_id_key unique (org_id, id),
  constraint event_batch_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint event_batch_producer_id_check check (
    char_length(producer_id) between 1 and 128
    and producer_id ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$'),
  constraint event_batch_event_count_check check (event_count >= 0),
  constraint event_batch_bytes_in_check check (bytes_in >= 0),
  constraint event_batch_bytes_expanded_check check (bytes_expanded >= 0),
  constraint event_batch_accepted_check check (accepted >= 0),
  constraint event_batch_duplicate_check check (duplicate >= 0),
  constraint event_batch_request_id_check check (char_length(request_id) between 1 and 128)
);

-- The batch list source (C5): one run's batches newest-first, keyset tail `id`.
create index if not exists event_batch_run on public.event_batch (org_id, run_id, created_at, id);

alter table public.event_batch enable row level security;
alter table public.event_batch force row level security;

drop policy if exists event_batch_tenant on public.event_batch;
create policy event_batch_tenant on public.event_batch
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1): see the note on `event` above.
drop policy if exists event_batch_owner_backfill on public.event_batch;
create policy event_batch_owner_backfill on public.event_batch
  for all to evidgate_owner using (true) with check (true);

revoke update, delete on public.event_batch from evidgate_app;
grant select, insert on public.event_batch to evidgate_app;
grant select on public.event_batch to evidgate_operator;
revoke all on public.event_batch from evidgate_worker_sched;

-- ---------------------------------------------------------------------------------------------------
-- Part 3: the two column grants this story needs — `producer_slot.last_sequence` (the 0029 hand-over)
-- and `run.updated_at` (the T91 row lock).
--
-- `producer_slot`: the ingest handler's `UPDATE producer_slot SET last_sequence = ...` names exactly
-- this column; the shared version trigger writes `new.updated_at`/`new.version` without needing a
-- column privilege, so no grant on those two columns is added (see the header). A GRANT of a
-- privilege already held is a no-op, so the statement needs no guard — 0029 and 0033 write their
-- column grants the same way.
--
-- `run`: the handler reads the run row `FOR SHARE` inside the ingest transaction (T91, task file §5),
-- and PostgreSQL requires UPDATE on at least one column of every table named in a locking clause,
-- `FOR SHARE` included — the rule 07.3.1's `QuarantineSupport` records for its `policy_revision`
-- lock ("a row lock needs UPDATE — a `FOR SHARE` on `policy_revision` is denied 42501"). 0031
-- revoked UPDATE on `run` and granted only SELECT/INSERT, so the lock would be refused 42501.
-- [DECISION] (2026-10-13; coordinator PANEL-CHOICE pending — A `update (state)` / B
-- `update (updated_at)` / C no lock): B, the narrowest grant that unlocks the row without granting
-- any write power over run state. `updated_at` is assigned by the shared
-- `set_updated_at_and_version` trigger on every UPDATE, so the app role cannot choose its value, and
-- `state` stays outside its grants (`grant update (state) on public.run` is 05.3.1's own, guarded
-- grant — 0031:53). Should the panel choose A or C, this one line is the only change.
-- ---------------------------------------------------------------------------------------------------

grant update (last_sequence) on public.producer_slot to evidgate_app;
grant update (updated_at) on public.run to evidgate_app;
