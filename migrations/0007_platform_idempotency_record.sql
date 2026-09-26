-- 0007_platform_idempotency_record.sql
--
-- Owner: 01.5.1-T6.1 (slice B1). `idempotency_record` is the only arbiter of
-- `Idempotency-Key` on mutating endpoints (00 §9.1, TL T11): no business table
-- carries a permanent unique constraint on a client key, so a retried mutation
-- is answered from this table instead of being executed twice. Unlike `outbox`
-- (0006) this is tenant data, not a mechanism: it is read and written inside the
-- command's tenant transaction by the C2 idempotency behaviour (00 §5.5 C2, step
-- "idempotency (commands with Idempotency-Key)"), so it gets ENABLE + FORCE row
-- level security with the standard org policy.
--
-- Shape (01.5.1 §4, T94):
--   * surrogate `id` primary key + `unique nulls not distinct (org_id,
--     project_id, operation, key)`. The key cannot be the primary key because an
--     org-scoped operation has no project (00 §7 starting at T94), and WITHOUT
--     `nulls not distinct` two rows with the same key and a NULL `project_id`
--     would both be accepted — the duplicate silently executes twice. PostgreSQL
--     15+ supports it; we run 18.
--   * `request_hash` is the RFC 8785 canonical digest `sha256:<hex>` of the
--     request document (`CanonicalHash`, 01.3.2-T4). Same key + same hash →
--     the command is replayed from the stored response; same key + different
--     hash → 409 `idempotency_conflict` (00 §9.1). Storing a digest instead of
--     the request keeps secrets out of the row (00 §5.7 S3).
--   * `response_status` / `response_body` are NULL until the command completes.
--     That window never escapes the transaction: the begin row, the handler's
--     writes and the stored response all commit together, so a committed row
--     without a response cannot be observed by another request.
--   * `expires_at >= created_at + interval '24 hours'` is the retention floor of
--     00 §7/T11, enforced by the database rather than by convention.
--   * `created_at` / `updated_at` / `version` plus the shared
--     `set_updated_at_and_version()` trigger (0004): completing a record is an
--     UPDATE, and the version column keeps that write honest if two writers ever
--     touch the same row. `updated_at`/`version` live in the table rather than in
--     a partial index so a future operator/replay path can see when a response
--     was stored.
--
-- `(org_id, id)` is the 00 §5.4.6 composite tenant key (TL T3): every business
-- table declares it, and the 02.6.1-T2 lint fails a table without it.
-- `idempotency_record` holds tenant data, so the platform/catalogue exception list
-- of 02 §4 — which covers `user`, `permission`, `organization`, `schema_migration`,
-- the `task` queue and the other mechanism tables — does not apply to it.
-- `project_id` additionally carries the 00 §5.4.1 composite tenant FK to
-- `project (org_id, id)`, so a record can never be stamped with another org's
-- project id.
--
-- The purge function below is the T80 sweep: owner `SECURITY DEFINER`,
-- `set search_path = ''`, schema-qualified references, `EXECUTE` granted to
-- `evidgate_worker_sched` alone, which is the role 0001 created for exactly this.
-- 01.5.1-T6.2 registers the `idempotency.purge` schedule that calls it; the
-- function itself is complete here so the schedule has a frozen signature.
-- T80 also requires every call to be audited: `audit_log` is 02.7.1 (M2) and does
-- not exist in M1, so the audit row lands with that story — recorded as a
-- deviation for this wave rather than inventing a second audit table.

create table if not exists public.idempotency_record (
  id uuid not null,
  org_id uuid not null,
  project_id uuid,
  operation text not null,
  key text not null,
  request_hash text not null,
  response_status integer,
  response_body jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  expires_at timestamptz not null,
  constraint idempotency_record_pkey primary key (id),
  constraint idempotency_record_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  constraint idempotency_record_key_length_check check (char_length(key) between 1 and 128),
  constraint idempotency_record_expiry_check check (expires_at >= created_at + interval '24 hours')
);

-- The arbitration key (T94). `nulls not distinct` is what makes a NULL
-- `project_id` conflict with itself instead of slipping past the unique index, so
-- two concurrent org-scoped commands with one key get exactly one winner.
create unique index if not exists idempotency_record_uq on public.idempotency_record (org_id, project_id, operation, key) nulls not distinct;

-- 00 §5.4.6 / TL T3: the composite tenant key every business table declares, and
-- the 02.6.1-T2 lint reads. The surrogate PK already makes `id` globally unique;
-- this index is what lets a later table hold `foreign key (org_id, record_id)
-- references idempotency_record (org_id, id)` and what keeps the table inside the
-- repo-wide invariant that org-scoped rows are addressable as `(org_id, id)`.
create unique index if not exists idempotency_record_org_id_uq on public.idempotency_record (org_id, id);

-- Serves the purge scan: it filters `expires_at < now()` and walks the index in
-- that order (01.5.1 §4).
create index if not exists idempotency_expiry_idx on public.idempotency_record (expires_at);

drop trigger if exists idempotency_record_set_updated_at_and_version on public.idempotency_record;
create trigger idempotency_record_set_updated_at_and_version
  before update on public.idempotency_record
  for each row
  execute function public.set_updated_at_and_version();

alter table public.idempotency_record enable row level security;
alter table public.idempotency_record force row level security;

drop policy if exists idempotency_record_app_tenant on public.idempotency_record;
create policy idempotency_record_app_tenant on public.idempotency_record
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- No `TO evidgate_operator` policy on purpose (TL T76, D1): the operator's read
-- path is grant-gated by 02.8.1's `evidgate.operator_grant_ok` policy. With FORCE
-- and no operator policy the SELECT grant below reads zero rows until that grant
-- opens the scope.

-- The migration role needs its own policy: FORCE applies to the owner too, so the
-- purge function below would otherwise delete zero rows without failing.
drop policy if exists idempotency_record_owner_sweep on public.idempotency_record;
create policy idempotency_record_owner_sweep on public.idempotency_record
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- M1W3-05 / 00 §5.4.3: the app role inserts the begin row and updates it once
-- with the stored response; it never deletes one — expiry is the purge function's
-- job, and 0001's default privileges would otherwise have handed DELETE over.
grant select, insert, update on public.idempotency_record to evidgate_app;
revoke delete on public.idempotency_record from evidgate_app;
grant select on public.idempotency_record to evidgate_operator;
revoke all on public.idempotency_record from evidgate_worker_sched;

-- Deletes only rows whose retention has run out, oldest first, in bounded
-- batches: the caller (01.5.1-T6.2's scheduled task, running as
-- `evidgate_worker_sched`) passes the batch size and loops while a full batch came
-- back, so a large backlog can never hold one long transaction or one long lock.
-- `least(…, 10000)` caps the batch at the function itself, so a caller cannot turn
-- a "bounded batch" into an unbounded delete; a NULL or non-positive limit deletes
-- nothing. The `for update skip locked` batch is what lets a second worker run
-- concurrently without waiting or double-counting.
create or replace function evidgate.purge_expired_idempotency(p_limit integer) returns integer
  language sql
  security definer
  set search_path = ''
  as $$
    with batch as (
      select r.id
        from public.idempotency_record r
       where r.expires_at < now()
       order by r.expires_at
       limit least(greatest(p_limit, 0), 10000)
         for update skip locked
    ), removed as (
      delete from public.idempotency_record r
       using batch b
       where r.id = b.id
      returning r.id
    )
    select count(*)::integer from removed
  $$;

revoke all on function evidgate.purge_expired_idempotency(integer) from public;
grant execute on function evidgate.purge_expired_idempotency(integer) to evidgate_worker_sched;
