-- 0103_run_manifest_suite_parent_keys.sql
--
-- WHY: `0102` tied a run to its own project for the manifest, environment revision, policy revision and suite revision,
-- and left three references that still only share an org:
--   (a) `manifest.suite_revision_id` is not compared with the manifest's project, so a manifest of project A may
--       name a suite revision of project B's suite;
--   (b) `run.suite_revision_id` is a second copy of the manifest's suite revision and nothing ties the two together, so a
--       run may claim a suite revision other than the one its (immutable) manifest was verified against;
--   (c) `run_parent_run_fk` is `(org_id, parent_run_id)`, so a rerun may point at a run of another project.
--
-- DECISIONS: (b) is EQUALITY, not compatibility. Every writer (`OrchestratorSql.RunManifest` then `InsertRun`) copies the
-- suite revision from the manifest row it resolved, and the manifest is immutable, so there is no case where the two
-- legitimately differ. (a) cannot be a key (`suite_revision` has no `project_id`, its project lives on `suite`), so it is
-- the same SECURITY INVOKER trigger shape `0102` uses for the run; (b) and (c) are composite foreign keys.
--
-- SHAPE:
--   * trigger `manifest_suite_revision_project_guard` (BEFORE INSERT on `manifest`; a manifest is append-only, so no UPDATE
--     case): raises `23503` under that constraint name unless the suite revision's suite belongs to `manifest.project_id`.
--     Fails closed: `not exists` over an inner join, so a missing, other-org or RLS-hidden revision raises. Invoker rights:
--     it reads two tenant tables the caller already reads under RLS;
--   * `manifest_org_id_suite_revision_key (org_id, id, suite_revision_id)` unique, and `run_manifest_suite_revision_fk
--     (org_id, manifest_id, suite_revision_id) -> manifest`: the run's suite revision equals its manifest's (MATCH SIMPLE,
--     all three columns are NOT NULL);
--   * `run_org_project_id_key (org_id, project_id, id)` unique, and `run_parent_run_fk` becomes
--     `(org_id, project_id, parent_run_id) -> run (org_id, project_id, id)`: the parent run is of the same project (name
--     kept; `parent_run_id` is nullable, MATCH SIMPLE leaves a run without a parent unchecked, as before).
--
-- DATA: the file never rewrites or deletes rows. A pre-check counts the rows that violate each rule and raises `23503`
-- with the counts; the operator repairs them and re-runs. Re-running is a no-op once applied.
--
-- LOCKS (all held to the end of the single transaction; `lock_timeout` 5 s fails the file instead of queuing behind a long
-- reader). ORDER, the same as `0102`: the parent table `manifest` first, then `run`; a run creation reads `manifest`
-- before it writes `run`, so the two cannot wait for each other.
--   * `create unique index` on `manifest`: SHARE (reads continue, writes wait); `add constraint ... unique using index`:
--     ACCESS EXCLUSIVE, catalogue only;
--   * `lock table public.manifest in access exclusive mode`, then `lock table public.run in access exclusive mode nowait`: the
--     unique key on `run` and the swap of its parent key need ACCESS EXCLUSIVE, so it is taken once up front instead of
--     upgraded from SHARE (an upgrade is a deadlock risk). `run` holds one row per run, not per execution, so the window is
--     short; `planned_execution`, the large table, is untouched. Reads on both tables wait for the file;
--   * `drop constraint run_parent_run_fk`: ACCESS EXCLUSIVE on `run` (already held);
--   * `add constraint ... foreign key ... not valid`: SHARE ROW EXCLUSIVE on `run` and `manifest`, no scan;
--   * `validate constraint`: SHARE UPDATE EXCLUSIVE on `run` and ROW SHARE on the referenced table, one pass over `run`
--     (the pre-check proved it passes; the locks above stay held to commit, so VALIDATE does not shorten them);
--   * `create trigger` on `manifest`: SHARE ROW EXCLUSIVE.
--
-- ACCEPTED RESIDUALS: as in `0102`: the table owner can DROP or DISABLE the trigger, and a superuser can set
-- `session_replication_role = replica`, which also skips the foreign-key triggers; the app role can do neither.
--
-- WRITERS: `UploadManifest` refuses an unknown or other-project suite revision (`unknown_suite_revision`) before
-- `OrchestratorSql.InsertManifest` writes; `InsertRun` copies `suite_revision_id` from the manifest row;
-- no source writer sets `parent_run_id`.
-- Deploy: any order. Rollback: drop the trigger and its function; drop `run_parent_run_fk` and
-- `run_manifest_suite_revision_fk` (the parent key depends on `run_org_project_id_key`), then both unique keys, then
-- re-add the 2-column `run_parent_run_fk (org_id, parent_run_id) -> run (org_id, id)`; no data is transformed.
set local lock_timeout = '5s';

