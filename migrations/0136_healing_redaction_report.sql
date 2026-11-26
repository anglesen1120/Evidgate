-- 0136_healing_redaction_report.sql
-- Why: every healing call to an AI provider is masked first, and the owner of the data must be able to prove what
--   was masked and that the report they see is the one the call was made with. The report holds rule ids, field
--   paths and counts only, never a value; its hash is stored on the call row.
-- What: public.healing_redaction_report (append-only, one row per call), the `blocked_leak` status of
--   healing_llm_call (a payload that still held a secret-shaped value after masking is not sent), a format check on
--   healing_llm_call.redaction_report_hash and the rule that a blocked call names its report.
-- Order: after 0135; the migrator refuses a gap, so 0133, 0134 and 0135 must land first.
-- Lock: the three ALTER ... ADD CONSTRAINT statements on healing_llm_call take ACCESS EXCLUSIVE and hold it until the
--   migrator's single transaction commits; NOT VALID then VALIDATE only shortens the scan, not the hold. The
--   table is the small attempt ledger, and the lock_timeout below makes a blocked migration fail instead of queueing.
-- No foreign key from candidate_id: the candidate table does not exist yet; the column is a plain uuid until it does.
-- Purge order for the owner erasure path: healing_redaction_report rows before the incident rows they name. The app
--   role holds no DELETE, so only an owner-owned purge function removes a row.
-- Rollback: drop the three constraints added below and re-add healing_llm_call_status_check without blocked_leak
--   (after deleting or recoding blocked_leak rows), then drop the table; nothing else reads it.
set local lock_timeout = '5s';

create table if not exists public.healing_redaction_report (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  incident_id uuid,
  candidate_id uuid,
  purpose text not null,
  rule_version text not null,
  report jsonb not null,
  report_hash text not null,
  created_at timestamptz not null default now(),
  -- 00 section 7 convention columns, inert here exactly as on healing_llm_call: no role may update a row.
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint healing_redaction_report_pkey primary key (id),
  constraint healing_redaction_report_org_id_key unique (org_id, id),
  constraint healing_redaction_report_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  -- Nullable incident: an authoring generation serves a project, never an incident (MATCH SIMPLE leaves it unenforced).
  constraint healing_redaction_report_incident_fk foreign key (org_id, incident_id)
    references public.incident (org_id, id),
  constraint healing_redaction_report_purpose_check
    check (purpose in ('authoring_generation', 'triage_hypothesis', 'candidate_generation')),
  constraint healing_redaction_report_rule_version_check check (char_length(rule_version) between 1 and 64),
  constraint healing_redaction_report_report_check check (jsonb_typeof(report) = 'object'),
  constraint healing_redaction_report_hash_check check (report_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint healing_redaction_report_version_check check (version >= 1)
);

-- The per-project read in creation order, and the lookup the composite incident key needs when an incident is purged.
create index if not exists healing_redaction_report_project_created_idx
  on public.healing_redaction_report (org_id, project_id, created_at, id);
create index if not exists healing_redaction_report_incident_idx
  on public.healing_redaction_report (org_id, incident_id)
  where incident_id is not null;

drop trigger if exists healing_redaction_report_set_updated_at_and_version on public.healing_redaction_report;
create trigger healing_redaction_report_set_updated_at_and_version
  before update on public.healing_redaction_report
  for each row execute function public.set_updated_at_and_version();

-- A report is evidence: UPDATE raises for every role, DELETE raises except for the owner's erasure path.
create or replace function evidgate.healing_redaction_report_immutable() returns trigger
  language plpgsql
  as $$
  begin
    if tg_op = 'UPDATE' then
      raise exception 'healing_redaction_report_immutable: a redaction report is append-only (UPDATE)'
        using errcode = '23514';
    elsif current_user <> 'evidgate_owner' then
      raise exception 'healing_redaction_report_immutable: only the owner erasure path deletes a report (DELETE)'
        using errcode = '23514';
    end if;
    return old;
  end
  $$;

revoke all on function evidgate.healing_redaction_report_immutable() from public;
grant execute on function evidgate.healing_redaction_report_immutable() to evidgate_app;

drop trigger if exists healing_redaction_report_immutable on public.healing_redaction_report;
create trigger healing_redaction_report_immutable
  before update or delete on public.healing_redaction_report
  for each row execute function evidgate.healing_redaction_report_immutable();

alter table public.healing_redaction_report enable row level security;
alter table public.healing_redaction_report force row level security;

drop policy if exists healing_redaction_report_tenant on public.healing_redaction_report;
create policy healing_redaction_report_tenant on public.healing_redaction_report
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists healing_redaction_report_owner_backfill on public.healing_redaction_report;
create policy healing_redaction_report_owner_backfill on public.healing_redaction_report
  for all to evidgate_owner using (true) with check (true);

-- 0001's default privileges give the app role full DML on a new table, so the revoke comes first. The insert is
-- column-scoped so a report cannot be backdated and its convention token stays at the default.
revoke insert, update, delete on public.healing_redaction_report from evidgate_app;
grant select on public.healing_redaction_report to evidgate_app;
grant insert (id, org_id, project_id, incident_id, candidate_id, purpose, rule_version, report, report_hash)
  on public.healing_redaction_report to evidgate_app;
revoke all on public.healing_redaction_report from evidgate_operator;
revoke all on public.healing_redaction_report from evidgate_worker_sched;

-- The attempt ledger: one more status, a hash that is always a sha256 digest, and a blocked call that names its report.
alter table public.healing_llm_call drop constraint if exists healing_llm_call_status_check;
alter table public.healing_llm_call
  add constraint healing_llm_call_status_check
  check (status in ('ok', 'timeout', 'llm_schema_violation', 'budget_exceeded', 'error', 'blocked_leak')) not valid;
alter table public.healing_llm_call validate constraint healing_llm_call_status_check;

alter table public.healing_llm_call drop constraint if exists healing_llm_call_report_hash_check;
alter table public.healing_llm_call
  add constraint healing_llm_call_report_hash_check
  check (redaction_report_hash is null or redaction_report_hash ~ '^sha256:[0-9a-f]{64}$') not valid;
alter table public.healing_llm_call validate constraint healing_llm_call_report_hash_check;

alter table public.healing_llm_call drop constraint if exists healing_llm_call_blocked_report_check;
alter table public.healing_llm_call
  add constraint healing_llm_call_blocked_report_check
  check (status <> 'blocked_leak' or redaction_report_hash is not null) not valid;
alter table public.healing_llm_call validate constraint healing_llm_call_blocked_report_check;
