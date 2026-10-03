-- 0015_identity_audit_log.sql
--
-- Owner: 02.7.1-T1.1. The tenant audit trail (area plan 02 §4.1, feature plan 02.7.1 §4):
-- one append-only row per permission-checked mutation, chained per org so any change,
-- deletion or reordering is detectable (TL T8).
--
-- Append-only is enforced by the database, not by convention (AC2): `evidgate_app` is
-- granted INSERT and SELECT and has UPDATE, DELETE and TRUNCATE revoked, so even a bug
-- or an injected statement in the application cannot rewrite history. The row-level
-- policy is the standard tenant policy (TL T2), so a foreign org's rows are invisible.
--
-- The chain: `seq` is dense per org (`unique (org_id, seq)`), `prev_hash` is the previous
-- row's `entry_hash` (32 zero bytes for the first row) and `entry_hash` is
-- sha256(prev_hash || RFC 8785 canonical row fields). The writer takes
-- `pg_advisory_xact_lock(hashtext('audit:' || org_id))` as the LAST statement group before
-- commit, so concurrent writers of one org serialise for about one insert. The hash is
-- computed in the application (`Evidgate.BuildingBlocks.Persistence.Audit`), not here:
-- the canonicalisation is not available in SQL, and both the writer and the verifier
-- share one implementation.
--
-- Deliberately absent: `updated_at` / `version` (a row is never updated) and any
-- `TO evidgate_operator` policy (02.8.1's grant-gated form, TL T76). The owner may only
-- SELECT and INSERT: an expand/contract backfill never rewrites audit history.

create table if not exists public.audit_log (
  id uuid not null,
  org_id uuid not null,
  seq bigint not null,
  actor_type text not null,
  actor_id text not null,
  action text not null,
  object_type text,
  object_id text,
  before_hash bytea,
  after_hash bytea,
  request_id text,
  trace_id text,
  metadata jsonb,
  prev_hash bytea not null,
  entry_hash bytea not null,
  created_at timestamptz not null default now(),
  constraint audit_log_pkey primary key (id),
  constraint audit_log_org_id_key unique (org_id, id),
  constraint audit_log_org_seq_key unique (org_id, seq),
  constraint audit_log_organization_fk foreign key (org_id) references public.organization (id),
  constraint audit_log_seq_check check (seq >= 1),
  constraint audit_log_actor_type_check check (actor_type in ('user', 'ci_producer', 'agent', 'platform_operator', 'system')),
  constraint audit_log_hash_length_check check (octet_length(prev_hash) = 32 and octet_length(entry_hash) = 32),
  constraint audit_log_action_length_check check (char_length(action) between 1 and 128)
);

-- The list cursor of GET /v1/orgs/{o}/audit: newest first on (created_at, id).
create index if not exists audit_log_org_created_id_idx
  on public.audit_log (org_id, created_at desc, id desc);

alter table public.audit_log enable row level security;
alter table public.audit_log force row level security;

drop policy if exists audit_log_app_tenant on public.audit_log;
create policy audit_log_app_tenant on public.audit_log
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- The owner (and the SECURITY DEFINER functions it owns) can read and append, never
-- rewrite: there is deliberately no UPDATE or DELETE policy for it, so under FORCE ROW
-- LEVEL SECURITY those statements match zero rows even for the table owner.
drop policy if exists audit_log_owner_read on public.audit_log;
create policy audit_log_owner_read on public.audit_log
  for select
  to evidgate_owner
  using (true);

drop policy if exists audit_log_owner_insert on public.audit_log;
create policy audit_log_owner_insert on public.audit_log
  for insert
  to evidgate_owner
  with check (true);

revoke update, delete, truncate on public.audit_log from evidgate_app;
revoke all on public.audit_log from evidgate_worker_sched;
grant select on public.audit_log to evidgate_operator;

-- The chain-verification task is a platform task (no tenant scope) that must learn which
-- orgs have an audit trail. It gets exactly that and nothing else: a bounded, ordered page
-- of org ids, through an owner-owned definer function the sweep role may execute. It then
-- verifies each org inside that org's own tenant scope, under RLS.
create or replace function evidgate.audit_org_ids(p_after uuid, p_limit integer) returns setof uuid
  language sql
  stable
  security definer
  set search_path = ''
  as $$
    select a.org_id
      from public.audit_log a
     where a.org_id > coalesce(p_after, '00000000-0000-0000-0000-000000000000'::uuid)
     group by a.org_id
     order by a.org_id
     limit least(greatest(coalesce(p_limit, 100), 1), 1000)
  $$;

revoke all on function evidgate.audit_org_ids(uuid, integer) from public;
grant execute on function evidgate.audit_org_ids(uuid, integer) to evidgate_worker_sched;
