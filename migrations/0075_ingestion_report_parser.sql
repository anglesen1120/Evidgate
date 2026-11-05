-- 0075_ingestion_report_parser.sql
--
-- WHY: the JUnit mapper is chosen by data, not by an `if`. A raw report names its `producer_kind` (pytest's
-- `junitxml`, and later one dialect per producer), and the mapping from that kind to a `parser_id` + `version` is a
-- registry row. Seeding the first row here is what makes a parser release a migration: a new version is a new row, and
-- the events it produces stay attributable to the version that produced them (D4).
--
-- SHAPE: `report_parser` is a catalogue table on the T3 platform exception list (contracts/rls-exceptions.txt). A
-- built-in row carries `org_id IS NULL` and is read by every tenant; a project-owned row (a later story's override)
-- carries its own org and is visible only inside it. `unique (parser_id, version)` makes one parser release one row,
-- so a seed or a reload can never fork a version into two mappings. `unique (org_id, id)` is the surrogate key every
-- table of this schema declares, kept even though a built-in row's org is null.
--
-- WHY `parser_id` IS THE REGISTRY KEY AND `Version` THE DISCRIMINATOR: a reprocess under a newer parser must add
-- events beside the old ones, never overwrite them, so the pair travels onto the parsed events as
-- `ext.parser = "<parser_id>/<version>"`. The resolver reads the pair for a producer kind; it never interprets
-- `notes` or `schema_version`, which are the mapping document's annotations.
--
-- GRANTS: `evidgate_app` reads only. The registry is written by the migration and by the owner role — a tenant must
-- never be able to point a producer kind at a parser of its own naming. `evidgate_operator` keeps SELECT with no
-- operator policy (inert until the grant-gated form lands, the 0071/0073 shape). `evidgate_worker_sched` holds
-- nothing: no scheduled job touches configuration.
--
-- ROW SECURITY: ENABLE + FORCE with the documented all-org read policy for `evidgate_app` and the owner backfill
-- policy. The app role holds no write grant, so a write is refused by the privilege rather than only by the policy.
--
-- LOCKS and ORDER: a catalogue write on a new relation; `lock_timeout` bounds the DDL's wait. Additive, so it applies
-- before the mapper that reads it. No down migration: dropping the table would lose which parser version produced
-- stored evidence.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`, `drop policy if exists`
-- before every policy, a re-stated `revoke`/`grant`, and a seed guarded by the unique key.

set local lock_timeout = '5s';

create table if not exists public.report_parser (
  id uuid not null,
  -- NULL is a built-in (platform) parser; a non-null value is a project-owned override.
  org_id uuid,
  parser_id text not null,
  version text not null,
  -- The producer kind a raw report declares (`producer_kind`), e.g. `pytest-junitxml`.
  producer_kind text not null,
  schema_version integer not null default 1,
  notes text,
  created_at timestamptz not null default now(),
  constraint report_parser_pkey primary key (id),
  constraint report_parser_org_id_key unique (org_id, id),
  constraint report_parser_parser_version_key unique (parser_id, version),
  constraint report_parser_parser_id_check check (
    char_length(parser_id) between 1 and 64 and parser_id ~ '^[a-z][a-z0-9-]*$'),
  -- A version is a label, not a number: `1.0.0` today, a pre-release suffix from a future parser is still a label.
  constraint report_parser_version_check check (
    char_length(version) between 1 and 64 and version ~ '^[0-9A-Za-z][0-9A-Za-z.+_-]*$'),
  constraint report_parser_producer_kind_check check (
    char_length(producer_kind) between 1 and 64 and producer_kind ~ '^[a-z][a-z0-9-]*$'),
  constraint report_parser_schema_version_check check (schema_version between 1 and 1000),
  constraint report_parser_notes_check check (notes is null or char_length(notes) between 1 and 1024)
);

-- The resolver's one read: producer kind -> parser pair. A kind may have several versions (a reprocess target), so
-- the index is not unique; the handler takes the highest version by the registry's own ordering rule.
create index if not exists report_parser_producer_kind on public.report_parser (producer_kind);

alter table public.report_parser enable row level security;
alter table public.report_parser force row level security;

drop policy if exists report_parser_app_read on public.report_parser;
create policy report_parser_app_read on public.report_parser
  for select to evidgate_app
  using (org_id is null or org_id = evidgate.current_org_id());

-- FORCE row level security applies to the owner too, so the migration role needs its own policy (0004/0071 shape).
drop policy if exists report_parser_owner_backfill on public.report_parser;
create policy report_parser_owner_backfill on public.report_parser
  for all to evidgate_owner using (true) with check (true);

-- The default privileges of 0001 hand `evidgate_app` full DML, so the write grants are revoked explicitly before the
-- read grant: a tenant must not be able to register a parser mapping.
revoke insert, update, delete on public.report_parser from evidgate_app;
grant select on public.report_parser to evidgate_app;
grant select on public.report_parser to evidgate_operator;
revoke all on public.report_parser from evidgate_worker_sched;

-- The first parser release. The id is fixed so a re-run and a database reset converge on the same row.
insert into public.report_parser (id, org_id, parser_id, version, producer_kind, schema_version, notes)
values (
  '018f8f5e-0000-7000-8000-000000000075'::uuid,
  null,
  'junit-pytest',
  '1.0.0',
  'pytest-junitxml',
  1,
  'pytest junitxml: testcase -> test.finished, classname/name identity hint, declared vs observed counts')
on conflict (parser_id, version) do nothing;
