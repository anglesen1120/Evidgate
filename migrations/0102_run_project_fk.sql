-- 0102_run_project_fk.sql
--
-- WHY: `0031` gave `run.project_id` no foreign key and tied each referenced row only to the run's org
-- (`(org_id, manifest_id)` and the like), so a run could name a project that does not exist, or a manifest,
-- environment revision, policy revision or suite revision that belongs to another project of the same org. The
-- evaluator and every run read assume one project per run, so the database itself must refuse the mix.
--
-- SHAPE:
--   * `run_project_fk (org_id, project_id) -> project (org_id, id)`;
--   * `run_manifest_fk`, `run_environment_revision_fk`, `run_policy_revision_fk` become 3-column keys
--     `(org_id, project_id, <ref>_id) -> <table> (org_id, project_id, id)`, with the matching unique keys
--     `<table>_org_project_id_key` added (the tables carry `project_id` already). The names are kept, so the
--     SQLSTATE `23503` constraint names callers and tests see do not change. `policy_revision_id` is nullable and the
--     key is MATCH SIMPLE: a run without a policy revision is not checked, as before.
--   * `suite_revision` has no `project_id` (its project is `suite.project_id`) and is append-only, so a 3-column key
--     is not possible without changing that table. The trigger `run_suite_revision_project_guard` checks the
--     suite revision's suite belongs to `run.project_id` and raises `23503` under the same constraint name, so
--     the writer sees one error shape for every cross-project reference. Why a trigger and not a key: the project of
--     a suite revision lives one hop away, on `suite`, and adding `project_id` to the sealed table would change its
--     immutability trigger and every writer. The check cannot be bypassed later because `suite.project_id` and
--     `suite_revision.suite_id` never change (`suite_guard`, 0022, for every role; `suite_revision_immutable`).
--     The lookup fails closed: `not exists` over an inner join, so a missing, other-org or RLS-hidden revision or
--     suite raises, and no NULL can pass. The function is SECURITY INVOKER: it only reads two tenant tables the
--     caller already reads under RLS, so it needs no definer rights and widens nothing. It fires on INSERT and on
--     UPDATE OF org_id, project_id, suite_revision_id.
--
-- DATA: the file never rewrites or deletes rows. A pre-check counts the rows that violate each rule and raises with
-- the counts; the operator repairs them and re-runs. Re-running is a no-op once applied.
--
-- LOCKS (all held to the end of the migration's single transaction; `lock_timeout` 5 s fails the file instead of
-- queuing behind a long reader):
--   * `create unique index` on `manifest`, `environment_revision`, `policy_revision`: SHARE (reads continue, writes wait);
--   * `add constraint ... unique using index`: ACCESS EXCLUSIVE, catalogue only, no scan;
--   * `drop constraint` of the three old keys: ACCESS EXCLUSIVE on `run` and the referenced table, catalogue only;
--   * `add constraint ... foreign key ... not valid`: SHARE ROW EXCLUSIVE on `run` and the referenced table, no scan;
--   * `validate constraint`: SHARE UPDATE EXCLUSIVE on `run`, ROW SHARE on the referenced table; one indexed pass
--     over `run` (the pre-check above already proved it passes). `planned_execution`, the large table, is untouched;
--   * `lock table public.run in share row exclusive mode` before the pre-check: reads continue, run writes wait;
--   * `create trigger`: SHARE ROW EXCLUSIVE on `run`.
-- The `run` table holds one row per run (not per execution), so the pass is short; the first deploy window is the
-- only exposure.
--
-- ACCEPTED RESIDUALS: the table owner can DROP or DISABLE the trigger, and a superuser can set
-- `session_replication_role = replica`, which also skips the foreign-key triggers; the app role can do neither. All
-- locks above are held to commit, so VALIDATE does not shorten them; the pre-check makes it a single pass over `run`.
--
-- WRITERS: every `run` insert (`OrchestratorSql.InsertRun`, the rerun path) takes the manifest, suite revision,
-- environment revision and policy revision from rows it has just resolved under the same project, so none of them
-- inserts a row this file refuses. Deploy: any order. Rollback: drop the four keys, the trigger and its function, and
-- re-add the old 2-column keys; no data is transformed.
set local lock_timeout = '5s';

-- Taken before the pre-check and held to commit: no run is written or moved between the check and the keys and trigger
-- below, so a row that passes the check cannot be changed to violate a rule the trigger (which does not re-check old
-- rows) would then miss. Reads continue.
lock table public.run in share row exclusive mode;

do $$
declare
  v_project bigint;
  v_manifest bigint;
  v_environment bigint;
  v_policy bigint;
  v_suite bigint;
