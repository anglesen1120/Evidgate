-- 0035_ingestion_producer_slot_declared.sql
--
-- Owner: 04.1.2-T5.1. `producer_slot`'s declared-values columns of README T68 (feature plan 04.1.2 §4
-- `alter table producer_slot add column declared_last_sequence bigint null; add column declared_count
-- int null`), plus the app-role column grant `CloseSlotAsync` needs to set them once together with
-- `closed_at`. 0029 created the table and explicitly handed all three columns to this task
-- (0029:20-23 "those two columns are owned by 04.1.2-T5, which adds them in its own migration together
-- with `CloseSlotAsync`" and 0029:30-32 "`closed_at` ... carries no UPDATE grant yet; each task adds its
-- own column grant with its own migration"):
--
--   * `declared_last_sequence bigint null` — the highest producer sequence the shard declares it sent;
--   * `declared_count int null`          — how many events the shard declares it sent.
--
-- Both stay NULL until `CloseSlotAsync` writes them once with `closed_at`; 05.3.2's reconciler compares
-- them with the observed acknowledged events per `(producer, sequence)` range (T68, 05.3.2-T2.2). They
-- are declared values, never observed ones: `last_sequence` (0034's grant) stays the observed high-water
-- mark the ingest handler moves.
--
-- Bound CHECKs (the 0006-0034 `char_length`/comparison style, each mirroring the column it guards):
-- `declared_last_sequence >= 0` mirrors `producer_slot_last_sequence_check` (0029:80) and
-- `declared_count >= 0` mirrors the same non-negative bound for a count. Neither is `not null`: an open
-- slot declares nothing. `add constraint` is not idempotent in PostgreSQL, so each key is dropped by
-- name first — 0033's form for changing a constraint on an existing table (0033:118-120).
--
-- Column grant on `producer_slot`: `grant update (closed_at, declared_last_sequence, declared_count) on
-- public.producer_slot to evidgate_app` below — exactly the three columns `CloseSlotAsync`'s single
-- conditional UPDATE names and nothing wider. `bound_token_jti`/`bound_ci_run_ref` are 0029's,
-- `last_sequence` is 0034's; `version`/`updated_at` need no grant because the shared
-- `set_updated_at_and_version` trigger assigns them without a column privilege (0034:66-70). `insert`
-- and `select` already exist (0029:117); `delete` stays revoked — a slot is closed, never removed.
--
-- Row security is untouched by this file: 0029's `enable`/`force row level security`, the
-- `producer_slot_tenant` (`for all to evidgate_app`) and `producer_slot_owner_backfill` policies and the
-- `producer_slot_set_updated_at_and_version` trigger already cover the new columns (a policy and a
-- trigger are table-scoped, not column-scoped), so no policy, trigger or `force` statement is repeated.
--
-- Idempotent like 0013/0025/0027-0034: `add column if not exists` / `drop constraint if exists` /
-- `create index`-free, so a re-run is a no-op.

alter table public.producer_slot
  add column if not exists declared_last_sequence bigint,
  add column if not exists declared_count integer;

alter table public.producer_slot drop constraint if exists producer_slot_declared_last_sequence_check;
alter table public.producer_slot
  add constraint producer_slot_declared_last_sequence_check check (declared_last_sequence is null or declared_last_sequence >= 0);

alter table public.producer_slot drop constraint if exists producer_slot_declared_count_check;
alter table public.producer_slot
  add constraint producer_slot_declared_count_check check (declared_count is null or declared_count >= 0);

grant update (closed_at, declared_last_sequence, declared_count) on public.producer_slot to evidgate_app;
