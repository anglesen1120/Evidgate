-- 0133_authoring_intake.sql
-- Why: requirement intake. A reviewer hands the generation pipeline free text, a DOCX or PDF document, or
--   an image; the row records what arrived, the extraction state, and the extracted text the later
--   generation steps read. The text is test data supplied by the customer and is kept until the project is
--   purged.
-- What: public.authoring_intake and public.authoring_intake_slot (the per-tenant extraction slots), their
--   guards, FORCE RLS policies and column-scoped grants; the artifact kinds `requirement_document` and
--   `requirement_image` on the artifact and policy checks; artifact (org_id, project_id, id) as a
--   same-project FK target.
-- Order: after 0131; the migrator refuses a gap, so 0132 must land first.
set local lock_timeout = '5s';

-- The widened checks are added NOT VALID and validated afterwards, so the artifact scan holds only a
-- SHARE UPDATE EXCLUSIVE lock instead of blocking writers for the whole pass.
alter table public.artifact
  drop constraint if exists artifact_kind_check,
  add constraint artifact_kind_check check (kind in (
    'trace', 'video', 'screenshot', 'raw_report', 'dom_snapshot', 'a11y_snapshot', 'patch', 'ui_hierarchy',
    'requirement_document', 'requirement_image')) not valid;
alter table public.artifact validate constraint artifact_kind_check;

alter table public.artifact_kind_policy
  drop constraint if exists artifact_kind_policy_kind_check,
  add constraint artifact_kind_policy_kind_check check (kind in (
    'trace', 'video', 'screenshot', 'raw_report', 'dom_snapshot', 'a11y_snapshot', 'patch', 'ui_hierarchy',
    'requirement_document', 'requirement_image')) not valid;
alter table public.artifact_kind_policy validate constraint artifact_kind_policy_kind_check;

alter table public.redaction_rule
  drop constraint if exists redaction_rule_kind_check,
  add constraint redaction_rule_kind_check check (kind in (
    'trace', 'video', 'screenshot', 'raw_report', 'dom_snapshot', 'a11y_snapshot', 'patch', 'ui_hierarchy',
    'requirement_document', 'requirement_image')) not valid;
alter table public.redaction_rule validate constraint redaction_rule_kind_check;

-- The intake points at its uploaded artifact through (org_id, project_id, id), so a row cannot name
-- another project's artifact. The unique index build blocks artifact writes until this migration commits.
do $$
begin
  if not exists (select 1 from pg_constraint where conname = 'artifact_org_project_id_key' and conrelid = 'public.artifact'::regclass) then
    alter table public.artifact
      add constraint artifact_org_project_id_key unique (org_id, project_id, id);
  end if;
end
$$;