begin
  select count(*) into v_project
    from public.run r
    left join public.project p on p.org_id = r.org_id and p.id = r.project_id
   where p.id is null;
  select count(*) into v_manifest
    from public.run r
    left join public.manifest m on m.org_id = r.org_id and m.id = r.manifest_id
   where m.project_id is distinct from r.project_id;
  select count(*) into v_environment
    from public.run r
    left join public.environment_revision e on e.org_id = r.org_id and e.id = r.environment_revision_id
   where e.project_id is distinct from r.project_id;
  select count(*) into v_policy
    from public.run r
    left join public.policy_revision pr on pr.org_id = r.org_id and pr.id = r.policy_revision_id
   where r.policy_revision_id is not null
     and pr.project_id is distinct from r.project_id;
  select count(*) into v_suite
    from public.run r
    left join public.suite_revision sr on sr.org_id = r.org_id and sr.id = r.suite_revision_id
    left join public.suite s on s.org_id = sr.org_id and s.id = sr.suite_id
   where s.project_id is distinct from r.project_id;

  if v_project + v_manifest + v_environment + v_policy + v_suite > 0 then
    raise exception 'run: existing row(s) break the one-project rule (missing project %, manifest of another project %, environment revision of another project %, policy revision of another project %, suite revision of another project %); repair them before 0102',
      v_project, v_manifest, v_environment, v_policy, v_suite
      using errcode = '23503';
  end if;
end
$$;

-- Unique keys the 3-column references need. The index is built first (SHARE), then attached as a constraint (no scan).
create unique index if not exists manifest_org_project_id_key on public.manifest (org_id, project_id, id);
create unique index if not exists environment_revision_org_project_id_key on public.environment_revision (org_id, project_id, id);
create unique index if not exists policy_revision_org_project_id_key on public.policy_revision (org_id, project_id, id);

do $$
begin
  if not exists (select 1 from pg_catalog.pg_constraint where conrelid = 'public.manifest'::regclass and conname = 'manifest_org_project_id_key') then
    alter table public.manifest add constraint manifest_org_project_id_key unique using index manifest_org_project_id_key;
  end if;
  if not exists (select 1 from pg_catalog.pg_constraint where conrelid = 'public.environment_revision'::regclass and conname = 'environment_revision_org_project_id_key') then
    alter table public.environment_revision add constraint environment_revision_org_project_id_key unique using index environment_revision_org_project_id_key;
  end if;
  if not exists (select 1 from pg_catalog.pg_constraint where conrelid = 'public.policy_revision'::regclass and conname = 'policy_revision_org_project_id_key') then
    alter table public.policy_revision add constraint policy_revision_org_project_id_key unique using index policy_revision_org_project_id_key;
  end if;
end
$$;

-- Swap the three 2-column keys for the 3-column ones and add the project key; NOT VALID first, then VALIDATE.
alter table public.run drop constraint if exists run_manifest_fk;
alter table public.run drop constraint if exists run_environment_revision_fk;
alter table public.run drop constraint if exists run_policy_revision_fk;

alter table public.run drop constraint if exists run_project_fk;
alter table public.run add constraint run_project_fk
  foreign key (org_id, project_id) references public.project (org_id, id) not valid;
alter table public.run add constraint run_manifest_fk
  foreign key (org_id, project_id, manifest_id) references public.manifest (org_id, project_id, id) not valid;
alter table public.run add constraint run_environment_revision_fk
  foreign key (org_id, project_id, environment_revision_id) references public.environment_revision (org_id, project_id, id) not valid;
alter table public.run add constraint run_policy_revision_fk
  foreign key (org_id, project_id, policy_revision_id) references public.policy_revision (org_id, project_id, id) not valid;

alter table public.run validate constraint run_project_fk;
alter table public.run validate constraint run_manifest_fk;
alter table public.run validate constraint run_environment_revision_fk;
alter table public.run validate constraint run_policy_revision_fk;

-- The suite revision's project is its suite's. Invoker rights: both tables are tenant-scoped, and a revision the
-- caller cannot see under RLS is as good as missing, so the run is refused.
create or replace function evidgate.run_guard_suite_revision_project() returns trigger
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
      raise exception 'run_suite_revision_project: suite revision % is not a revision of a suite of project %', new.suite_revision_id, new.project_id
        using errcode = '23503', constraint = 'run_suite_revision_project_guard';
    end if;
    return new;
  end;
  $$;

revoke all on function evidgate.run_guard_suite_revision_project() from public;
alter function evidgate.run_guard_suite_revision_project() owner to evidgate_owner;

drop trigger if exists run_suite_revision_project_guard on public.run;
create trigger run_suite_revision_project_guard
  before insert or update of org_id, project_id, suite_revision_id on public.run
  for each row
  execute function evidgate.run_guard_suite_revision_project();
