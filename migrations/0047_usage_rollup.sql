-- 0047_usage_rollup.sql
--
-- Owner: 12.3.1-T1.2. `usage_rollup`: the per-(org, project, period, metric) aggregate of
-- `usage_ledger_entry` that the idempotent recompute owns (feature plan 12.3.1 §2 AC6, §4, §7;
-- area plan 12 §2 FR-12-8, NFR-12-1; rulings T43, T45, T60). The roll-up job (`usage_rollup.compute`,
-- Cronos daily + on period close) writes it with one upsert per row, so re-running a closed period
-- reproduces identical totals instead of appending a second aggregate (AC6, NFR-12-1). The
-- reconciliation query (12.3.1-T5) reads the same rows against the ledger and reports `gap_flag`
-- when a source stream shows usage with no ledger rows, plus duplicate `billing_key` attempts.
--
-- The upsert target and why it is an expression index. The plan §4 names
-- `unique (org_id, project_id, period, metric)`, but in a plain unique constraint (or a plain unique
-- index) NULLs compare distinct: an org-level row (`project_id is null` — `support_hours`, T45's
-- tenant-wide limits) would never collide with itself, so every recompute would append another
-- aggregate and AC6 could never hold for exactly the metrics that need it most. The unique index
-- below therefore indexes `coalesce(project_id, '00000000-0000-0000-0000-000000000000')`, and that
-- same expression is the `on conflict (…) do update` arbiter of the recompute:
--
--   insert into public.usage_rollup (…) values (…)
--   on conflict (org_id,
--                coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid),
--                period, metric)
--   do update set total_quantity = excluded.total_quantity, …;
--
-- The all-zero UUID is the sentinel: application ids are UUIDv7 (00 §7, the high nibble of the
-- version field is 7), so no `project.id` can ever equal it and the sentinel cannot swallow a real
-- project row. `nulls not distinct` (0007:78, 0039) would express the same equality on the four
-- plain columns; the coalesce form is used so that the recompute's `on conflict` arbiter names the
-- null rule explicitly in the same expression the index is built on.
--
-- Grants: the app role may SELECT, INSERT and UPDATE — the recompute is an upsert, so a
-- SELECT+INSERT-only grant would make the second run of a period fail 42501 — but never DELETE, and
-- the UPDATE is column-scoped to the derived numbers (0028/0033/0039 precedent): `org_id`,
-- `project_id`, `period` and `metric` are the key the aggregate belongs to, and a recompute that
-- could rewrite them would move a total into another project's statement. `0001`'s default
-- privileges grant full DML, so both `revoke`s are required before the grants.
--
-- Row security: the tenant policy is `for all to evidgate_app` over the helpers (T2), with the
-- owner-backfill policy FORCE row level security requires (DECISIONS F5, 0004/0028 precedent), and
-- deliberately no `TO evidgate_operator` policy (TL T76, D1) — the operator's SELECT grant below
-- reads zero rows until 02.8.1's grant-gated policy lands.
--
-- `with (fillfactor = 80)` (P-2), additive to the feature plan §4 (its `usage_rollup` line gives no
-- storage parameters): every row is an upsert target rewritten once per recompute, and the UPDATE
-- touches only non-indexed columns, so 20 % page headroom keeps each rewrite a HOT update instead of
-- spilling a new tuple version to a fresh page.
--
-- Idempotent like 0025/0027/0028: `create table if not exists` / `drop … if exists`.

create table if not exists public.usage_rollup (
  id uuid not null,
  org_id uuid not null,
  project_id uuid,
  period text not null,
  metric text not null,
  total_quantity numeric not null,
  total_amount numeric,
  currency text,
  row_count integer not null,
  gap_flag boolean not null default false,
  computed_at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint usage_rollup_pkey primary key (id),
  -- TL T3 / 00 §5.4.6: the composite-FK target every tenant table needs.
  constraint usage_rollup_org_id_key unique (org_id, id),
  constraint usage_rollup_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  constraint usage_rollup_period_check check (period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'),
  constraint usage_rollup_metric_check check (
    metric in ('results', 'artifact_gb_month', 'egress_gb', 'ai_tokens', 'support_hours', 'runner_minutes', 'device_minutes')),
  constraint usage_rollup_row_count_check check (row_count >= 0),
  constraint usage_rollup_version_check check (version >= 1)
) with (fillfactor = 80);

-- The upsert target of the recompute (AC6): one aggregate per (org, project-or-org-level, period,
-- metric). See the header for why the project member is coalesced to the all-zero sentinel instead
-- of being indexed as a plain nullable column.
create unique index if not exists usage_rollup_scope_key
  on public.usage_rollup (org_id, coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid), period, metric);

-- The referencing side of the composite project FK (0024/0028 precedent): an owner erasure sweep of
-- one project walks its aggregates instead of scanning the table.
create index if not exists usage_rollup_project_idx
  on public.usage_rollup (org_id, project_id);

-- The shared concurrency trigger of 0004 (M1W3-01): the database owns `updated_at` and the `version`
-- increment, so the recompute's upsert cannot leave a stale token behind.
drop trigger if exists usage_rollup_set_updated_at_and_version on public.usage_rollup;
create trigger usage_rollup_set_updated_at_and_version
  before update on public.usage_rollup
  for each row execute function public.set_updated_at_and_version();

alter table public.usage_rollup enable row level security;
alter table public.usage_rollup force row level security;

drop policy if exists usage_rollup_tenant on public.usage_rollup;
create policy usage_rollup_tenant on public.usage_rollup
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1); see the header.

-- The migration role needs its own policy (FORCE applies to the owner too): a period recompute or
-- backfill would otherwise update zero rows without failing (0004/0005/0028 precedent).
drop policy if exists usage_rollup_owner_backfill on public.usage_rollup;
create policy usage_rollup_owner_backfill on public.usage_rollup
  for all to evidgate_owner using (true) with check (true);

-- Recompute = upsert: SELECT, INSERT and a column-scoped UPDATE on the derived numbers, never DELETE.
revoke update, delete on public.usage_rollup from evidgate_app;
grant select, insert on public.usage_rollup to evidgate_app;
grant update (total_quantity, total_amount, currency, row_count, gap_flag, computed_at)
  on public.usage_rollup to evidgate_app;
grant select on public.usage_rollup to evidgate_operator;
revoke all on public.usage_rollup from evidgate_worker_sched;