create table if not exists public.authoring_intake (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  feature_node_id uuid,
  kind text not null,
  artifact_id uuid,
  input_text text,
  media_type text not null,
  declared_bytes bigint not null,
  extracted_text text,
  extracted_text_hash text,
  caps jsonb not null,
  state text not null default 'received',
  failure_code text,
  created_by uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint authoring_intake_pkey primary key (id),
  constraint authoring_intake_org_id_key unique (org_id, id),
  constraint authoring_intake_org_project_id_key unique (org_id, project_id, id),
  -- One intake per uploaded artifact: a replayed completion cannot start a second extraction.
  constraint authoring_intake_artifact_key unique (org_id, artifact_id),
  constraint authoring_intake_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint authoring_intake_feature_node_fk foreign key (org_id, project_id, feature_node_id)
    references public.feature_node (org_id, project_id, id),
  constraint authoring_intake_artifact_fk foreign key (org_id, project_id, artifact_id)
    references public.artifact (org_id, project_id, id),
  -- Nullable creator: a service credential has no membership row, and MATCH SIMPLE leaves the key
  -- unenforced then (the authoring_version precedent).
  constraint authoring_intake_created_by_fk foreign key (org_id, created_by) references public.membership (org_id, id),
  constraint authoring_intake_kind_check check (kind in ('text', 'docx', 'pdf', 'image')),
  constraint authoring_intake_state_check check (state in ('received', 'extracting', 'ready', 'rejected')),
  constraint authoring_intake_failure_code_check check (failure_code is null or failure_code in (
    'container_mismatch', 'macro_present', 'encryption_unsupported', 'external_reference_present',
    'archive_limit_exceeded', 'page_limit_exceeded', 'char_limit_exceeded', 'no_extractable_text',
    'extraction_timeout', 'extraction_failed')),
  -- Text carries its body inline; every other kind carries an uploaded artifact and no inline text.
  constraint authoring_intake_source_check check (
    (kind = 'text' and artifact_id is null and input_text is not null)
    or (kind <> 'text' and artifact_id is not null and input_text is null)),
  -- An image feeds the generator as an attachment, so it names no feature node and never has text.
  constraint authoring_intake_feature_node_check check ((kind = 'image') = (feature_node_id is null)),
  constraint authoring_intake_media_type_check check (
    case kind
      when 'text' then media_type = 'text/plain'
      when 'docx' then media_type = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
      when 'pdf' then media_type = 'application/pdf'
      else media_type in ('image/png', 'image/jpeg', 'image/webp')
    end),
  constraint authoring_intake_input_text_check check (input_text is null or char_length(input_text) between 1 and 1048576),
  constraint authoring_intake_declared_bytes_check check (declared_bytes > 0),
  constraint authoring_intake_extracted_text_check check (
    extracted_text is null or char_length(extracted_text) between 1 and 2000000),
  constraint authoring_intake_extracted_text_hash_check check (
    (extracted_text is null) = (extracted_text_hash is null)
    and (extracted_text_hash is null or extracted_text_hash ~ '^sha256:[0-9a-f]{64}$')),
  constraint authoring_intake_image_text_check check (kind <> 'image' or extracted_text is null),
  -- ready needs its text (except an image); rejected names its reason and keeps no text.
  constraint authoring_intake_result_check check (
    case state
      when 'ready' then failure_code is null and (kind = 'image' or extracted_text is not null)
      when 'rejected' then failure_code is not null and extracted_text is null
      else failure_code is null and extracted_text is null
    end),
  constraint authoring_intake_caps_check check (jsonb_typeof(caps) = 'object' and octet_length(caps::text) <= 4096),
  constraint authoring_intake_version_check check (version >= 1)
);

-- The list keyset `(created_at desc, id desc)` of one project, scanned backwards.
create index if not exists authoring_intake_project_created_idx
  on public.authoring_intake (org_id, project_id, created_at desc, id desc);

-- Serves the composite FK's check when a feature node is removed, and reads through the node.
create index if not exists authoring_intake_feature_node_idx
  on public.authoring_intake (org_id, project_id, feature_node_id)
  where feature_node_id is not null;

-- The queue position of a received or extracting intake: the unfinished rows of one tenant in creation order, read
-- as a bounded index range, so the cost follows the unfinished backlog and never the history of finished rows.
create index if not exists authoring_intake_pending_created_idx
  on public.authoring_intake (org_id, created_at, id)
  where state in ('received', 'extracting');

-- Extraction slots: at most one row per (org, slot number), so the number of live rows is the tenant's
-- concurrency. A crashed worker's slot is reclaimed once its lease has expired.
create table if not exists public.authoring_intake_slot (
  id uuid not null,
  org_id uuid not null,
  slot integer not null,
  intake_id uuid not null,
  lease_id uuid not null,
  expires_at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint authoring_intake_slot_pkey primary key (id),
  constraint authoring_intake_slot_org_id_key unique (org_id, id),
  constraint authoring_intake_slot_number_key unique (org_id, slot),
  constraint authoring_intake_slot_intake_key unique (org_id, intake_id),
  constraint authoring_intake_slot_intake_fk foreign key (org_id, intake_id)
    references public.authoring_intake (org_id, id),
  constraint authoring_intake_slot_number_check check (slot between 0 and 63),
  constraint authoring_intake_slot_version_check check (version >= 1)
);

drop trigger if exists authoring_intake_set_updated_at_and_version on public.authoring_intake;
create trigger authoring_intake_set_updated_at_and_version
  before update on public.authoring_intake
  for each row execute function public.set_updated_at_and_version();

drop trigger if exists authoring_intake_slot_set_updated_at_and_version on public.authoring_intake_slot;
create trigger authoring_intake_slot_set_updated_at_and_version
  before update on public.authoring_intake_slot
  for each row execute function public.set_updated_at_and_version();

