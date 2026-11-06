-- 0078_usage_quota_counter.sql
--
-- The quota counter: one row per (org, project-or-tenant, period, metric) holding the units already
-- consumed, the agreed limit and the two threshold stamps. A metered hot path asks one question before
-- it commits work — "may this batch add q units?" — and must answer it without a read-then-write race,
-- so the gate is one statement: a conditional UPDATE of `used` (`where used + q <= quota_limit`) that
-- answers the same round trip for a scope with no counter row, and, only when a counter exists and the
-- call did not fit, a second statement that reserves or stamps in one conditional UPDATE. Two batches
-- racing the last unit then serialise on the row lock the UPDATE takes, and the loser matches no row
-- instead of raising an exception: `used <= quota_limit` holds by construction. The stamp carries the
-- same capacity predicate, so a raise that lands between the two statements cannot leave a counter with
-- room blocked.
--
-- The gate's predicate repeats the index expression. `unique (org_id, project_id, period, metric)`
-- cannot be a plain constraint here: NULLs compare distinct, so a tenant-level limit (`project_id`
-- null) would never collide with itself and every period would add a second counter for the same scope.
-- The unique index below therefore indexes `coalesce(project_id, '000…0')`, and the gate names the same
-- expression on both sides of its equality, so the index is the access path rather than a filter over
-- the tenant's rows. The all-zero UUID cannot equal a generated id (UUIDv7), exactly as in the roll-up's
-- scope key.
--
-- `quota_limit`, not `limit`: the column appears in every gate statement, and `limit` is a reserved word
-- that would need quoting at each use — one forgotten quote is a syntax error in the hottest statement
-- of the module. The name keeps the meaning, not the spelling.
--
-- Grants: the app role SELECTs (the gate reads the row it just updated), INSERTs (a raise lands a
-- counter for a scope that has none) and UPDATEs only the four columns the counter owns — `used` and
-- `warned_at` for the increment, `quota_limit`/`blocked_at` for an audited raise. A scope or tenant
-- column inside the grant would let a write move a consumed quantity into another project's statement,
-- so those stay outside it; `warn_ratio` is outside it too, because nothing writes the warning line after
-- the row is created. DELETE is refused outright, because a limit is closed by a new period, never by
-- dropping the row. `evidgate_operator` and `evidgate_worker_sched` hold nothing: the operator raises a
-- limit through the audited command on the app role's tenant transaction, and no platform sweep reads a
-- tenant's counters.
--
-- Row security: the tenant policy is `for all to evidgate_app` (the counter is written in the caller's
-- own tenant transaction) plus the owner policy FORCE row level security requires for a backfill or an
-- operator repair.
--
-- Idempotent like 0047/0076: `set local lock_timeout` bounds the DDL waits, the table is
-- `create table if not exists`, and every index, trigger and policy is guarded or dropped first.

set local lock_timeout = '5s';

create table if not exists public.quota_counter (
  id uuid not null,
  org_id uuid not null,
  project_id uuid,
  period text not null,
  metric text not null,
  used numeric not null default 0,
  quota_limit numeric not null,
  warn_ratio numeric not null default 0.8,
  warned_at timestamptz,
  blocked_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint quota_counter_pkey primary key (id),
  -- 00 §5.4.6: the composite-FK target every tenant table needs.
  constraint quota_counter_org_id_key unique (org_id, id),
  constraint quota_counter_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  -- The counter's retry hint and the operator's raise both build a date from the period, so the year is
  -- bounded here as well as in the domain: 1999-12 and 3000-01 are refused instead of reaching a .NET
  -- date type that cannot represent them. Four digits starting with 2 are exactly 2000..2999.
  constraint quota_counter_period_check check (period ~ '^2[0-9]{3}-(0[1-9]|1[0-2])$'),
  constraint quota_counter_metric_check check (
    metric in ('results', 'artifact_gb_month', 'egress_gb', 'ai_tokens', 'support_hours', 'runner_minutes', 'device_minutes')),
  -- A refused quantity is never counted and a limit is never negative, infinite or NaN: the two halves of
  -- the gate's arithmetic are enforced here, so a corrupt row cannot make `used + q <= quota_limit` true by
  -- accident. PostgreSQL orders NaN above every number, so the comparison alone would admit it.
  constraint quota_counter_used_check check (
    used >= 0 and used <> 'NaN'::numeric and used < 'Infinity'::numeric),
  constraint quota_counter_quota_limit_check check (
    quota_limit >= 0 and quota_limit <> 'NaN'::numeric and quota_limit < 'Infinity'::numeric),
  -- The warning line is a fraction strictly inside the interval: 0 would warn on the first unit and 1
  -- would warn only at the point where the gate already blocks.
  constraint quota_counter_warn_ratio_check check (warn_ratio > 0 and warn_ratio < 1),
  constraint quota_counter_version_check check (version >= 1)
);

-- The gate's access path and the raise's upsert arbiter: one counter per (org, project-or-tenant,
-- period, metric). See the header for why the project member is coalesced instead of indexed as a
-- plain nullable column.
create unique index if not exists quota_counter_scope_key
  on public.quota_counter (org_id, coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid), period, metric);

-- The referencing side of the composite project FK (0024/0028 precedent): an owner erasure sweep of one
-- project walks its counters instead of scanning the table.
create index if not exists quota_counter_project_idx
  on public.quota_counter (org_id, project_id);

-- The shared concurrency trigger of 0004: the database owns `updated_at` and the `version` increment, so
-- neither the gate's increment nor a raise can leave a stale token behind.
drop trigger if exists quota_counter_set_updated_at_and_version on public.quota_counter;
create trigger quota_counter_set_updated_at_and_version
  before update on public.quota_counter
  for each row execute function public.set_updated_at_and_version();

alter table public.quota_counter enable row level security;
alter table public.quota_counter force row level security;

drop policy if exists quota_counter_app_tenant on public.quota_counter;
create policy quota_counter_app_tenant on public.quota_counter
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- FORCE applies to the table owner too, so the backfill/repair path needs its own permissive policy
-- (0004/0028/0047 precedent).
drop policy if exists quota_counter_owner_backfill on public.quota_counter;
create policy quota_counter_owner_backfill on public.quota_counter
  for all to evidgate_owner using (true) with check (true);

-- The increment, the refusal stamp and the raise share one write surface: SELECT + INSERT, a
-- column-scoped UPDATE on the four counter columns (never on the scope, never on the warning ratio, which
-- only the migration's default sets), and no DELETE. 0001's default privileges hand the app role full DML,
-- so the revoke comes before the grants.
revoke update, delete on public.quota_counter from evidgate_app;
grant select, insert on public.quota_counter to evidgate_app;
grant update (used, quota_limit, warned_at, blocked_at)
  on public.quota_counter to evidgate_app;

-- Decided explicitly rather than inherited from 0001's default privileges: the counter is written and
-- read through the app role on the audited operator ingress, and the platform sweep role never touches
-- a tenant's quota.
revoke all on public.quota_counter from evidgate_operator;
revoke all on public.quota_counter from evidgate_worker_sched;
