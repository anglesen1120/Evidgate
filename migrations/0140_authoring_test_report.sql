-- 0140_authoring_test_report.sql
-- Why: a test-case report must stay what it was when it was built: a later requirement edit, relink or case change
--   must not alter a report an owner already read or shared. The job reads the source once, canonicalizes it and
--   writes the whole snapshot in one insert; nothing ever edits it.
-- What: public.test_report (immutable, one row per job). `job_id` is the task's logical key, so a redelivered job
--   publishes one report (unique per org, project and job).
-- Order: after 0139; the migrator refuses a gap.
-- Lock: new table only; the lock_timeout below makes a blocked migration fail instead of queueing.
-- Purge order for the owner erasure path: test_report rows before the project or the membership they name. The app
--   role holds no DELETE, so only an owner-owned purge removes a row.
-- Rollback: stop the writer, then drop public.test_report and evidgate.test_report_immutable(); nothing else reads it.
set local lock_timeout = '5s';

create table if not exists public.test_report (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  job_id uuid not null,
  scope_kind text not null,
  selected_ids jsonb not null,
  sections jsonb not null,
  language text not null,
  schema_version integer not null,
  pinned_versions jsonb not null,
  payload jsonb not null,
  content_hash text not null,
  created_by uuid,
  created_at timestamptz not null default now(),
  -- 00 section 7 convention columns, inert here: no role may update a row.
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint test_report_pkey primary key (id),
  constraint test_report_org_id_key unique (org_id, id),
  constraint test_report_project_id_key unique (org_id, project_id, id),
  -- One report per job: a redelivered task inserts nothing the second time.
  constraint test_report_job_key unique (org_id, project_id, job_id),
  constraint test_report_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  -- Nullable author: a CI or agent principal has no membership row, and MATCH SIMPLE then leaves it unenforced.
  constraint test_report_created_by_fk foreign key (org_id, created_by)
    references public.membership (org_id, id),
  constraint test_report_scope_kind_check
    check (scope_kind in ('feature_nodes', 'requirements', 'manual_run', 'release')),
  constraint test_report_language_check check (language in ('vi', 'en')),
  constraint test_report_selected_ids_check
    check (jsonb_typeof(selected_ids) = 'array' and jsonb_array_length(selected_ids) between 1 and 1000),
  constraint test_report_sections_check
    check (jsonb_typeof(sections) = 'array' and jsonb_array_length(sections) between 1 and 16),
  constraint test_report_schema_version_check check (schema_version >= 1),
  constraint test_report_pinned_versions_check check (jsonb_typeof(pinned_versions) = 'object'),
  constraint test_report_payload_check check (jsonb_typeof(payload) = 'object'),
  -- The snapshot is a size outcome, never a truncation: the job refuses a larger one before it inserts.
  constraint test_report_payload_size_check check (octet_length(payload::text) <= 8388608),
  constraint test_report_content_hash_check check (content_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint test_report_version_check check (version >= 1)
);

-- The history read: newest first inside one project, keyset by (created_at, id).
create index if not exists test_report_project_created_idx
  on public.test_report (org_id, project_id, created_at desc, id desc);
create index if not exists test_report_created_by_idx
  on public.test_report (org_id, created_by)
  where created_by is not null;

drop trigger if exists test_report_set_updated_at_and_version on public.test_report;
create trigger test_report_set_updated_at_and_version
  before update on public.test_report
  for each row execute function public.set_updated_at_and_version();

-- A report is evidence of what was shown: UPDATE raises for every role, DELETE raises except for the owner's erasure path.
create or replace function evidgate.test_report_immutable() returns trigger
  language plpgsql
  as $$
  begin
    if tg_op = 'UPDATE' then
      raise exception 'test_report_immutable: a test report is append-only (UPDATE)'
        using errcode = '23514';
    elsif current_user <> 'evidgate_owner' then
      raise exception 'test_report_immutable: only the owner erasure path deletes a report (DELETE)'
        using errcode = '23514';
    end if;
    return old;
  end
  $$;

revoke all on function evidgate.test_report_immutable() from public;
grant execute on function evidgate.test_report_immutable() to evidgate_app;

drop trigger if exists test_report_immutable on public.test_report;
create trigger test_report_immutable
  before update or delete on public.test_report
  for each row execute function evidgate.test_report_immutable();

alter table public.test_report enable row level security;
alter table public.test_report force row level security;

drop policy if exists test_report_tenant on public.test_report;
create policy test_report_tenant on public.test_report
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists test_report_owner_backfill on public.test_report;
create policy test_report_owner_backfill on public.test_report
  for all to evidgate_owner using (true) with check (true);

-- 0001's default privileges give the app role full DML on a new table, so the revoke comes first. The insert is
-- column-scoped so a report cannot be backdated and its convention token stays at the default.
revoke insert, update, delete on public.test_report from evidgate_app;
grant select on public.test_report to evidgate_app;
grant insert (id, org_id, project_id, job_id, scope_kind, selected_ids, sections, language, schema_version,
              pinned_versions, payload, content_hash, created_by)
  on public.test_report to evidgate_app;
revoke all on public.test_report from evidgate_operator;
revoke all on public.test_report from evidgate_worker_sched;
