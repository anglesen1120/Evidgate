-- 0029_ingestion_producer_slot.sql
--
-- Owner: 04.1.2-T4.1. `producer_slot`: one row per manifest shard of a run, written by 05's
-- run-create command through `IProducerRegistry.RegisterSlotsAsync` inside the create transaction
-- (feature plan 04.1.2 §4, area plan 04 §4, 00 §9.4, rulings T3/T9/T54/T68/T76/T85/T88). The row
-- is the run's producer registry: the first accepted write of any kind (an event batch or
-- 06.1.2's `upload-url`, T85) binds it to the caller's token identity (`bound_token_jti`, plus
-- `bound_ci_run_ref` from the GitHub `run_id`/`run_attempt` claims when present, T88); a write or
-- `finalize` under another identity is 403 `producer_slot_taken`, an unknown producer 422
-- `unknown_producer` (T9). `producer_id` is deterministic and declared by the manifest:
-- `shard-<index>`, 1-based, `shard-1` when unsharded (T54).
--
-- Owner decision 2026-10-10 (chat): the `run` table does not exist yet - 05.2.1-T1 creates it - so
-- the plan's `foreign key (org_id, run_id) references run (org_id, id)` is deferred to that task.
-- `run_id` stays `uuid not null` with no foreign key here, exactly like 0028's `artifact.run_id`.
-- `event_slot_fk` is likewise not created here: the `event` table does not exist at this migration's
-- number, so 04.1.1-T1's `event` migration adds
-- `event_slot_fk (org_id, run_id, producer_id) -> producer_slot (org_id, run_id, producer_id)`.
--
-- T68: `declared_last_sequence`/`declared_count` are NOT in this migration. Those two columns are
-- owned by 04.1.2-T5, which adds them in its own migration together with `CloseSlotAsync`. `closed_at`
-- is created here because the plan §4 DDL declares it and the §6 state machine (`open -> closed`) is
-- the table's contract; nothing in T4 writes it.
--
-- Column grants (why not the plain `select, insert, update` of 0024): the app role may move only the
-- two binding columns - `bound_token_jti` and `bound_ci_run_ref`, which `BindOrCheckAsync` sets on
-- the first accepted write (T9) - never the identity, the shard index, the producer id, the audit
-- columns or `last_sequence`/`closed_at`. `0001`'s default privileges grant `evidgate_app` full DML
-- on every table `evidgate_owner` creates in `public`, so the table-level `revoke update` below is
-- required before the column-level `grant update` (0027/0028 precedent). `last_sequence` is written
-- by the ingest handler (04.1.1-T1/T5) and `closed_at` by `CloseSlotAsync` (04.1.2-T5), so neither
-- carries an UPDATE grant yet; each task adds its own column grant with its own migration. No DELETE:
-- a slot is never removed, it is closed.
--
-- Row security: the tenant policy is `for all to evidgate_app`, exactly like 0005/0020-0028. A policy
-- with no `to` clause applies to PUBLIC, which would let `evidgate_operator` (it holds the SELECT
-- grant below) set `evidgate.org_id` and read any org (security finding from PR #51, T76). No
-- `TO evidgate_operator` policy is created here (TL T76, D1): the grant-gated operator policy is
-- 02.8.1's `evidgate.operator_grant_ok` form, and until it lands the operator's SELECT grant reads
-- zero rows.
--
-- Bound CHECKs (the 0006-0028 `char_length` style): `shard_index` is 1-based and capped at the
-- manifest's 256 shards (contracts/manifest/manifest.v1.json `shard_plan.shards.maxItems`), the two
-- binding columns carry their documented bounds (`bound_token_jti` mirrors 0017's `jti` 1..256;
-- `bound_ci_run_ref` holds the GitHub `(repository_id, run_id, run_attempt)` triple, T88), and
-- `last_sequence`/`version` are non-negative. `producer_id` is bounded by length and charset rather
-- than pinned to `^shard-[1-9][0-9]*$`: T54 derives it server-side as `shard-<index>`, but the wire
-- contract (contracts/schemas/event-envelope.v1.json) allows any string up to 128 characters and the
-- checked-in examples use `pw-shard-0`, so a strict shape would be stricter than the wire. The
-- charset bound is the manifest `shard_id` grammar (`^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$`) widened to
-- the wire's 128-character limit.
--
-- Idempotent like 0025/0027/0028: `create table if not exists` / `drop ... if exists` so a re-run is a no-op.

create table if not exists public.producer_slot (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  shard_index integer not null,
  producer_id text not null,
  bound_token_jti text,
  bound_ci_run_ref text,
  last_sequence bigint not null default 0,
  closed_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint producer_slot_pkey primary key (id),
  constraint producer_slot_org_id_key unique (org_id, id),
  constraint producer_slot_run_producer_key unique (org_id, run_id, producer_id),
  constraint producer_slot_run_shard_key unique (org_id, run_id, shard_index),
  constraint producer_slot_shard_index_check check (shard_index between 1 and 256),
  constraint producer_slot_producer_id_check check (
    char_length(producer_id) between 1 and 128
    and producer_id ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$'),
  constraint producer_slot_bound_token_jti_check check (
    bound_token_jti is null or char_length(bound_token_jti) between 1 and 256),
  constraint producer_slot_bound_ci_run_ref_check check (
    bound_ci_run_ref is null or char_length(bound_ci_run_ref) between 1 and 256),
  constraint producer_slot_last_sequence_check check (last_sequence >= 0),
  constraint producer_slot_version_check check (version >= 1)
);

-- The per-run slot list (P4): 05's reconciler and the finalize path read every slot of one run.
create index if not exists producer_slot_run on public.producer_slot (org_id, run_id);

-- The shared concurrency trigger of 0004 (M1W3-01): the database owns `updated_at` and the `version`
-- increment, so a second tracked update in the same scope matches the incremented token.
drop trigger if exists producer_slot_set_updated_at_and_version on public.producer_slot;
create trigger producer_slot_set_updated_at_and_version
  before update on public.producer_slot
  for each row execute function public.set_updated_at_and_version();

alter table public.producer_slot enable row level security;
alter table public.producer_slot force row level security;

drop policy if exists producer_slot_tenant on public.producer_slot;
create policy producer_slot_tenant on public.producer_slot
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1): the base migration does not grant
-- the operator standing read access. The operator reaches this table only through 02.8.1's
-- grant-gated `evidgate.operator_grant_ok` policy; until that lands the SELECT grant below reads
-- zero rows.

-- The migration role needs its own policy: `force row level security` applies to the table owner too,
-- so an expand/contract backfill or an owner SECURITY DEFINER sweep would otherwise update zero rows
-- without failing (0004/0005 precedent). Explicit and permissive on purpose - the alternative would
-- be BYPASSRLS, which DECISIONS F5 rejects.
drop policy if exists producer_slot_owner_backfill on public.producer_slot;
create policy producer_slot_owner_backfill on public.producer_slot
  for all to evidgate_owner using (true) with check (true);

revoke update on public.producer_slot from evidgate_app;
grant select, insert on public.producer_slot to evidgate_app;
grant update (bound_token_jti, bound_ci_run_ref) on public.producer_slot to evidgate_app;
revoke delete on public.producer_slot from evidgate_app;
grant select on public.producer_slot to evidgate_operator;
revoke all on public.producer_slot from evidgate_worker_sched;
