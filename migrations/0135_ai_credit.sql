-- 0135_ai_credit.sql
--
-- Owner: 08.5.5-T1.1. `ai_credit_account` (one balance row per tenant) and `ai_credit_entry` (the append-only
-- log every balance change is written through), plus the `ai_credit` ledger metric on the four usage tables.
--
-- Money is `bigint` micro-units, never `numeric` or a float. An entry's `amount_micro` is a MAGNITUDE (> 0), except an
-- `adjust`, which is signed and non-zero; the kind decides the effect, so the log and the account agree by construction:
--   grant   available +a            reserve  available -a, reserved +a
--   refund  available +a, reserved -a   consume  reserved -a (the credit leaves the account)
--   expire  available -a            adjust   available +a (a may be negative)
-- `balance_micro` is the available part, `reserved_micro` the part held for calls in flight; both stay >= 0.
--
-- Dedupe: `unique (org_id, billing_key)` is the replay key and `payload_hash` records what the first write meant, so
-- the writer can tell a replay (same hash) from a key reused for different work (different hash). A call-bound entry
-- (reserve, consume, refund) additionally holds `unique (org_id, call_id, kind)`: one of each kind per call.
--
-- Append-only: `evidgate_app` holds SELECT + INSERT on the entry table and nothing else, and the immutability trigger
-- refuses UPDATE for every role and DELETE for every role but `evidgate_owner` (the erasure path), like
-- `requirement_version` (0132). The account row is mutable by the writer only through column-scoped UPDATE.
--
-- `call_id` is a composite FK to `healing_llm_call` with no cascade, like the usage ledger's project FK: an accounting
-- row keeps its call, so a purge deletes the entries of a call before the call (owner erasure order).
--
-- The metric check of `usage_ledger_entry`, `usage_rollup`, `usage_price` and `quota_counter` gains 'ai_credit'. Each
-- widening adds the new constraint NOT VALID and validates it. The migrator runs one script in one transaction, so
-- the ACCESS EXCLUSIVE lock of the ADD is held through the validation scan and until commit (the same one-scan-under-
-- lock cost as 0094); `lock_timeout` bounds only the wait for the lock, not the scan.
--
-- Role grants: the operator and the sweep role hold nothing here; no policy admits them.

set local lock_timeout = '5s';

create table if not exists public.ai_credit_account (
  id uuid not null,
  org_id uuid not null,
  balance_micro bigint not null,
  reserved_micro bigint not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint ai_credit_account_pkey primary key (id),
  constraint ai_credit_account_org_id_key unique (org_id, id),
  constraint ai_credit_account_org_key unique (org_id),
  constraint ai_credit_account_org_fk foreign key (org_id) references public.organization (id),
  constraint ai_credit_account_balance_check check (balance_micro >= 0),
  constraint ai_credit_account_reserved_check check (reserved_micro >= 0),
  constraint ai_credit_account_version_check check (version >= 1)
);

