-- 0021_catalog_test_revision.sql
--
-- Owner: 03.1.3-T1.1. `test_revision`: every version of a test that was ever synced, stored append-only and
-- identified by (case_id, revision_hash) (feature plan 03.1.3 §4, 00 §7, DECISIONS A2). Same content gives the same
-- hash and the same row; any change is a new revision; a revision is never updated or deleted, so evidence (06/07) and
-- healing (08) always point at a reproducible input. Git is the content store: the row keeps hashes and a small
-- metadata object, never the test source (S3), so a secret cannot enter it.
--
-- revision_hash = sha256 over the RFC 8785 canonical JSON of {content_sha256, framework, language, assertion_hash,
-- metadata}; file_path is excluded so a rename-only commit reuses the revision (the application computes it).

create table if not exists public.test_revision (
  id uuid not null,
  org_id uuid not null,
  case_id uuid not null,
  revision_hash text not null,
  commit_sha text not null,
  file_path text not null,
  framework text not null,
  language text,
  content_sha256 text not null,
  assertion_hash text not null,
  metadata jsonb not null default '{}',
  created_at timestamptz not null default now(),
  constraint test_revision_pkey primary key (id),
  constraint test_revision_org_id_key unique (org_id, id),
  constraint test_revision_case_hash_key unique (org_id, case_id, revision_hash),
  constraint test_revision_case_fk foreign key (org_id, case_id) references public.test_case (org_id, id),
  constraint test_revision_hash_check check (revision_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint test_revision_content_hash_check check (content_sha256 ~ '^sha256:[0-9a-f]{64}$'),
  constraint test_revision_assertion_hash_check check (assertion_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint test_revision_commit_check check (commit_sha ~ '^[0-9a-f]{7,64}$'),
  constraint test_revision_path_check check (char_length(file_path) between 1 and 1024),
  constraint test_revision_framework_check check (char_length(framework) between 1 and 64),
  constraint test_revision_metadata_check check (jsonb_typeof(metadata) = 'object' and octet_length(metadata::text) <= 4096)
);

-- Serves GET /v1/cases/{c}/revisions newest first (P4): scanned backwards for `order by created_at desc, id desc`.
create index if not exists test_revision_case_idx
  on public.test_revision (org_id, case_id, created_at, id);

-- Append-only: the guard of 0020 raises for any UPDATE or DELETE, whoever issues it; the grants say the same to the app role.
create or replace function evidgate.catalog_revision_immutable() returns trigger
  language plpgsql
  as $$
  begin
    raise exception 'revision_immutable: a test revision is append-only (%)', tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.catalog_revision_immutable() from public;

drop trigger if exists test_revision_immutable on public.test_revision;
create trigger test_revision_immutable
  before update or delete on public.test_revision
  for each row execute function evidgate.catalog_revision_immutable();

alter table public.test_revision enable row level security;
alter table public.test_revision force row level security;

drop policy if exists test_revision_app_tenant on public.test_revision;
create policy test_revision_app_tenant on public.test_revision
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists test_revision_owner_backfill on public.test_revision;
create policy test_revision_owner_backfill on public.test_revision
  for all to evidgate_owner using (true) with check (true);

grant select, insert on public.test_revision to evidgate_app;
revoke update, delete on public.test_revision from evidgate_app;
grant select on public.test_revision to evidgate_operator;
revoke all on public.test_revision from evidgate_worker_sched;

-- Review of 0020 (03.1.1 performance review): `test_case_path_idx` served a lookup the sync never makes (it matches by
-- annotation_id), cost maintenance on every insert and path update, and a btree entry cannot hold a 1024-character
-- non-ASCII path. The owner foreign key, on the other hand, had no index for the membership side of its check.
drop index if exists public.test_case_path_idx;

create index if not exists test_case_owner_idx
  on public.test_case (org_id, owner_membership_id)
  where owner_membership_id is not null;
