-- 0046_usage_ledger.sql
--
-- Owner: 12.3.1-T1.1. `usage_ledger_entry`: one row per billed unit of one metered metric — the only
-- billing source (feature plan 12.3.1 §2 AC1-AC3, §4, §7-§9; area plan 12 §2 FR-12-7…FR-12-9,
-- NFR-12-1, D3, 12-CR-1; rulings T2, T3, T43, T50, T60). Every emitter (04.8.1 batch results, 06.5.2
-- storage periods, 08.5.x AI tokens, 12.2.1 support hours, the manual operator intake) writes here
-- through `IUsageLedger.RecordAsync`, one `insert … select from unnest(…) on conflict (org_id,
-- billing_key) do nothing returning billing_key` statement per batch (P2, T50).
--
-- The dedupe key is `unique (org_id, billing_key)`: a `billing_key` names one source metering event
-- (`batch:<event_batch_id>:<metric>`, `artifact_gb_month:<project>:<period>`, …), so a resent batch
-- inserts nothing and reports the key as a duplicate instead of billing twice (AC1). That is the
-- source-event dedupe of T43, deliberately separate from `idempotency_record`, which governs request
-- retries (T11) — the documented difference, not a conflict (00 §9.2).
--
-- Append-only (C6, AC3): rows are immutable once inserted and `evidgate_app` holds no UPDATE and no
-- DELETE on the table, so a correction cannot rewrite history — it inserts an adjustment row whose
-- `adjustment_of` points at the corrected row, and the original stays byte-identical (AC2). The
-- rejection is the ACL's, before any trigger or policy sees the row (0028/0034 precedent).
--
-- `price_ref` is `uuid null` with NO foreign key: `usage_price` is 12.3.1-T3's migration
-- (T44/DECISIONS §8 O5 — no published prices in V1), so the composite key to `(org_id, id)` on
-- `usage_price` is added by that file, exactly as 0028 deferred the `run`/`attempt` keys it could not
-- reference yet. An unpriced row carries `amount null`/`currency null` — never 0 (T60: a metric with
-- no price is unknown, not free).
--
-- KNOWN GAP for 12.3.1-T2.2 (`quantity` versus T60). The plan's §4 column list says
-- `quantity numeric not null`, and this file implements that literal: a row without a measured
-- quantity does not exist, so a missing source cannot be represented by a NULL quantity. T60 and
-- §7/§9 say the opposite for one metric — `egress_gb` rows stay "`null` + reason until
-- `IObjectStore` reports egress", and AC8 wants that metric reported as unavailable rather than 0.
-- The two cannot both hold for the same column, so the decision belongs to 12.3.1-T2.2 (its Done-when
-- is "`egress_gb` row is `null`, not 0"): it must either relax this NOT NULL (a new migration, with
-- the check that a null quantity also carries a reason) or drop the null-quantity rule from the plan.
-- Neither is invented here.
--
-- Row security: both policies are `for all to evidgate_app`, exactly like 0005/0020-0028. A policy
-- with no `to` clause applies to PUBLIC, which would let `evidgate_operator` (it holds the SELECT
-- grant below) set `evidgate.org_id` and read any org's billing data. No `TO evidgate_operator`
-- policy is created here (TL T76, D1): the grant-gated operator policy is 02.8.1's
-- `evidgate.operator_grant_ok` form, and until it lands the operator's SELECT grant reads zero rows.
--
-- Bound CHECKs use the 0006-0028 `char_length`/regex idiom: `metric` is the closed §4 list,
-- `period` is the §4 `YYYY-MM` form (a period is a calendar month, never an instant), `billing_key`
-- is a 1-200 character source id (T43), and `reason` is mandatory — 1-2000 characters — exactly
-- when the row is an adjustment, which is what makes the two-person adjustment act (T69) auditable
-- per row. `unit`, `amount`, `currency` and `occurred_at` carry no bound here because the plan's §4
-- does not give one; the emitters that first write a metric own its unit vocabulary (12.3.1-T2).
--
-- Idempotent like 0025/0027/0028: `create table if not exists` / `drop … if exists` so a re-run is a
-- no-op. One file runs in one transaction, so the table, its policies, its grants and its journal row
-- are all-or-nothing (migrations/README.md).

