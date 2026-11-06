-- 0080_usage_billing_statement.sql
--
-- An issued billing statement is a document, not a view: the numbers it carries and the worksheet it
-- was computed from are frozen when the operator issues it, because a later ledger row or price table
-- change must never rewrite an invoice that has already been sent.
--
-- `unique (org_id, period, kind)` is what makes re-issue idempotent. A second issue of the same period
-- collides with the row the first one wrote, reads that row back and compares its `content_hash` with
-- the recompute: an identical hash is a replay that answers the stored document, and a different one is
-- a late change the caller refuses instead of overwriting. The database only has to guarantee one row
-- per period, which the key does under concurrent inserts (`insert … on conflict do nothing`).
--
-- `content_hash` is the canonical document hash. `worksheet_json` is jsonb, and jsonb normalises key
-- order and whitespace on storage, so the column's text is never byte-identical to the document that
-- was hashed; the stored hash is the only address of that document.
--
-- Append-only: the app role holds SELECT and INSERT only, so an issued statement cannot be edited or
-- deleted by the API path (a correction is a statement of a later period). Rows are tenant-scoped by
-- row level security exactly as the ledger's are, and the operator's SELECT grant stays inert until a
-- grant-gated operator policy exists.
--
-- Idempotent like 0046/0077: `set local lock_timeout` bounds the DDL wait and every object is guarded
-- or dropped before it is created, so a re-run changes nothing.

set local lock_timeout = '5s';

create table if not exists public.billing_statement (
  id uuid not null,
  org_id uuid not null,
  period text not null,
  kind text not null,
  total_amount numeric not null,
  currency text not null,
  line_count integer not null,
  content_hash text not null,
  worksheet_json jsonb not null,
  issued_by uuid not null,
  issued_at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint billing_statement_pkey primary key (id),
  -- The composite-key target every tenant table declares for its child references.
  constraint billing_statement_org_id_key unique (org_id, id),
  -- The idempotency key of an issue: one statement of a kind per period and tenant.
  constraint billing_statement_period_kind_key unique (org_id, period, kind),
  -- The tenant root has no `org_id` column, so a tenant table names it through its own org column.
  constraint billing_statement_organization_fk foreign key (org_id)
    references public.organization (id),
  constraint billing_statement_period_check check (period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'),
  constraint billing_statement_kind_check check (kind in ('invoice')),
  constraint billing_statement_currency_check check (currency ~ '^[A-Z]{3}$'),
  constraint billing_statement_line_count_check check (line_count >= 0),
  constraint billing_statement_content_hash_check check (content_hash ~ '^[0-9a-f]{64}$'),
  -- A statement is a bounded document: the worksheet is the longest member, so the cap lives on it.
  constraint billing_statement_worksheet_size_check check (
    jsonb_typeof(worksheet_json) = 'object' and octet_length(worksheet_json::text) <= 8388608),
  constraint billing_statement_version_check check (version >= 1)
);

-- The document freezes `issued_by` as an operator identity, which is a platform principal rather than
-- a tenant row; no key is declared for it, so an operator erasure does not cascade into issued billing
-- history.

drop trigger if exists billing_statement_set_updated_at_and_version on public.billing_statement;
create trigger billing_statement_set_updated_at_and_version
  before update on public.billing_statement
  for each row execute function public.set_updated_at_and_version();

alter table public.billing_statement enable row level security;
alter table public.billing_statement force row level security;

drop policy if exists billing_statement_tenant on public.billing_statement;
create policy billing_statement_tenant on public.billing_statement
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- FORCE row level security applies to the owner too, so an audit or erasure sweep needs its own
-- permissive policy (0004/0028/0046 precedent).
drop policy if exists billing_statement_owner_backfill on public.billing_statement;
create policy billing_statement_owner_backfill on public.billing_statement
  for all to evidgate_owner using (true) with check (true);

-- Append-only in SQL: 0001's default privileges grant the app role full DML, so the table-level revoke
-- comes before the minimal grant (0046 precedent). A statement is written once and read back.
revoke update, delete on public.billing_statement from evidgate_app;
grant select, insert on public.billing_statement to evidgate_app;
-- No `to evidgate_operator` policy, deliberately (0046 precedent): an issued statement is read through
-- the audited app-role worksheet, and the SELECT grant alone reaches no row because RLS has no policy
-- for the operator. Adding such a policy later would hand it an unaudited billing read.
grant select on public.billing_statement to evidgate_operator;
revoke all on public.billing_statement from evidgate_worker_sched;
