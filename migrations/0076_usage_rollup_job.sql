-- 0076_usage_rollup_job.sql
--
-- The roll-up job's two missing pieces: a bounded cross-tenant org scan for the
-- platform task, and the table that records duplicate billing-key attempts, which
-- the ledger reports but until now did not persist. The recompute itself writes the
-- existing `usage_rollup` table (0047); its upsert arbiter is unchanged.
--
-- `usage_duplicate_attempt` exists because a duplicate is not a ledger row: the
-- ledger answers `duplicates` for a replayed key and writes nothing, so the only
-- evidence a source sent the same metering event twice is this counter. It is
-- upserted in the caller's own tenant transaction by the same statement group as the
-- ledger insert, so a rolled-back batch leaves neither a row nor an attempt.
--
-- Locks: `set local lock_timeout` bounds every DDL wait in one transaction.

set local lock_timeout = '5s';

-- The billing reconciliation read joins this table to `usage_ledger_entry` on
-- `(org_id, billing_key)` to learn the period of a duplicated key, so the tenant key
-- doubles as that lookup; the recount order is stable on the same key.
create table if not exists public.usage_duplicate_attempt (
  id uuid not null,
  org_id uuid not null,
  billing_key text not null,
  attempts integer not null,
  first_seen_at timestamptz not null,
  last_seen_at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint usage_duplicate_attempt_pkey primary key (id),
  constraint usage_duplicate_attempt_org_id_key unique (org_id, id),
  constraint usage_duplicate_attempt_billing_key_key unique (org_id, billing_key),
  constraint usage_duplicate_attempt_billing_key_check check (char_length(billing_key) between 1 and 200),
  constraint usage_duplicate_attempt_attempts_check check (attempts >= 1),
  constraint usage_duplicate_attempt_version_check check (version >= 1)
);

-- The shared concurrency trigger of 0004: the database owns `updated_at` and the
-- `version` increment, so the recount cannot leave a stale token behind.
drop trigger if exists usage_duplicate_attempt_set_updated_at_and_version on public.usage_duplicate_attempt;
create trigger usage_duplicate_attempt_set_updated_at_and_version
  before update on public.usage_duplicate_attempt
  for each row execute function public.set_updated_at_and_version();

alter table public.usage_duplicate_attempt enable row level security;
alter table public.usage_duplicate_attempt force row level security;

drop policy if exists usage_duplicate_attempt_app_tenant on public.usage_duplicate_attempt;
create policy usage_duplicate_attempt_app_tenant on public.usage_duplicate_attempt
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- FORCE row level security applies to the table owner too, so the backfill/erasure
-- path needs its own permissive policy (0004/0028/0047 precedent).
drop policy if exists usage_duplicate_attempt_owner_backfill on public.usage_duplicate_attempt;
create policy usage_duplicate_attempt_owner_backfill on public.usage_duplicate_attempt
  for all to evidgate_owner using (true) with check (true);

-- The write is `insert … on conflict (org_id, billing_key) do update set attempts =
-- attempts + 1, last_seen_at = now()`: SELECT + INSERT and a column-scoped UPDATE on
-- the recount itself, never on the key, and never DELETE.
revoke update, delete on public.usage_duplicate_attempt from evidgate_app;
grant select, insert on public.usage_duplicate_attempt to evidgate_app;
grant update (attempts, last_seen_at) on public.usage_duplicate_attempt to evidgate_app;

-- Decided explicitly rather than inherited from 0001's default privileges: duplicate
-- attempts are reached through the audited reconciliation read as the app role, so the
-- operator role holds nothing here (D1).
revoke all on public.usage_duplicate_attempt from evidgate_operator;
revoke all on public.usage_duplicate_attempt from evidgate_worker_sched;

-- The roll-up task is a platform task: it cannot read any tenant's ledger row, so it
-- learns which orgs have usage through this owner-owned projection — a bounded,
-- ordered page of org ids and nothing else, the same footprint as 0015's
-- `evidgate.audit_org_ids`. It then recomputes each org inside that org's own tenant
-- scope, under RLS.
create or replace function evidgate.usage_ledger_org_ids(p_after uuid, p_limit integer) returns setof uuid
  language sql
  stable
  security definer
  set search_path = ''
  as $$
    select distinct l.org_id
      from public.usage_ledger_entry l
     where l.org_id > coalesce(p_after, '00000000-0000-0000-0000-000000000000'::uuid)
     order by l.org_id
     limit least(greatest(coalesce(p_limit, 100), 1), 1000)
  $$;

-- lint: audit-deferred shared-org-id-projection evidgate.usage_ledger_org_ids
-- A per-call audit row has no tenant to belong to: the projection spans orgs and
-- `audit_log.org_id` is NOT NULL, exactly like `audit_org_ids`. The fire and its org
-- count are observed through the task row the sweep writes, so no audit is owed here.
revoke all on function evidgate.usage_ledger_org_ids(uuid, integer) from public;
grant execute on function evidgate.usage_ledger_org_ids(uuid, integer) to evidgate_worker_sched;
