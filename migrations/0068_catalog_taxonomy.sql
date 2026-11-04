-- 0068_catalog_taxonomy.sql
--
-- WHY: a case has to be able to carry the external requirement it exercises — a pointer to a Jira/Linear/story id —
-- without that pointer becoming test content (03.1.4, D-A6 §8.1): it is metadata, so it never enters `test_revision`
-- and never changes a `revision_hash`. Separately, a suite has to say what kind of suite it is (`platform`, `level`,
-- `purpose`) so a release decision can pick "the regression web suites" without re-reading every variant; that
-- taxonomy is authored content, so it is pinned on the immutable revision that first carried it and copied to the
-- suite row only as the current reading of its head.
--
-- SHAPE: `test_case.requirement_ref` is nullable text, trimmed, 1..128 characters of `[A-Za-z0-9._:/#-]`; the
-- database check is the cheap half of the rule and the .NET side applies the same two predicates. `suite` and `suite_revision` carry the same three closed lists, `platform` (web, api, unit, mobile),
-- `level` (unit, integration, e2e, smoke) and `purpose` (regression, smoke, release_gate, exploratory); a new value is
-- a migration, like every other enum-like text column (D4). The row defaults are the suite behaviour the product has
-- today: a suite is a browser suite at `e2e` for `regression`. Existing rows take those defaults — a backfill is not a
-- rewrite: no stored `revision_hash` is recomputed, so an old hash stays exactly as valid as it was (AC7). The API
-- picks the level from the platform (`api` -> `integration`, `unit` -> `unit`, `web`/`mobile` -> `e2e`), so the column
-- default is only the fallback for inserts that bypass the API.
--
-- INDEX: `(org_id, project_id, platform, level)` is the suite list's filter (03.1.4 AC3); it is a prefix scan of the
-- project, so the existing `(org_id, project_id, created_at, id)` list index keeps the unfiltered page.
--
-- GUARD: `evidgate.catalog_suite_immutable` compares an explicit column list, so it is re-created with the three
-- taxonomy columns: without them a publish transition could also rewrite the taxonomy of the revision it publishes.
-- `evidgate.catalog_suite_guard` gains the one rule the taxonomy needs on the mutable row — it moves only together
-- with the head, i.e. only as part of the revision that carries it, never as a free-standing edit of the suite.
--
-- GRANTS: `test_case` already holds table-level `update` for `evidgate_app` (0020), so the new column needs no extra
-- grant; `suite` and `suite_revision` keep the grants 0022 gave them (insert/update on both, no delete).
--
-- Idempotent in the shape the README's file contract allows: `add column if not exists`, `drop constraint if exists`
-- before every `add constraint`, `create index if not exists`, and `create or replace function`.

set local lock_timeout = '5s';

-- ---------------------------------------------------------------- a case's requirement pointer (03.1.4 AC1, AC2)

alter table public.test_case
  add column if not exists requirement_ref text;

alter table public.test_case
  drop constraint if exists test_case_requirement_ref_check;

-- NULL passes (a case need not point at a requirement). Anything else must already be trimmed, be 1..128 characters
-- and match `[A-Za-z0-9._:/#-]`: a writer that skips the route cannot store a pointer no screen can render or one no
-- requirement id can look like.
alter table public.test_case
  add constraint test_case_requirement_ref_check
  check (requirement_ref is null
         or (requirement_ref = btrim(requirement_ref)
             and char_length(requirement_ref) between 1 and 128
             and requirement_ref ~ '^[A-Za-z0-9._:/#-]+$'));

-- ---------------------------------------------------------------- the suite taxonomy (03.1.4 AC3, AC5)

alter table public.suite
  add column if not exists platform text not null default 'web';

alter table public.suite
  add column if not exists level text not null default 'e2e';

alter table public.suite
  add column if not exists purpose text not null default 'regression';

alter table public.suite
  drop constraint if exists suite_platform_check;
alter table public.suite
  add constraint suite_platform_check check (platform in ('web', 'api', 'unit', 'mobile'));

alter table public.suite
  drop constraint if exists suite_level_check;
alter table public.suite
  add constraint suite_level_check check (level in ('unit', 'integration', 'e2e', 'smoke'));