create table if not exists public.ai_credit_entry (
  id uuid not null,
  org_id uuid not null,
  account_id uuid not null,
  kind text not null,
  amount_micro bigint not null,
  call_id uuid,
  billing_key text not null,
  payload_hash text not null,
  reason text,
  actor text not null,
  created_at timestamptz not null default clock_timestamp(),
  constraint ai_credit_entry_pkey primary key (id),
  constraint ai_credit_entry_org_id_key unique (org_id, id),
  constraint ai_credit_entry_billing_key_key unique (org_id, billing_key),
  constraint ai_credit_entry_account_fk foreign key (org_id, account_id)
    references public.ai_credit_account (org_id, id),
  constraint ai_credit_entry_call_fk foreign key (org_id, call_id)
    references public.healing_llm_call (org_id, id),
  constraint ai_credit_entry_kind_check check (kind in ('grant', 'reserve', 'consume', 'refund', 'expire', 'adjust')),
  constraint ai_credit_entry_amount_check check (
    case when kind = 'adjust' then amount_micro <> 0 else amount_micro > 0 end),
  -- A reserve, consume or refund belongs to one call; every other kind is not bound to a call.
  constraint ai_credit_entry_call_kind_check check (
    (kind in ('reserve', 'consume', 'refund')) = (call_id is not null)),
  constraint ai_credit_entry_billing_key_check check (char_length(billing_key) between 1 and 200),
  constraint ai_credit_entry_payload_hash_check check (payload_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint ai_credit_entry_reason_check check (reason is null or char_length(reason) between 1 and 500),
  constraint ai_credit_entry_actor_check check (char_length(actor) between 1 and 200)
);

-- One reserve, consume and refund per call: the guard that makes a replayed settle a no-op.
create unique index if not exists ai_credit_entry_call_kind_key
  on public.ai_credit_entry (org_id, call_id, kind) where call_id is not null;

-- The history read of one account, newest first, and the referencing side of the account key.
create index if not exists ai_credit_entry_account_idx
  on public.ai_credit_entry (org_id, account_id, created_at desc, id);

drop trigger if exists ai_credit_account_set_updated_at_and_version on public.ai_credit_account;
create trigger ai_credit_account_set_updated_at_and_version
  before update on public.ai_credit_account
  for each row execute function public.set_updated_at_and_version();

create or replace function evidgate.ai_credit_entry_immutable() returns trigger
  language plpgsql
  as $$
  begin
    if tg_op = 'UPDATE' then
      raise exception 'ai_credit_entry_immutable: a credit entry is append-only (UPDATE)'
        using errcode = '23514';
    elsif current_user <> 'evidgate_owner' then
      raise exception 'ai_credit_entry_immutable: only the owner erasure path deletes an entry (DELETE)'
        using errcode = '23514';
    end if;
    return old;
  end
  $$;

revoke all on function evidgate.ai_credit_entry_immutable() from public;
grant execute on function evidgate.ai_credit_entry_immutable() to evidgate_app;

drop trigger if exists ai_credit_entry_immutable on public.ai_credit_entry;
create trigger ai_credit_entry_immutable
  before update or delete on public.ai_credit_entry
  for each row execute function evidgate.ai_credit_entry_immutable();

alter table public.ai_credit_account enable row level security;
alter table public.ai_credit_account force row level security;

drop policy if exists ai_credit_account_tenant on public.ai_credit_account;
create policy ai_credit_account_tenant on public.ai_credit_account
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists ai_credit_account_owner_backfill on public.ai_credit_account;
create policy ai_credit_account_owner_backfill on public.ai_credit_account
  for all to evidgate_owner using (true) with check (true);

alter table public.ai_credit_entry enable row level security;
alter table public.ai_credit_entry force row level security;

drop policy if exists ai_credit_entry_tenant on public.ai_credit_entry;
create policy ai_credit_entry_tenant on public.ai_credit_entry
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists ai_credit_entry_owner_backfill on public.ai_credit_entry;
create policy ai_credit_entry_owner_backfill on public.ai_credit_entry
  for all to evidgate_owner using (true) with check (true);

-- 0001's default privileges give the app role full DML on every new table, so the revoke comes first.
revoke update, delete on public.ai_credit_account from evidgate_app;
grant select, insert on public.ai_credit_account to evidgate_app;
grant update (balance_micro, reserved_micro) on public.ai_credit_account to evidgate_app;
revoke all on public.ai_credit_account from evidgate_operator;
revoke all on public.ai_credit_account from evidgate_worker_sched;

revoke update, delete on public.ai_credit_entry from evidgate_app;
grant select, insert on public.ai_credit_entry to evidgate_app;
revoke all on public.ai_credit_entry from evidgate_operator;
revoke all on public.ai_credit_entry from evidgate_worker_sched;

-- The `ai_credit` metric on the four usage tables (the 12.3.1 hook). Add NOT VALID, validate, then drop the old check.
alter table public.usage_ledger_entry add constraint usage_ledger_entry_metric_check_v2 check (
  metric in ('results', 'artifact_gb_month', 'egress_gb', 'ai_tokens', 'support_hours', 'runner_minutes', 'device_minutes', 'ai_credit')) not valid;
alter table public.usage_ledger_entry validate constraint usage_ledger_entry_metric_check_v2;
alter table public.usage_ledger_entry drop constraint usage_ledger_entry_metric_check;
alter table public.usage_ledger_entry rename constraint usage_ledger_entry_metric_check_v2 to usage_ledger_entry_metric_check;

alter table public.usage_rollup add constraint usage_rollup_metric_check_v2 check (
  metric in ('results', 'artifact_gb_month', 'egress_gb', 'ai_tokens', 'support_hours', 'runner_minutes', 'device_minutes', 'ai_credit')) not valid;
alter table public.usage_rollup validate constraint usage_rollup_metric_check_v2;
alter table public.usage_rollup drop constraint usage_rollup_metric_check;
alter table public.usage_rollup rename constraint usage_rollup_metric_check_v2 to usage_rollup_metric_check;

alter table public.usage_price add constraint usage_price_metric_check_v2 check (
  metric in ('results', 'artifact_gb_month', 'egress_gb', 'ai_tokens', 'support_hours', 'runner_minutes', 'device_minutes', 'ai_credit')) not valid;
alter table public.usage_price validate constraint usage_price_metric_check_v2;
alter table public.usage_price drop constraint usage_price_metric_check;
alter table public.usage_price rename constraint usage_price_metric_check_v2 to usage_price_metric_check;

alter table public.quota_counter add constraint quota_counter_metric_check_v2 check (
  metric in ('results', 'artifact_gb_month', 'egress_gb', 'ai_tokens', 'support_hours', 'runner_minutes', 'device_minutes', 'ai_credit')) not valid;
alter table public.quota_counter validate constraint quota_counter_metric_check_v2;
alter table public.quota_counter drop constraint quota_counter_metric_check;
alter table public.quota_counter rename constraint quota_counter_metric_check_v2 to quota_counter_metric_check;
