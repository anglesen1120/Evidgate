-- 0073_usage_price.sql
--
-- `usage_price`: one row per priced window of one metered metric, either the platform rate card
-- (`org_id` NULL, read by every tenant) or a per-org override. A price is never edited in place:
-- a change is a new row with a new window, which is what keeps a ledger row's `price_ref` meaning
-- the same amount forever.
--
-- WHY `org_id` IS NULLABLE. The NULL row is the platform rate card. It is not tenant data, so the
-- read policy is the documented all-org catalogue shape (`org_id IS NULL OR org_id =
-- evidgate.current_org_id()`), while the write policy is the plain tenant predicate, so an app
-- transaction can never insert, update or delete a global row — the only writer of one is the
-- definer below. The table is therefore registered as `catalogue` in `contracts/rls-exceptions.txt`
-- for the same reason `healing_switch` is, and the boot self-check judges exactly those two shapes.
--
-- WHY THE OVERLAP RULE IS A TRIGGER. Two prices of the same scope and metric may not cover the same
-- instant, or the resolver's "latest effective window wins" would depend on row order. The cheap
-- database form would be an EXCLUDE constraint over a range with btree_gist, but no migration in
-- this repository enables an extension, so the rule is a BEFORE INSERT trigger instead: it takes an
-- advisory transaction lock keyed by (scope, metric), so two concurrent loads cannot both pass the
-- existence check. Under that lock it refuses a repeated card version under the unique index's own
-- name (`usage_price_card_key`, 23505, the store's duplicate_version) and a genuine window clash
-- under its own (`usage_price_no_overlap`, 23514) instead of a bare CHECK. The windows are half-open
-- `[effective_from, effective_to)` and a NULL `effective_to` is infinity, so adjacent windows
-- `[a,b)` + `[b,c)` are legal. A per-org override is allowed to cover
-- an instant the global card also covers — override-first is the point — so only rows of the SAME
-- scope are compared, and the trigger runs with the caller's rights, so row level security already
-- limits an org's check to its own rows plus the global card.
--
-- WHY THE CARD VERSION IS UNIQUE PER SCOPE. The unique index over
-- `(coalesce(org_id, zero-uuid), metric, rate_card_version)` is what makes a card load idempotent at
-- the row level: the same version of the same metric can be loaded once per scope and a retry is
-- refused by the database, not by an application pre-check. The zero uuid sentinel mirrors the
-- roll-up's coalesce index (a real org id is never the nil uuid). The BEFORE INSERT guard below
-- answers a reload under the same constraint name, so a reloaded card whose window also overlaps an
-- existing row is still a duplicate-version refusal rather than an overlap one.
--
-- GRANTS. A price row is immutable, so the app role holds SELECT (its own rows plus the global
-- card) and INSERT (its own rows only) and nothing else: UPDATE and DELETE are revoked before the
-- minimal grant, because the migration role's default privileges would otherwise leave them in
-- place. The operator holds SELECT alone, and with FORCE row level security and no operator policy
-- that grant is inert: the operator reads zero rows until a grant-gated operator form adds its own
-- policy, which is the shape the ledger and `redaction_rule` already carry. The sweep role holds
-- nothing.

-- LOCKS. `set local lock_timeout` bounds every DDL wait in this file, including the trigger and the
-- key on `public.usage_ledger_entry`, so a busy ledger fails the migration for a retry instead of
-- stalling its writers behind an unqueued lock request.
--
-- THE DEFINER. `evidgate.usage_price_insert_global` is the only writer of a global row. It borrows
-- the migration role's rights, and FORCE row level security would otherwise deny the owner, so it
-- reaches the row through the explicit owner policy; that is exactly why it carries the tenant
-- boundary itself and refuses to run at all inside a tenant transaction. It validates nothing the
-- table's own constraints cannot (a bad metric, currency, window or version is answered by the
-- CHECK, a duplicate card version by the guard's version check), and it returns the stored id.
--
-- THE DEFERRED LEDGER REFERENCE. `usage_ledger_entry.price_ref` was declared without a foreign key
-- because this table did not exist yet. The key added here is SINGLE-column on purpose: a ledger
-- row always carries an `org_id`, a global price carries NULL, so a composite `(org_id, price_ref)`
-- key could never match a global row. Whether the referenced price is *visible* to the writer is
-- not something a foreign key can express, so the BEFORE INSERT OR UPDATE trigger below refuses a
-- row whose `price_ref` is neither a global price nor one of the row's own org — which also keeps
-- an owner backfill from filing a cross-tenant pointer. The key is added `not valid` and stays so:
-- no writer sets `price_ref` yet (the ledger store inserts it as null), so every existing ledger
-- row carries NULL and there is nothing to check, while a NOT VALID key still enforces every new
-- insert and every update of the column immediately, and the visibility trigger answers before it.
-- Validating would scan the whole ledger under its lock for no row it could reject, so a later
-- migration may validate once writers populate the column.
--
-- Idempotent in the 0025/0027/0028/0046 shape: `create table if not exists`, `create index if not
-- exists`, `drop … if exists` before every `create` and re-stated grants, so a re-run is a no-op.
-- One file runs in one transaction, so the table, its policies, its functions and the ledger key
-- are never observable apart. Rollback: `drop trigger if exists usage_ledger_entry_price_visible
-- on public.usage_ledger_entry; alter table public.usage_ledger_entry drop constraint if exists
-- usage_ledger_entry_price_fk; drop function if exists evidgate.usage_ledger_price_guard();
-- drop function if exists evidgate.usage_price_insert_global(uuid, text, numeric, text,
-- timestamptz, timestamptz, numeric, text); drop function if exists evidgate.usage_price_overlap_guard();
-- drop table if exists public.usage_price;` and remove the `contracts/rls-exceptions.txt` line.

set local lock_timeout = '5s';

create table if not exists public.usage_price (
  id uuid not null,
  -- NULL is the platform rate card; a value is a per-org override of it.
  org_id uuid,
  metric text not null,
  unit_price numeric not null,
  currency text not null,
  -- Half-open [effective_from, effective_to); a NULL end is infinity.
  effective_from timestamptz not null,
  effective_to timestamptz,
  included_quota numeric,
  rate_card_version text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint usage_price_pkey primary key (id),
  -- The composite key a tenant foreign key targets, as every business table declares it.
  constraint usage_price_org_id_key unique (org_id, id),
  constraint usage_price_metric_check check (
    metric in ('results', 'artifact_gb_month', 'egress_gb', 'ai_tokens', 'support_hours', 'runner_minutes', 'device_minutes')),
  constraint usage_price_unit_price_check check (unit_price >= 0),
  constraint usage_price_currency_check check (currency ~ '^[A-Z]{3}$'),
  constraint usage_price_window_check check (effective_to is null or effective_to > effective_from),
  constraint usage_price_included_quota_check check (included_quota is null or included_quota >= 0),
  constraint usage_price_rate_card_version_check check (char_length(rate_card_version) between 1 and 64),
  constraint usage_price_version_check check (version >= 1)
);

-- One card version of one metric per scope: the load's idempotency key. `coalesce` gives the NULL
-- scope a value, because a plain unique index treats every NULL as distinct and would let a card be
-- loaded twice globally.
create unique index if not exists usage_price_card_key
  on public.usage_price (coalesce(org_id, '00000000-0000-0000-0000-000000000000'::uuid), metric, rate_card_version);

-- The resolver's lookup: metric and instant, with the scope and the price it returns in the index.
create index if not exists idx_price_lookup
  on public.usage_price (metric, effective_from) include (org_id, unit_price);

-- The database owns `updated_at` and the version increment, as on every table that carries them.
drop trigger if exists usage_price_set_updated_at_and_version on public.usage_price;
create trigger usage_price_set_updated_at_and_version
  before update on public.usage_price
  for each row execute function public.set_updated_at_and_version();

-- The overlap refusal. Two integer-key advisory locks (scope, metric) serialize concurrent writers
-- of one scope, so the existence checks below cannot miss a row another transaction is inserting.
create or replace function evidgate.usage_price_overlap_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
as $$
begin
  -- An empty or inverted window is the table's CHECK to answer, so the refusal a caller sees names
  -- the constraint it actually violated.
  if new.effective_to is not null and new.effective_to <= new.effective_from then
    return new;
  end if;

  perform pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtext('evidgate.usage_price:' || new.metric),
    pg_catalog.hashtext(coalesce(new.org_id::text, 'global')));

  -- A duplicate card version is answered under its unique index's name: a reload is refused as a
  -- duplicate even when its window also overlaps, because the guard runs before the index and the
  -- store maps this name to its duplicate_version refusal.
  if exists (
    select 1
      from public.usage_price p
     where p.org_id is not distinct from new.org_id
       and p.metric = new.metric
       and p.rate_card_version = new.rate_card_version)
  then
    raise exception 'usage_price_card_key: this card version is already loaded for this metric in this scope'
      using errcode = '23505', constraint = 'usage_price_card_key';
  end if;

  if exists (
    select 1
      from public.usage_price p
     where p.org_id is not distinct from new.org_id
       and p.metric = new.metric
       and p.effective_from < coalesce(new.effective_to, 'infinity'::timestamptz)
       and coalesce(p.effective_to, 'infinity'::timestamptz) > new.effective_from)
  then
    raise exception 'usage_price_no_overlap: metric % has a price covering this window in the same scope (% ... %)',
        new.metric,
        new.effective_from,
        coalesce(new.effective_to::text, 'infinity')
      using errcode = '23514', constraint = 'usage_price_no_overlap';
  end if;

  return new;