alter table public.suite
  drop constraint if exists suite_purpose_check;
alter table public.suite
  add constraint suite_purpose_check check (purpose in ('regression', 'smoke', 'release_gate', 'exploratory'));

create index if not exists suite_taxonomy_idx on public.suite (org_id, project_id, platform, level);

-- The same three lists on the immutable revision: the taxonomy a revision was authored under is content, so the
-- revision that changed it is the only place the change exists (a suite's own columns are the head's reading).
alter table public.suite_revision
  add column if not exists platform text not null default 'web';

alter table public.suite_revision
  add column if not exists level text not null default 'e2e';

alter table public.suite_revision
  add column if not exists purpose text not null default 'regression';

alter table public.suite_revision
  drop constraint if exists suite_revision_platform_check;
alter table public.suite_revision
  add constraint suite_revision_platform_check check (platform in ('web', 'api', 'unit', 'mobile'));

alter table public.suite_revision
  drop constraint if exists suite_revision_level_check;
alter table public.suite_revision
  add constraint suite_revision_level_check check (level in ('unit', 'integration', 'e2e', 'smoke'));

alter table public.suite_revision
  drop constraint if exists suite_revision_purpose_check;
alter table public.suite_revision
  add constraint suite_revision_purpose_check check (purpose in ('regression', 'smoke', 'release_gate', 'exploratory'));

-- ---------------------------------------------------------------- the guards (immutability, taxonomy moves with a head)

-- Unchanged apart from the three columns: the one permitted edit of a revision (draft -> published) must compare them
-- too, or a publish could smuggle a taxonomy change into a revision whose hash was computed without it.
create or replace function evidgate.catalog_suite_immutable() returns trigger
  language plpgsql
  as $$
  begin
    if tg_table_name = 'suite_revision' and tg_op = 'UPDATE' then
      if old.status = 'draft' and new.status = 'published'
         and (new.id, new.org_id, new.suite_id, new.revision_no, new.revision_hash, new.shard_strategy,
              new.retry_policy, new.environment_requirements, new.platform, new.level, new.purpose,
              new.created_by, new.created_at)
             is not distinct from
             (old.id, old.org_id, old.suite_id, old.revision_no, old.revision_hash, old.shard_strategy,
              old.retry_policy, old.environment_requirements, old.platform, old.level, old.purpose,
              old.created_by, old.created_at) then
        return new;
      end if;
    end if;

    raise exception 'revision_immutable: % is append-only (%)', tg_table_name, tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.catalog_suite_immutable() from public;

-- The suite row's taxonomy is the head revision's taxonomy, enforced in both directions: a taxonomy change needs a
-- head, and any head must be a revision carrying exactly that taxonomy. A free-standing taxonomy edit (head kept or
-- cleared) and a head move that leaves a stale taxonomy on the suite are both refused, whatever role writes.
create or replace function evidgate.catalog_suite_guard() returns trigger
  language plpgsql
  as $$
  begin
    if (new.id, new.org_id, new.project_id) is distinct from (old.id, old.org_id, old.project_id) then
      raise exception 'suite: id, org_id and project_id never change'
        using errcode = '23514';
    end if;

    if new.head_revision_id is not null
       and new.head_revision_id is distinct from old.head_revision_id
       and not exists (
         select 1
           from public.suite_revision r
          where r.org_id = new.org_id
            and r.suite_id = new.id
            and r.id = new.head_revision_id) then
      raise exception 'suite: the head must be a revision of this suite'
        using errcode = '23514';
    end if;

    if new.head_revision_id is null then
      if (new.platform, new.level, new.purpose) is distinct from (old.platform, old.level, old.purpose) then
        raise exception 'suite: the taxonomy changes only with the revision that carries it'
          using errcode = '23514';
      end if;
    elsif not exists (
      select 1
        from public.suite_revision r
       where r.org_id = new.org_id
         and r.suite_id = new.id
         and r.id = new.head_revision_id
         and r.platform = new.platform
         and r.level = new.level
         and r.purpose = new.purpose) then
      raise exception 'suite: the taxonomy must be the head revision''s taxonomy'
        using errcode = '23514';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.catalog_suite_guard() from public;
