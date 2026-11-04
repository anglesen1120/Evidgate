-- 0066_evidence_masking_event.sql
--
-- WHY: the masking scan of an uploaded artifact has to leave a tenant-visible trace of what it found, and that trace
-- must be a row rather than only a column on the artifact: an artifact is scanned once, a row per pattern class says
-- which classes matched and how often, and the security-event review reads them without a second scan of the bytes.
-- The matched secret value itself is never stored — the class and the count are the whole row.
--
-- SHAPE: append-only, one row per (artifact, pattern class). `unique (org_id, artifact_id, pattern_class)` is what
-- makes a replayed scan write no second event: the scan's insert is `on conflict do nothing` on exactly this key.
-- `action` is the scan's final decision for that class — `flagged` when the artifact was only classified, `rejected`
-- when the project's `reject_on_secret` moved the artifact out of evidence — so one class has one row whatever the
-- scan's outcome, and a scan that is replayed after a rejection cannot add a second, disagreeing row.
-- `(org_id, artifact_id) -> artifact (org_id, id)` is the composite tenant FK (D3): an event can only ever name an
-- artifact of its own org.
--
-- CHECKS: `pattern_class` is the scanner's closed code list (a new class is a migration, like every other enum-like
-- text column, D4) and `hit_count > 0` keeps a row without a finding out of the table. No CHECK on the count's upper
-- bound: the scanner counts what it matched in one object, whose size the kind policy's cap already bounds.
--
-- RLS: ENABLE + FORCE with `to evidgate_app` over `evidgate.current_org_id()` (never raw `current_setting`): another
-- org's events are not rows at all, so a scan running under the wrong tenant writes nothing. The owner policy exists
-- because FORCE row level security applies to the migration role too (0004/0028/0062 precedent); BYPASSRLS is
-- rejected by the platform's security decisions.
--
-- GRANTS: append-only for the app role. 0001's default privileges grant it full DML on every table created in
-- `public`, so the revoke below removes the three statements that could rewrite or erase history (0015/0031/0053
-- precedent); SELECT and INSERT survive. `evidgate_operator` keeps its default SELECT — a decision recorded here, not
-- a grant: it holds no policy on this table, so until 02.8.1's grant-gated operator policy lands it reads zero rows
-- (0028's posture). The scheduler role holds nothing.
--
-- GUARD: the scan is the first writer of `sensitivity`, so this migration adds the `artifact_scan_guard` trigger
-- (D2): `sensitive` never returns to `normal`, and a decided `scan_state` never changes again — neither back to
-- `pending` nor from one verdict to the other, whatever role writes. Raising sensitivity and moving `pending` to a
-- verdict stay allowed (and so does re-writing the value a row already holds), and the trigger only judges those two
-- columns, so the lifecycle writes 0028 grants are untouched.
--
-- LOCKS: `set local lock_timeout` bounds the DDL's waits and turns a busy table into a failed migration the operator
-- retries, rather than a stall.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists` / `drop ... if exists` so a
-- re-run is a no-op.

set local lock_timeout = '5s';

create table if not exists public.masking_event (
  id uuid not null,
  org_id uuid not null,
  artifact_id uuid not null,
  pattern_class text not null,
  hit_count integer not null,
  action text not null,
  created_at timestamptz not null default now(),
  constraint masking_event_pkey primary key (id),
  constraint masking_event_org_id_key unique (org_id, id),
  constraint masking_event_artifact_pattern_key unique (org_id, artifact_id, pattern_class),
  constraint masking_event_artifact_fk foreign key (org_id, artifact_id) references public.artifact (org_id, id),
  constraint masking_event_pattern_class_check
    check (pattern_class in ('aws_access_key', 'private_key_pem', 'generic_bearer_token')),
  constraint masking_event_hit_count_check check (hit_count > 0),
  constraint masking_event_action_check check (action in ('flagged', 'rejected'))
);

-- The events of one artifact: the read the artifact detail and the security-event review make, and the referencing
-- side of the composite artifact FK (0024/0028 precedent), so a tenant or artifact delete never scans the table.
create index if not exists masking_event_artifact_idx on public.masking_event (org_id, artifact_id);

alter table public.masking_event enable row level security;
alter table public.masking_event force row level security;

drop policy if exists masking_event_app_tenant on public.masking_event;
create policy masking_event_app_tenant on public.masking_event
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy (TL T76, D1): the grant-gated operator policy is 02.8.1's
-- `evidgate.operator_grant_ok` form; until it lands the operator's default SELECT reads zero rows.
drop policy if exists masking_event_owner_backfill on public.masking_event;
create policy masking_event_owner_backfill on public.masking_event
  for all to evidgate_owner using (true) with check (true);

-- Append-only in SQL: the scan inserts a row per class once and nothing rewrites or removes one, so the read side
-- cannot lose or rewrite a finding after the fact (0015/0031/0053 precedent).
revoke update, delete, truncate on public.masking_event from evidgate_app;
grant select on public.masking_event to evidgate_operator;
revoke all on public.masking_event from evidgate_worker_sched;

-- The two columns the app may now move encode one-way facts: a secret found once stays found, and a decided scan is
-- neither re-queued nor re-decided. The guard is an invoker, so it refuses an owner write too without needing a
-- definer grant, and its name sorts before `artifact_set_updated_at_and_version`, so a refused update never bumps the
-- bookkeeping.
create or replace function evidgate.artifact_scan_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if old.sensitivity = 'sensitive' and new.sensitivity = 'normal' then
      raise exception 'artifact_sensitivity_invalid: sensitive cannot become normal'
        using errcode = '23514';
    end if;

    -- A verdict is a fact about the bytes, so a decided row keeps it: `pending` is the only state that may leave
    -- itself, and only for a verdict. Re-writing the value a row already holds is not a change and stays allowed.
    if old.scan_state in ('clean', 'flagged') and new.scan_state is distinct from old.scan_state then
      raise exception 'artifact_scan_state_invalid: a completed scan cannot change'
        using errcode = '23514';
    end if;

    return new;
  end;
  $$;

revoke all on function evidgate.artifact_scan_guard() from public;

-- The migration role owns it; stated so a re-run over a function some other role once created ends owner-owned too.
alter function evidgate.artifact_scan_guard() owner to evidgate_owner;

drop trigger if exists artifact_scan_guard on public.artifact;
create trigger artifact_scan_guard
  before update on public.artifact
  for each row execute function evidgate.artifact_scan_guard();

-- The existing lifecycle-column grants exclude sensitivity; the scan needs only this additional column.
grant update (sensitivity) on public.artifact to evidgate_app;