end
$$;

drop trigger if exists usage_price_overlap_guard on public.usage_price;
create trigger usage_price_overlap_guard
  before insert on public.usage_price
  for each row execute function evidgate.usage_price_overlap_guard();

alter table public.usage_price enable row level security;
alter table public.usage_price force row level security;

-- Read: the org's own overrides plus the platform card it inherits — the catalogue read shape.
drop policy if exists usage_price_app_read on public.usage_price;
create policy usage_price_app_read on public.usage_price
  for select
  to evidgate_app
  using (org_id is null or org_id = evidgate.current_org_id());

-- Write: the org's own rows only. A NULL `org_id` never equals `evidgate.current_org_id()`, so a
-- global row is refused here (42501) — the definer is its only writer.
drop policy if exists usage_price_app_write on public.usage_price;
create policy usage_price_app_write on public.usage_price
  for insert
  to evidgate_app
  with check (org_id = evidgate.current_org_id());

-- FORCE row level security applies to the owner too, so the migration role needs its own policy
-- or the definer below would write zero rows without failing; a BYPASSRLS role is not an option.
drop policy if exists usage_price_owner_backfill on public.usage_price;
create policy usage_price_owner_backfill on public.usage_price
  for all to evidgate_owner using (true) with check (true);

-- A price is immutable: a correction is a new row. The revoke must come before the grant, because
-- the migration role's default privileges hand the app role every DML verb.
revoke update, delete on public.usage_price from evidgate_app;
grant select, insert on public.usage_price to evidgate_app;
grant select on public.usage_price to evidgate_operator;
revoke all on public.usage_price from evidgate_worker_sched;

