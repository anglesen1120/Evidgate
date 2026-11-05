-- 0074_catalog_fixtures.sql
--
-- `fixture` is a project-scoped, named body of test data a run must create before it executes, and
-- `fixture_revision` is one immutable version of it. A revision pins what the executor may create —
-- where the body lives (`body_ref`, never the body itself), which version of the data it is
-- (`seed_version`), how long it may take, how it is cleaned up, and the resource prefix every object
-- it creates carries — content-hashed and superseded by `revision_no + 1`.
--
-- WHY TWO TABLES AND NOT ONE. A suite revision names a fixture by key and revision number, so the key
-- must be an identity of its own: with the key stored only on the version rows, `(key, revision_no)`
-- would be ambiguous as soon as one key's revisions are interleaved with a re-created fixture's, and
-- the reference check would have nothing stable to point at. `fixture` is that identity, never
-- updated; every version lives in `fixture_revision`.
--
-- WHY `fixture_key` IS DENORMALISED INTO THE REVISION. The revision carries its fixture's key so the
-- composite foreign key `(org_id, fixture_id, project_id, fixture_key)` can pin all three at once: no
-- row can name another org's fixture, another project's fixture, or a key that its fixture does not
-- hold. The alternative — reading the key through the join — would let a revision's stored key drift
-- from the fixture's, and the catalog would then answer a reference question with two answers.
--
-- WHY `body_ref` AND NOT THE BODY. A fixture body is customer test data, and the catalog stores
-- references only (S3): `body_ref` is a repository-relative path or an artifact id, capped and
-- refused when it carries whitespace (an inline body always does), and the content-hash of the
-- revision covers the pointer, so re-pointing a fixture is a new revision.
--
-- WHY `resource_prefix` IS STORED. The executor names every object it creates with it, so it must be
-- stable for a given revision and never chosen by a caller (a client-chosen prefix would let one
-- tenant's cleanup drop another's objects). The create path derives it from the fixture id and the
-- content hash, and the CHECK is the shape a derived value has.
--
-- IMMUTABILITY. Both tables are append-only for every role: UPDATE and DELETE raise 23514
-- `revision_immutable` from a BEFORE trigger, and the app role's grants leave it no UPDATE/DELETE to
-- attempt. A fixture with no revisions cannot be created, so there is always a head to point at.

create table if not exists public.fixture (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  fixture_key text not null,
  created_by uuid,
  created_at timestamptz not null default now(),
  constraint fixture_pkey primary key (id),
  constraint fixture_org_id_key unique (org_id, id),
  constraint fixture_project_key unique (org_id, project_id, fixture_key),
  -- The four-column key the revision's denormalised fixture_key references. Redundant beside
  -- `fixture_org_id_key` as a lookup index, but a foreign key can only name a unique key over exactly
  -- its own column list.
  constraint fixture_identity_key unique (org_id, id, project_id, fixture_key),
  constraint fixture_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint fixture_key_format_check check (fixture_key ~ '^[a-z0-9][a-z0-9-]{0,63}$')
);

create index if not exists fixture_list_idx on public.fixture (org_id, project_id, created_at, id);

create table if not exists public.fixture_revision (
  id uuid not null,
  org_id uuid not null,
  fixture_id uuid not null,
  project_id uuid not null,
  fixture_key text not null,
  revision_no integer not null,
  revision_hash text not null,
  seed_version text not null,
  timeout_s integer not null default 60,
  cleanup_policy text not null default 'drop',
  resource_prefix text not null,
  body_ref text not null,
  created_by uuid,
  created_at timestamptz not null default now(),
  constraint fixture_revision_pkey primary key (id),
  constraint fixture_revision_org_id_key unique (org_id, id),
  constraint fixture_revision_no_key unique (org_id, fixture_id, revision_no),
  constraint fixture_revision_hash_key unique (org_id, fixture_id, revision_hash),
  constraint fixture_revision_fixture_fk foreign key (org_id, fixture_id, project_id, fixture_key)
    references public.fixture (org_id, id, project_id, fixture_key),
  constraint fixture_revision_no_check check (revision_no >= 1),
  constraint fixture_revision_hash_check check (revision_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint fixture_revision_seed_check check (char_length(seed_version) between 1 and 64),
  constraint fixture_revision_timeout_check check (timeout_s between 1 and 86400),
  constraint fixture_revision_cleanup_check check (cleanup_policy in ('drop', 'truncate', 'none')),
  constraint fixture_revision_prefix_check check (resource_prefix ~ '^fx_[0-9a-f]{16}$'),
  constraint fixture_revision_body_ref_check check (char_length(body_ref) between 1 and 1024 and body_ref !~ '\s')
);

-- The head lookup and the reference check of a suite revision read one fixture's revisions newest first (P4).
create index if not exists fixture_revision_fixture_idx on public.fixture_revision (org_id, fixture_id, revision_no desc);

-- Immutability for every role. A fixture's identity and a revision's content never change: a change is a
-- new revision, which is what makes a pinned `(fixture_key, revision_no)` mean the same data forever.
create or replace function evidgate.catalog_fixture_immutable() returns trigger
  language plpgsql
  as $$
  begin
    raise exception 'revision_immutable: % is append-only (%)', tg_table_name, tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.catalog_fixture_immutable() from public;

drop trigger if exists fixture_immutable on public.fixture;
create trigger fixture_immutable
  before update or delete on public.fixture
  for each row execute function evidgate.catalog_fixture_immutable();

drop trigger if exists fixture_revision_immutable on public.fixture_revision;
create trigger fixture_revision_immutable
  before update or delete on public.fixture_revision
  for each row execute function evidgate.catalog_fixture_immutable();

-- RLS: the standard tenant policy plus the owner policy FORCE needs for backfills.
alter table public.fixture enable row level security;
alter table public.fixture force row level security;
alter table public.fixture_revision enable row level security;
alter table public.fixture_revision force row level security;

drop policy if exists fixture_app_tenant on public.fixture;
create policy fixture_app_tenant on public.fixture for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists fixture_owner_backfill on public.fixture;
create policy fixture_owner_backfill on public.fixture for all to evidgate_owner using (true) with check (true);

drop policy if exists fixture_revision_app_tenant on public.fixture_revision;
create policy fixture_revision_app_tenant on public.fixture_revision for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists fixture_revision_owner_backfill on public.fixture_revision;
create policy fixture_revision_owner_backfill on public.fixture_revision for all to evidgate_owner using (true) with check (true);

-- Grants: both tables are append-only, so the app role reads and inserts and nothing else. UPDATE and DELETE are
-- revoked explicitly, because the migration role's default privileges would otherwise leave them in place.
grant select, insert on public.fixture, public.fixture_revision to evidgate_app;
revoke update, delete on public.fixture, public.fixture_revision from evidgate_app;

grant select on public.fixture, public.fixture_revision to evidgate_operator;
revoke all on public.fixture, public.fixture_revision from evidgate_worker_sched;