create unique index if not exists manifest_org_id_suite_revision_key on public.manifest (org_id, id, suite_revision_id);

do $$
begin
  if not exists (select 1 from pg_catalog.pg_constraint where conrelid = 'public.manifest'::regclass and conname = 'manifest_org_id_suite_revision_key') then
    alter table public.manifest add constraint manifest_org_id_suite_revision_key unique using index manifest_org_id_suite_revision_key;
  end if;
end
$$;

-- Explicit so a re-execution (key already attached) still takes the parent first, then `run`.
lock table public.manifest in access exclusive mode;
-- NOWAIT: a run lock held by a reconciler or a run creation, which then reads `manifest` (held above), would wait for
-- this file while this file waits for it. Failing at once (55P03) rolls the file back and frees `manifest`; rerun when idle.
lock table public.run in access exclusive mode nowait;

create unique index if not exists run_org_project_id_key on public.run (org_id, project_id, id);

do $$
begin
  if not exists (select 1 from pg_catalog.pg_constraint where conrelid = 'public.run'::regclass and conname = 'run_org_project_id_key') then
    alter table public.run add constraint run_org_project_id_key unique using index run_org_project_id_key;
  end if;
end
$$;

do $$
declare
  v_manifest bigint;
  v_run_suite bigint;
  v_parent bigint;
begin
  select count(*) into v_manifest
    from public.manifest m
    left join public.suite_revision sr on sr.org_id = m.org_id and sr.id = m.suite_revision_id
    left join public.suite s on s.org_id = sr.org_id and s.id = sr.suite_id
   where s.project_id is distinct from m.project_id;
  select count(*) into v_run_suite
    from public.run r
    left join public.manifest m on m.org_id = r.org_id and m.id = r.manifest_id
   where m.suite_revision_id is distinct from r.suite_revision_id;
  select count(*) into v_parent
    from public.run r
    left join public.run p on p.org_id = r.org_id and p.id = r.parent_run_id
   where r.parent_run_id is not null
     and p.project_id is distinct from r.project_id;

  if v_manifest + v_run_suite + v_parent > 0 then
    raise exception 'run keys: existing row(s) break the one-project rule (manifest with a suite revision of another project %, run whose suite revision differs from its manifest''s %, run whose parent run is of another project %); repair them before 0103',
      v_manifest, v_run_suite, v_parent
      using errcode = '23503';
  end if;
end
$$;

alter table public.run drop constraint if exists run_parent_run_fk;
alter table public.run drop constraint if exists run_manifest_suite_revision_fk;

alter table public.run add constraint run_parent_run_fk
  foreign key (org_id, project_id, parent_run_id) references public.run (org_id, project_id, id) not valid;
alter table public.run add constraint run_manifest_suite_revision_fk
  foreign key (org_id, manifest_id, suite_revision_id) references public.manifest (org_id, id, suite_revision_id) not valid;

alter table public.run validate constraint run_parent_run_fk;
alter table public.run validate constraint run_manifest_suite_revision_fk;

create or replace function evidgate.manifest_guard_suite_revision_project() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if not exists (
        select 1
          from public.suite_revision sr
          join public.suite s on s.org_id = sr.org_id and s.id = sr.suite_id
         where sr.org_id = new.org_id
           and sr.id = new.suite_revision_id
           and s.project_id = new.project_id) then
      raise exception 'manifest_suite_revision_project: suite revision % is not a revision of a suite of project %', new.suite_revision_id, new.project_id
        using errcode = '23503', constraint = 'manifest_suite_revision_project_guard';
    end if;
    return new;
  end;
  $$;

revoke all on function evidgate.manifest_guard_suite_revision_project() from public;
alter function evidgate.manifest_guard_suite_revision_project() owner to evidgate_owner;

drop trigger if exists manifest_suite_revision_project_guard on public.manifest;
create trigger manifest_suite_revision_project_guard
  before insert on public.manifest
  for each row
  execute function evidgate.manifest_guard_suite_revision_project();