create table if not exists public.usage_ledger_entry (
  id uuid not null,
  org_id uuid not null,
  project_id uuid,
  metric text not null,
  quantity numeric not null,
  unit text not null,
  period text not null,
  billing_key text not null,
  occurred_at timestamptz not null,
  price_ref uuid,
  amount numeric,
  currency text,
  adjustment_of uuid,
  reason text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint usage_ledger_entry_pkey primary key (id),
  -- TL T3 / 00 §5.4.6: the composite-FK target the adjustment self-reference points at.
  constraint usage_ledger_entry_org_id_key unique (org_id, id),
  -- T43 / AC1: one row per source metering event, so a retried batch inserts nothing.
  constraint usage_ledger_entry_billing_key_key unique (org_id, billing_key),
  constraint usage_ledger_entry_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  -- AC2 / T3: an adjustment names the corrected row and can only name one of its own org —
  -- `(org_id, adjustment_of)` makes a cross-tenant pointer structurally impossible.
  constraint usage_ledger_entry_adjustment_fk foreign key (org_id, adjustment_of)
    references public.usage_ledger_entry (org_id, id),
  constraint usage_ledger_entry_metric_check check (
    metric in ('results', 'artifact_gb_month', 'egress_gb', 'ai_tokens', 'support_hours', 'runner_minutes', 'device_minutes')),
  constraint usage_ledger_entry_period_check check (period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'),
  constraint usage_ledger_entry_billing_key_check check (char_length(billing_key) between 1 and 200),
  constraint usage_ledger_entry_adjustment_reason_check check (
    adjustment_of is null or (reason is not null and char_length(reason) between 1 and 2000)),
  constraint usage_ledger_entry_version_check check (version >= 1)
);

-- P4: the filter of every roll-up, summary and statement read is (project, period, metric) within
-- the org — one index serves all of them, so no read fans out into a sequential scan.
create index if not exists idx_ledger_period
  on public.usage_ledger_entry (org_id, project_id, period, metric);

-- The referencing side of the adjustment self-reference (0024/0028 precedent): an owner erasure sweep
-- of one org walks its adjustments without scanning the ledger.
create index if not exists usage_ledger_entry_adjustment_idx
  on public.usage_ledger_entry (org_id, adjustment_of) where adjustment_of is not null;

-- The referencing side of the composite project FK is served by `idx_ledger_period`: its leading
-- `(org_id, project_id)` prefix is exactly the lookup a project delete/erasure issues, so a second
-- index on the same prefix would only add write cost to an append-only table (unlike 0028, where no
-- index led with the pair).

-- The shared concurrency trigger of 0004 (M1W3-01). The app role cannot UPDATE this table, so the
-- trigger is inert on the API path; it exists because 00 §7 gives every table carrying `version` a
-- database-owned increment, and the owner backfill/erasure paths are real UPDATEs.
drop trigger if exists usage_ledger_entry_set_updated_at_and_version on public.usage_ledger_entry;
create trigger usage_ledger_entry_set_updated_at_and_version
  before update on public.usage_ledger_entry
  for each row execute function public.set_updated_at_and_version();

alter table public.usage_ledger_entry enable row level security;
alter table public.usage_ledger_entry force row level security;

drop policy if exists usage_ledger_entry_tenant on public.usage_ledger_entry;
create policy usage_ledger_entry_tenant on public.usage_ledger_entry
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1): billing reads are audited app-role
-- reads (`billing:read`, T69); the operator reaches this table only through 02.8.1's grant-gated
-- `evidgate.operator_grant_ok` policy, and until that lands the SELECT grant below reads zero rows.

-- The migration role needs its own policy: `force row level security` applies to the table owner too,
-- so an expand/contract backfill or an owner erasure sweep would otherwise update zero rows without
-- failing (0004/0005/0028 precedent). Explicit and permissive on purpose — the alternative would be
-- BYPASSRLS, which DECISIONS F5 rejects.
drop policy if exists usage_ledger_entry_owner_backfill on public.usage_ledger_entry;
create policy usage_ledger_entry_owner_backfill on public.usage_ledger_entry
  for all to evidgate_owner using (true) with check (true);

-- AC3 / C6: append-only in SQL. `0001`'s default privileges grant `evidgate_app` full DML on every
-- table `evidgate_owner` creates in `public`, so the table-level `revoke` is required before the
-- minimal grant (0027/0028/0034 precedent): SELECT + INSERT, nothing else. A correction is a new row.
revoke update, delete on public.usage_ledger_entry from evidgate_app;
grant select, insert on public.usage_ledger_entry to evidgate_app;
grant select on public.usage_ledger_entry to evidgate_operator;
revoke all on public.usage_ledger_entry from evidgate_worker_sched;
