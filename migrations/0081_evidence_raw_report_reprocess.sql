-- 0081_evidence_raw_report_reprocess.sql
--
-- WHY: a reprocess has to outlive the request that asked for it. The parse itself runs on the task queue, minutes
-- after the route answered 202, and the row the caller was given must name work that will exist — so the request is
-- stored here first, and the task that does it commits in the same transaction. `raw_report` cannot carry it: that
-- table is append-only and a row there is only ever written once the parser has judged the bytes, so it has no
-- pending state a request could point at.
--
-- SHAPE: one row per (report artifact, parser release), enforced by `unique (org_id, artifact_id, parser_id,
-- parser_version)`; the handler's insert is `on conflict ... do nothing`, so a second identical request records
-- nothing new and queues no second task. `id` is the answer's `parsing_id` and is carried onto the `raw_report` row
-- the task writes when the parse finishes, which is why it is minted here rather than by the task.
--
-- SOURCE: `source_raw_report_id` is the row the caller asked to reprocess, referenced through the tenant pair
-- `(org_id, id) -> raw_report (org_id, id)`: a request can only ever name a report of its own org, so a cross-tenant
-- report id inserts nothing instead of overriding another org's evidence.
--
-- RUN PAIRING: a report artifact belongs to a run. Referencing the artifact through `(org_id, id)` alone would accept
-- a request naming run A and an artifact of run B of the same org, and the task would write events under the wrong
-- run. The reference therefore names the triple `(org_id, artifact_id, run_id) -> artifact (org_id, id, run_id)`, the
-- key the report table itself is paired through.
--
-- PARSER REFERENCE: `report_parser` carries a null org for a built-in release, so a composite tenant FK on
-- `(org_id, parser_id)` could never match one; the reference is the catalogue's own `unique (parser_id, version)`
-- key. That key sees the full catalogue, so the FK alone would accept a project-owned release of another org: the
-- `raw_report_reprocess_parser_visible` trigger answers that case, reading the catalogue as the caller so row level
-- security (which hides the foreign row from the tenant, not from the owner) and the explicit org predicate agree.
--
-- IMMUTABILITY: for every role. UPDATE and DELETE raise 23514 `raw_report_reprocess_immutable` from a BEFORE trigger,
-- and the app role's grants leave it no UPDATE/DELETE/TRUNCATE to attempt. A request is a fact: which report, which
-- release, which run, and never rewritten after the task may already have read it.
--
-- RLS: ENABLE + FORCE with `to evidgate_app` over `evidgate.current_org_id()`; the owner policy exists because FORCE
-- applies to the migration role too. `evidgate_operator` keeps its default SELECT with no operator policy (inert
-- until the grant-gated form lands); the scheduler role holds nothing.
--
-- LOCKS: a catalogue write on the new relation, plus no lock on an existing one: every key it references already
-- exists. `lock_timeout` bounds the DDL's wait and turns a busy database into a failed migration the operator
-- retries, rather than a stall. Additive, applies before the route that writes it. No down migration: dropping the
-- table would lose which reprocesses were accepted.
--
-- Idempotent in the shape the README's file contract allows.

set local lock_timeout = '5s';

create table if not exists public.raw_report_reprocess (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  artifact_id uuid not null,
  source_raw_report_id uuid not null,
  parser_id text not null,
  parser_version text not null,
  created_at timestamptz not null default now(),
  constraint raw_report_reprocess_pkey primary key (id),
  constraint raw_report_reprocess_org_id_key unique (org_id, id),
  -- One request per report and release: a repeat conflicts here and writes nothing.
  constraint raw_report_reprocess_natural_key unique (org_id, artifact_id, parser_id, parser_version),
  constraint raw_report_reprocess_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint raw_report_reprocess_artifact_fk foreign key (org_id, artifact_id, run_id)
    references public.artifact (org_id, id, run_id),
  constraint raw_report_reprocess_source_fk foreign key (org_id, source_raw_report_id)
    references public.raw_report (org_id, id),
  constraint raw_report_reprocess_parser_fk foreign key (parser_id, parser_version)
    references public.report_parser (parser_id, version)
);

create or replace function evidgate.raw_report_reprocess_immutable() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    raise exception 'raw_report_reprocess_immutable: % is append-only (%)', tg_table_name, tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.raw_report_reprocess_immutable() from public;

drop trigger if exists raw_report_reprocess_immutable on public.raw_report_reprocess;
create trigger raw_report_reprocess_immutable
  before update or delete on public.raw_report_reprocess
  for each row execute function evidgate.raw_report_reprocess_immutable();

-- A project-owned `report_parser` row is visible only inside its own org, but the foreign key above compares the
-- release key alone, and a foreign key check bypasses row level security. The guard therefore reads the catalogue as
-- the caller: the tenant's policy hides another org's release, and the owner's backfill policy shows the whole table
-- to the org predicate. An insert is the only event that can name a release; updates raise the immutability trigger.
create or replace function evidgate.raw_report_reprocess_parser_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
  as $$
  begin
    if not exists (
      select 1
        from public.report_parser p
       where p.parser_id = new.parser_id
         and p.version = new.parser_version
         and (p.org_id is null or p.org_id = new.org_id))
    then
      raise exception 'raw_report_reprocess_parser_invisible: parser % / % is not a release visible to org %',
          new.parser_id, new.parser_version, new.org_id
        using errcode = '23514', constraint = 'raw_report_reprocess_parser_invisible';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.raw_report_reprocess_parser_guard() from public;

drop trigger if exists raw_report_reprocess_parser_visible on public.raw_report_reprocess;
create trigger raw_report_reprocess_parser_visible
  before insert on public.raw_report_reprocess
  for each row execute function evidgate.raw_report_reprocess_parser_guard();

alter table public.raw_report_reprocess enable row level security;
alter table public.raw_report_reprocess force row level security;

drop policy if exists raw_report_reprocess_app_tenant on public.raw_report_reprocess;
create policy raw_report_reprocess_app_tenant on public.raw_report_reprocess
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists raw_report_reprocess_owner_backfill on public.raw_report_reprocess;
create policy raw_report_reprocess_owner_backfill on public.raw_report_reprocess
  for all to evidgate_owner using (true) with check (true);

-- Grants: append-only, so the app role reads and inserts and nothing else; UPDATE and DELETE are revoked explicitly,
-- because the migration role's default privileges would otherwise leave them in place.
grant select, insert on public.raw_report_reprocess to evidgate_app;
revoke update, delete, truncate on public.raw_report_reprocess from evidgate_app;
grant select on public.raw_report_reprocess to evidgate_operator;
revoke all on public.raw_report_reprocess from evidgate_worker_sched;