-- The only writer of a global row. The tenant boundary is the function's own: RLS cannot confine a
-- definer (it runs as the migration role), so a call made inside a tenant transaction is refused
-- before any statement, with 23514 because the caller does hold EXECUTE.
create or replace function evidgate.usage_price_insert_global(
  p_id uuid,
  p_metric text,
  p_unit_price numeric,
  p_currency text,
  p_effective_from timestamptz,
  p_effective_to timestamptz,
  p_included_quota numeric,
  p_rate_card_version text)
returns uuid
language plpgsql
volatile
security definer
set search_path = ''
as $$
begin
  if evidgate.current_org_id() is not null then
    raise exception 'usage price: a global price is not writable inside a tenant transaction (org %)', evidgate.current_org_id()
      using errcode = '23514', constraint = 'usage_price_global_tenant_scope';
  end if;

  insert into public.usage_price
      (id, org_id, metric, unit_price, currency, effective_from, effective_to, included_quota, rate_card_version)
  values
      (p_id, null, p_metric, p_unit_price, p_currency, p_effective_from, p_effective_to, p_included_quota, p_rate_card_version);

  return p_id;
end
$$;

revoke all on function evidgate.usage_price_insert_global(uuid, text, numeric, text, timestamptz, timestamptz, numeric, text) from public;
grant execute on function evidgate.usage_price_insert_global(uuid, text, numeric, text, timestamptz, timestamptz, numeric, text) to evidgate_app;

-- A ledger row may name a global price or one of its own org's overrides, never another org's. The
-- foreign key alone cannot say that (it sees ids, not tenants), and row level security would hide
-- the referenced row instead of answering, so the check is explicit. The FK's name is reused as the
-- constraint name: a dangling reference and a foreign one are the same broken reference to the
-- caller, and both answer 23503. An update that rewrites the reference or the tenant is checked
-- too, so a later repair statement cannot move a row onto a price its org may not see.
create or replace function evidgate.usage_ledger_price_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
as $$
begin
  if not exists (
    select 1
      from public.usage_price p
     where p.id = new.price_ref
       and (p.org_id is null or p.org_id = new.org_id))
  then
    raise exception 'usage_ledger_entry_price_fk: price_ref % is not a price visible to org %', new.price_ref, new.org_id
      using errcode = '23503', constraint = 'usage_ledger_entry_price_fk';
  end if;

  return new;
end
$$;

drop trigger if exists usage_ledger_entry_price_visible on public.usage_ledger_entry;
create trigger usage_ledger_entry_price_visible
  before insert or update of price_ref, org_id on public.usage_ledger_entry
  for each row when (new.price_ref is not null)
  execute function evidgate.usage_ledger_price_guard();

do $fk$
begin
  if not exists (
    select 1
      from pg_catalog.pg_constraint
     where conrelid = 'public.usage_ledger_entry'::regclass
       and conname = 'usage_ledger_entry_price_fk')
  then
    alter table public.usage_ledger_entry
      add constraint usage_ledger_entry_price_fk foreign key (price_ref) references public.usage_price (id) not valid;
  end if;
end
$fk$;