-- One guard for every role, the owner included: the row is created `received`, only the extraction
-- columns move, and the state follows the table below. DELETE is the owner erasure path alone
-- (`current_user = 'evidgate_owner'`, the authoring_version shape).
--   received   -> extracting | rejected | ready (text and image only: nothing to extract)
--   extracting -> extracting (a re-driven job) | ready | rejected
--   ready, rejected: frozen, a write that changes nothing is let through
create or replace function evidgate.authoring_intake_immutable() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      if current_user <> 'evidgate_owner' then
        raise exception 'authoring_intake_immutable: only the owner erasure path deletes an intake (DELETE)'
          using errcode = '23514';
      end if;
      return old;
    end if;

    if tg_op = 'INSERT' then
      if new.state <> 'received' or new.failure_code is not null
         or new.extracted_text is not null or new.extracted_text_hash is not null then
        raise exception 'authoring_intake_state_invalid: an intake is created received and empty (INSERT)'
          using errcode = '23514';
      end if;
      return new;
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.feature_node_id is distinct from old.feature_node_id
       or new.kind is distinct from old.kind
       or new.artifact_id is distinct from old.artifact_id
       or new.input_text is distinct from old.input_text
       or new.media_type is distinct from old.media_type
       or new.declared_bytes is distinct from old.declared_bytes
       or new.created_by is distinct from old.created_by
       or new.caps is distinct from old.caps
       or new.created_at is distinct from old.created_at then
      raise exception 'authoring_intake_immutable: what was submitted never changes (UPDATE)'
        using errcode = '23514';
    end if;

    if new.state is not distinct from old.state
       and new.extracted_text is not distinct from old.extracted_text
       and new.extracted_text_hash is not distinct from old.extracted_text_hash
       and new.failure_code is not distinct from old.failure_code then
      return new;
    end if;

    if old.state in ('ready', 'rejected') then
      raise exception 'authoring_intake_state_invalid: a % intake never changes', old.state
        using errcode = '23514';
    end if;

    if not (   (old.state = 'received' and new.state in ('received', 'extracting', 'rejected'))
            or (old.state = 'received' and new.state = 'ready' and old.kind in ('text', 'image'))
            or (old.state = 'extracting' and new.state in ('extracting', 'ready', 'rejected'))) then
      raise exception 'authoring_intake_state_invalid: % -> % for a % intake', old.state, new.state, old.kind
        using errcode = '23514';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.authoring_intake_immutable() from public;
-- The trigger fires for whoever wrote the row, so the app role needs EXECUTE exactly as it does on the
-- authoring_version guard.
grant execute on function evidgate.authoring_intake_immutable() to evidgate_app;

drop trigger if exists authoring_intake_immutable on public.authoring_intake;
create trigger authoring_intake_immutable
  before insert or update or delete on public.authoring_intake
  for each row execute function evidgate.authoring_intake_immutable();

alter table public.authoring_intake enable row level security;
alter table public.authoring_intake force row level security;

drop policy if exists authoring_intake_app_tenant on public.authoring_intake;
create policy authoring_intake_app_tenant on public.authoring_intake
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists authoring_intake_owner_backfill on public.authoring_intake;
create policy authoring_intake_owner_backfill on public.authoring_intake
  for all to evidgate_owner using (true) with check (true);

alter table public.authoring_intake_slot enable row level security;
alter table public.authoring_intake_slot force row level security;

drop policy if exists authoring_intake_slot_app_tenant on public.authoring_intake_slot;
create policy authoring_intake_slot_app_tenant on public.authoring_intake_slot
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists authoring_intake_slot_owner_backfill on public.authoring_intake_slot;
create policy authoring_intake_slot_owner_backfill on public.authoring_intake_slot
  for all to evidgate_owner using (true) with check (true);

-- The extracted text is customer test data: no operator or scheduler read, and the app role writes only
-- the request columns on insert and the extraction columns on update. The caps snapshot is written once at
-- insert and never updated, so no role can loosen the limits a stored intake runs under.
revoke all on public.authoring_intake from evidgate_app, evidgate_operator, evidgate_worker_sched;
grant select on public.authoring_intake to evidgate_app;
grant insert (id, org_id, project_id, feature_node_id, kind, artifact_id, input_text, media_type, declared_bytes, caps, created_by)
  on public.authoring_intake to evidgate_app;
grant update (state, extracted_text, extracted_text_hash, failure_code)
  on public.authoring_intake to evidgate_app;

revoke all on public.authoring_intake_slot from evidgate_app, evidgate_operator, evidgate_worker_sched;
grant select, delete on public.authoring_intake_slot to evidgate_app;
grant insert (id, org_id, slot, intake_id, lease_id, expires_at) on public.authoring_intake_slot to evidgate_app;
-- A slot is taken, renewed and released, never moved: only the lease expiry is updatable, so a holder cannot
-- change the slot number or the lease id it was given.
grant update (expires_at) on public.authoring_intake_slot to evidgate_app;
