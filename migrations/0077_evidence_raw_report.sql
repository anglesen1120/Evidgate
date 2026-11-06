-- 0077_evidence_raw_report.sql
--
-- WHY: a parsed report has to leave a tenant-visible record of what the parser decided about it, separate from the
-- events it produced: which parser release read it, what the document declared, what the mapper observed and why the
-- two differ. A refused document (malformed, unsupported producer) produces no event at all, so without this row the
-- refusal would be invisible and a reviewer could not tell "never parsed" from "parsed and refused". The row is also
-- what the comparison read joins against the events, so a count mismatch is explainable without re-parsing the bytes.
--
-- SHAPE: append-only, one row per (report artifact, parser release). The artifact is immutable, so "the same bytes"
-- is the same artifact id, and `parser_id`/`parser_version` is the key a reprocess under a newer parser changes: a new
-- release is a new row beside the old one, the old row untouched. Two partial unique indexes carry that key: the
-- parsed and malformed rows by (artifact, parser pair), and the rows no parser was resolved for (an unregistered
-- producer kind has no pair) by (artifact, producer kind). The writer's insert is `on conflict do nothing`, so a
-- replay or a duplicate delivery writes no second row.
--
-- OUTCOME: `mapped` (parsed, declared and observed agree), `partial` (parsed, at least one discrepancy),
-- `malformed` (the parser profile refused the document: zero events, `reason` names the profile code word) and
-- `unsupported` (no parser for the producer kind: zero events, `reason` names why). The CHECKs make the refusal rows
-- structurally empty, so a refused report can never carry an observed count or a discrepancy list.
--
-- DISCREPANCIES: a jsonb array of `{code, count}` objects over the mapper's closed code vocabulary: codes and counts
-- only, never a message body or a case name (PDPL: the raw bytes stay the artifact's, under its retention). Every
-- element is exactly that pair — a code word and an integer count of at least one — so a free-form document, a
-- missing member or an extra one is refused by the shape CHECK before it can carry text out of the artifact.
--
-- PARSER REFERENCE: `report_parser` carries a null org for a built-in release, so a composite tenant FK on
-- (org_id, parser_id) could never match one; the reference is the catalogue's own `unique (parser_id, version)` key.
-- That key sees the full catalogue, so the FK alone would accept a project-owned release of another org: the
-- `raw_report_parser_visible` trigger answers that case, reading the catalogue as the caller so row level security
-- (which hides the foreign row from the tenant, not from the owner) and the explicit org predicate agree.
--
-- RUN PAIRING: a report names both its run and its artifact, and the artifact itself belongs to a run. Referencing
-- the artifact through `(org_id, id)` alone would accept a row naming run A and an artifact of run B of the same
-- org, and the comparison read would attribute the report to a run that never produced it. The reference therefore
-- names the pair `(org_id, id, run_id)`, which needs a key of its own on the large, existing artifact relation:
-- `artifact_org_id_run_key (org_id, id, run_id)`.
--
-- IMMUTABILITY: for every role. UPDATE and DELETE raise 23514 `raw_report_immutable` from a BEFORE trigger, and the
-- app role's grants leave it no UPDATE/DELETE/TRUNCATE to attempt (0066/0074 precedent). The trigger is the only
-- guard the owner role meets; it is also why a row's outcome can never be rewritten after the fact.
--
-- RLS: ENABLE + FORCE with `to evidgate_app` over `evidgate.current_org_id()`; the owner policy exists because FORCE
-- applies to the migration role too. `evidgate_operator` keeps its default SELECT with no operator policy (inert
-- until 02.8.1's grant-gated form); the scheduler role holds nothing.
--
-- LOCKS: a catalogue write on the new relation, plus the artifact pairing key, whose ALTER builds its index over
-- the whole existing table under the ACCESS EXCLUSIVE lock it acquires, so concurrent readers and writers of
-- `artifact` wait for the build; `lock_timeout` bounds only the wait to acquire the lock, never the hold. Additive,
-- applies before the handler that writes it. No down migration: dropping the table would lose which parser release
-- judged stored evidence.
--
-- Idempotent in the shape the README's file contract allows.

set local lock_timeout = '5s';

-- The target of the artifact pairing reference below. PostgreSQL matches a foreign key onto a unique key of the
-- principal, and `artifact_org_id_key` stops at (org_id, id), so the run half needs its own key.
do $artifact_key$
begin
  if not exists (
    select 1
      from pg_catalog.pg_constraint c
     where c.conname = 'artifact_org_id_run_key'
       and c.conrelid = 'public.artifact'::regclass
       and c.contype = 'u')
  then
    alter table public.artifact
      add constraint artifact_org_id_run_key unique (org_id, id, run_id);
  end if;
end
$artifact_key$;

create table if not exists public.raw_report (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  artifact_id uuid not null,
  producer_kind text not null,
  -- Both null only for `unsupported`: no parser was resolved for the producer kind.
  parser_id text,
  parser_version text,
  outcome text not null,
  -- The code word behind a refusal; null for a parsed report.
  reason text,
  declared_tests integer,
  declared_failures integer,
  declared_errors integer,
  declared_skipped integer,
  observed_tests integer not null default 0,
  observed_failures integer not null default 0,
  observed_errors integer not null default 0,
  observed_skipped integer not null default 0,
  discrepancies jsonb not null default '[]'::jsonb,
  created_at timestamptz not null default now(),
  constraint raw_report_pkey primary key (id),
  constraint raw_report_org_id_key unique (org_id, id),
  constraint raw_report_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint raw_report_artifact_fk foreign key (org_id, artifact_id, run_id)
    references public.artifact (org_id, id, run_id),
  constraint raw_report_parser_fk foreign key (parser_id, parser_version)
    references public.report_parser (parser_id, version),
  constraint raw_report_outcome_check check (outcome in ('mapped', 'partial', 'malformed', 'unsupported')),
  constraint raw_report_producer_kind_check check (
    char_length(producer_kind) between 1 and 64 and producer_kind ~ '^[a-z][a-z0-9-]*$'),
  constraint raw_report_parser_pair_check check (
    (parser_id is null) = (parser_version is null)
    and (parser_id is not null or outcome = 'unsupported')),
  constraint raw_report_reason_check check (
    (outcome in ('mapped', 'partial') and reason is null)
    or (outcome in ('malformed', 'unsupported') and reason is not null and reason ~ '^[a-z][a-z0-9_]{0,63}$')),
  constraint raw_report_declared_check check (
    coalesce(declared_tests, 0) >= 0 and coalesce(declared_failures, 0) >= 0
    and coalesce(declared_errors, 0) >= 0 and coalesce(declared_skipped, 0) >= 0),
  constraint raw_report_observed_check check (
    observed_tests >= 0 and observed_failures >= 0 and observed_errors >= 0 and observed_skipped >= 0
    and observed_failures + observed_errors + observed_skipped <= observed_tests),
  -- A refused report has no events and nothing to compare, so its counters and discrepancies are empty by shape.
  constraint raw_report_refused_empty_check check (
    outcome in ('mapped', 'partial')
    or (observed_tests = 0 and declared_tests is null and declared_failures is null
        and declared_errors is null and declared_skipped is null and discrepancies = '[]'::jsonb)),
  constraint raw_report_discrepancy_shape_check check (
    jsonb_typeof(discrepancies) = 'array' and jsonb_array_length(discrepancies) <= 16
    -- Each element is exactly one `{code, count}` object: the element count matches a strict whole-element shape
    -- match, and twice the element count matches every key/value pair in the array, so a missing member, an extra
    -- key or a mistyped value leaves one of the two counts short. A jsonpath predicate skips an element it cannot
    -- read rather than answering for it, which is why the refusal is a count and not a `not exists` test.
    and jsonb_array_length(discrepancies) = jsonb_array_length(
      jsonb_path_query_array(
        discrepancies,
        'strict $[*] ? (@.type() == "object" && @.code.type() == "string"
          && @.code like_regex "^[a-z][a-z0-9_]{0,63}$" && @.count.type() == "number"
          && @.count >= 1 && @.count == @.count.floor())'))
    and jsonb_array_length(discrepancies) * 2 = jsonb_array_length(
      jsonb_path_query_array(discrepancies, 'strict $[*] ? (@.type() == "object").keyvalue()'))),
  -- `partial` means at least one discrepancy; `mapped` means none.
  constraint raw_report_partial_check check (
    (outcome = 'mapped' and discrepancies = '[]'::jsonb)
    or (outcome = 'partial' and discrepancies <> '[]'::jsonb)
    or outcome in ('malformed', 'unsupported'))
);

-- One row per report and parser release; a replay or a duplicate delivery conflicts here and writes nothing.
create unique index if not exists raw_report_artifact_parser_key
  on public.raw_report (org_id, artifact_id, parser_id, parser_version) where parser_id is not null;

-- An unsupported report has no parser pair, so its key is the producer kind it declared.
create unique index if not exists raw_report_artifact_kind_key
  on public.raw_report (org_id, artifact_id, producer_kind) where parser_id is null;

-- The comparison read lists one run's reports (P4), and the referencing side of the composite run FK.
create index if not exists raw_report_run_idx on public.raw_report (org_id, run_id, id);

-- The referencing side of the composite artifact FK: the partial keys above stop at (org_id, artifact_id) and omit
-- run_id, while this non-partial index leads with the same pair, which the three-column check uses as its prefix.
create index if not exists raw_report_artifact_idx on public.raw_report (org_id, artifact_id);

create or replace function evidgate.raw_report_immutable() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    raise exception 'raw_report_immutable: % is append-only (%)', tg_table_name, tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.raw_report_immutable() from public;

drop trigger if exists raw_report_immutable on public.raw_report;
create trigger raw_report_immutable
  before update or delete on public.raw_report
  for each row execute function evidgate.raw_report_immutable();

-- A project-owned `report_parser` row is visible only inside its own org, but the foreign key above compares the
-- release key alone, and a foreign key check bypasses row level security. The guard therefore reads the catalogue
-- as the caller: the tenant's policy hides another org's release, and the owner's backfill policy shows the whole
-- table to the org predicate. An insert is the only event that can name a release; updates raise `raw_report_immutable`.
create or replace function evidgate.raw_report_parser_guard() returns trigger
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
      raise exception 'raw_report_parser_invisible: parser % / % is not a release visible to org %',
          new.parser_id, new.parser_version, new.org_id
        using errcode = '23514', constraint = 'raw_report_parser_invisible';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.raw_report_parser_guard() from public;

drop trigger if exists raw_report_parser_visible on public.raw_report;
create trigger raw_report_parser_visible
  before insert on public.raw_report
  for each row when (new.parser_id is not null)
  execute function evidgate.raw_report_parser_guard();

alter table public.raw_report enable row level security;
alter table public.raw_report force row level security;

drop policy if exists raw_report_app_tenant on public.raw_report;
create policy raw_report_app_tenant on public.raw_report
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists raw_report_owner_backfill on public.raw_report;
create policy raw_report_owner_backfill on public.raw_report
  for all to evidgate_owner using (true) with check (true);

-- Grants: append-only, so the app role reads and inserts and nothing else; UPDATE and DELETE are revoked
-- explicitly, because the migration role's default privileges would otherwise leave them in place (0066/0074 shape).
grant select, insert on public.raw_report to evidgate_app;
revoke update, delete, truncate on public.raw_report from evidgate_app;
grant select on public.raw_report to evidgate_operator;
revoke all on public.raw_report from evidgate_worker_sched;
