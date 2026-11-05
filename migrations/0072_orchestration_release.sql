-- 0072_orchestration_release.sql
--
-- WHY: a release names the exact build digests a set of runs must have exercised, so a decision can be bound to
-- binaries rather than to a branch head that moved. Without the tables there is nowhere to store the declaration, and
-- the run-create path keeps answering `release_unresolvable`.
--
-- SHAPE: three tables, one write path each.
--   release           one declaration per project: a name, the closed state vocabulary `draft`/`active`, and the
--                     identity of its author. `unique (org_id, project_id, name)` makes a name one declaration in its
--                     project, and `unique (org_id, id)` is the composite-FK target every business table declares (T3).
--                     `state` is set by the create command alone: a release is inserted `draft` and moved to `active` in
--                     the same transaction when the request asked for it, so the only transition the guard below
--                     permits is `draft -> active`, and `active` is final.
--   release_component one row per component key with the digest and the environment revision the digest was built for.
--                     `unique (org_id, release_id, component_key)` makes a component key unique inside its release, and
--                     the two composite FKs keep the row inside its org: `(org_id, release_id) -> release` and
--                     `(org_id, environment_revision_id) -> environment_revision`.
--   release_run       the binding of a run to a release for one component key, written by the bind path only. The
--                     digest match itself is 05.6.1-T2's; this migration creates the table its rows land in, so the
--                     shape (and its constraints) is reviewed with the release it belongs to. `release_run` is the only
--                     run <-> release binding: `run` carries no `release_id` (T15).
--
-- CHECKS: every enum-like text column is closed in the database (D4). A digest is `sha256:` + 64 lower-case hex, the
-- same shape `environment_revision.revision_hash` and `manifest.manifest_hash` carry, so a writer that skips the route
-- cannot store a digest nothing can compare. A component key is `[a-z0-9][a-z0-9_-]{0,63}` — the environment-key
-- grammar the same plan set uses — and a release name is 1..128 trimmed printable characters: the route applies the
-- same bounds before any statement, and these are the cheap half that keeps a bypassing writer from storing text no
-- screen can render.
--
-- RLS: ENABLE + FORCE on all three, with the tenant policy `to evidgate_app` over `evidgate.current_org_id()` (never
-- raw `current_setting`, so a missing or empty setting yields zero rows rather than an error or every row, T2) and the
-- owner backfill policy FORCE requires (0004/0028/0062/0066/0069/0071 precedent). The project half of the scope is
-- the statement's own `project_id` predicate and the composite FKs — the same split every Orchestrator table uses
-- (`run`, `environment_revision`), because a policy over `current_project_id()` would refuse a project-wide read that
-- legitimately spans the org.
--
-- GRANTS: 0001's default privileges grant `evidgate_app` full DML on a new `public` table, so the table-level revoke
-- below comes first (0062/0066/0069 precedent). The app role then holds SELECT + INSERT on `release` and UPDATE on
-- `state` alone — the one column the create command moves from `draft` to `active` — so no code path can rename a
-- release, repoint it at another project or rewrite its author, and no DELETE exists. `release_component` and
-- `release_run` are append-only: SELECT + INSERT only, no UPDATE and no DELETE at any role, which is the grant half of
-- AC7 ("the app role holds no UPDATE grant") and of "unbind does not exist in V1". `evidgate_operator` keeps its
-- default SELECT and holds no policy on these tables, so until 02.8.1's grant-gated operator policy lands it reads
-- zero rows (0028/0069's posture, recorded here as a decision). `evidgate_worker_sched` holds nothing: no scheduled job
-- reads or writes releases.
--
-- GUARDS: `release.state` is an app-movable state column, so it gets a transition trigger (D2) rather than trusting
-- every future writer: the identity columns never change, `draft` moves to `active`, `active` never moves, and a
-- DELETE is refused. `release_component` is append-only and, once its release is `active`, closed to INSERT too —
-- the immutability AC7 requires, enforced in SQL because the domain type is only half of it (C6). `release_run` is
-- append-only like `environment_revision` (0024's guard, same shape).
--
-- LOCKS: `set local lock_timeout` bounds the DDL's wait, so a busy database fails the migration for a retry instead of
-- stalling writes. Everything here is a catalogue write on a new relation.
--
-- ORDER: additive — three new relations nothing referenced before — so apply it before the route that writes releases.
-- No down migration: dropping the tables would delete every release declaration and every binding, which no rollback
-- restores.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`, `create index if not
-- exists`, `drop policy/trigger if exists` before every policy and trigger, `create or replace function`, and the
-- repeatable `revoke`/`grant`.

set local lock_timeout = '5s';

create table if not exists public.release (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  name text not null,
  state text not null default 'draft',
  created_by text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint release_pkey primary key (id),
  constraint release_org_id_key unique (org_id, id),
  constraint release_name_key unique (org_id, project_id, name),
  constraint release_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  -- A name is trimmed, 1..128 characters and printable: a control character would make two releases whose names look
  -- identical to an operator, and an untrimmed one would make the unique key above miss a duplicate.
  -- The app rule (`ReleaseRules.IsName`) refuses Cc, Cf, Zl and Zp; this check refuses Cc plus the common invisible and
  -- separator code points (bidi, zero-width, BOM, the two separators). The check refuses a subset of what the app
  -- refuses, so no value the route accepts can fail it (a 500).
  constraint release_name_check check (char_length(name) between 1 and 128
                                      and name = btrim(name)
                                      and name !~ '[[:cntrl:]]'
                                      and name !~ '[\u00AD\u200B-\u200F\u202A-\u202E\u2060-\u2064\uFEFF\u2028\u2029]'),
  constraint release_state_check check (state in ('draft', 'active')),
  constraint release_created_by_check check (char_length(created_by) between 1 and 128),
  constraint release_version_check check (version >= 1)
);

-- The list keyset (P4): `(org_id, project_id, created_at, id)` serves the project's release list in the cursor's own
-- order, and its `(org_id, project_id)` prefix also serves the referencing side of the project FK.
create index if not exists release_project_idx on public.release (org_id, project_id, created_at, id);

drop trigger if exists release_set_updated_at_and_version on public.release;
create trigger release_set_updated_at_and_version
  before update on public.release
  for each row execute function public.set_updated_at_and_version();

create table if not exists public.release_component (
  id uuid not null,
  org_id uuid not null,
  release_id uuid not null,
  component_key text not null,
  digest text not null,
  environment_revision_id uuid not null,
  created_at timestamptz not null default now(),
  constraint release_component_pkey primary key (id),
  constraint release_component_org_id_key unique (org_id, id),
  constraint release_component_key_key unique (org_id, release_id, component_key),
  constraint release_component_release_fk foreign key (org_id, release_id) references public.release (org_id, id),
  constraint release_component_environment_revision_fk
    foreign key (org_id, environment_revision_id) references public.environment_revision (org_id, id),
  constraint release_component_key_check check (component_key ~ '^[a-z0-9][a-z0-9_-]{0,63}$'),
  constraint release_component_digest_check check (digest ~ '^sha256:[0-9a-f]{64}$')
);

-- The referencing side of the environment-revision FK (a project or tenant erase). The release FK is served by the
-- prefix of `release_component_key_key`.
create index if not exists release_component_environment_revision_idx
  on public.release_component (org_id, environment_revision_id);

create table if not exists public.release_run (
  id uuid not null,
  org_id uuid not null,
  release_id uuid not null,
  run_id uuid not null,
  component_key text not null,
  created_at timestamptz not null default now(),
  constraint release_run_pkey primary key (id),
  constraint release_run_org_id_key unique (org_id, id),
  constraint release_run_binding_key unique (org_id, release_id, run_id, component_key),
  constraint release_run_release_fk foreign key (org_id, release_id) references public.release (org_id, id),
  constraint release_run_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint release_run_component_key_check check (component_key ~ '^[a-z0-9][a-z0-9_-]{0,63}$')
);

-- "Which releases is this run bound to" (P4). The release side is served by the prefix of `release_run_binding_key`.
create index if not exists release_run_run_idx on public.release_run (org_id, run_id);

alter table public.release enable row level security;
alter table public.release force row level security;
alter table public.release_component enable row level security;
alter table public.release_component force row level security;
alter table public.release_run enable row level security;
alter table public.release_run force row level security;

drop policy if exists release_app_tenant on public.release;
create policy release_app_tenant on public.release
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy (TL T76, D1): the grant-gated operator policy is 02.8.1's
-- `evidgate.operator_grant_ok` form; until it lands the operator's default SELECT reads zero rows.
drop policy if exists release_owner_backfill on public.release;
create policy release_owner_backfill on public.release
  for all to evidgate_owner using (true) with check (true);

drop policy if exists release_component_app_tenant on public.release_component;
create policy release_component_app_tenant on public.release_component
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists release_component_owner_backfill on public.release_component;
create policy release_component_owner_backfill on public.release_component
  for all to evidgate_owner using (true) with check (true);

drop policy if exists release_run_app_tenant on public.release_run;
create policy release_run_app_tenant on public.release_run
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists release_run_owner_backfill on public.release_run;
create policy release_run_owner_backfill on public.release_run
  for all to evidgate_owner using (true) with check (true);

revoke insert, update, delete, truncate on public.release from evidgate_app;
grant select on public.release to evidgate_app;
grant insert (id, org_id, project_id, name, created_by) on public.release to evidgate_app;
grant update (state) on public.release to evidgate_app;
grant select on public.release to evidgate_operator;
revoke all on public.release from evidgate_worker_sched;

revoke insert, update, delete, truncate on public.release_component from evidgate_app;
grant select, insert on public.release_component to evidgate_app;
grant select on public.release_component to evidgate_operator;
revoke all on public.release_component from evidgate_worker_sched;

revoke insert, update, delete, truncate on public.release_run from evidgate_app;
grant select, insert on public.release_run to evidgate_app;
grant select on public.release_run to evidgate_operator;
revoke all on public.release_run from evidgate_worker_sched;

-- The release state machine (D2). Identity, author and creation time are facts of the declaration and never move;
-- `draft` moves to `active`, `active` is final, and a release is never deleted. Re-writing the state a row already
-- holds is not a change and stays allowed, so a replay of the same transition is not an error.
create or replace function evidgate.orchestration_release_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'release_immutable: a release is never deleted'
        using errcode = '23514';
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.name is distinct from old.name
       or new.created_by is distinct from old.created_by
       or new.created_at is distinct from old.created_at then
      raise exception 'release_immutable: the release, its project, name and author never change (UPDATE)'
        using errcode = '23514';
    end if;

    if new.state is distinct from old.state
       and not (old.state = 'draft' and new.state = 'active') then
      raise exception 'release_state_invalid: % -> %', old.state, new.state
        using errcode = '23514';
    end if;

    -- An `active` release with no component states nothing a run could be bound to, so the one permitted transition
    -- requires the set it freezes. The create path inserts the release `draft`, its components, and only then moves it
    -- to `active` in the same transaction, so a create naming `active` satisfies this.
    if new.state = 'active'
       and old.state = 'draft'
       and not exists (select 1 from public.release_component c where c.org_id = new.org_id and c.release_id = new.id) then
      raise exception 'release_state_invalid: an active release needs at least one component'
        using errcode = '23514';
    end if;

    return new;
  end;
  $$;

revoke all on function evidgate.orchestration_release_guard() from public;

drop trigger if exists release_guard on public.release;
create trigger release_guard
  before update or delete on public.release
  for each row execute function evidgate.orchestration_release_guard();

-- A component is written once, inside the release's own create transaction, and only while the release is still
-- `draft`: once the release is `active` its component set is final (AC7). The grant already withholds UPDATE and
-- DELETE from every role, so this trigger is what closes INSERT on an active release — the one write the grant cannot
-- express.
create or replace function evidgate.orchestration_release_component_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op <> 'INSERT' then
      raise exception 'release_component_immutable: a component is written once (%)', tg_op
        using errcode = '23514';
    end if;

    -- The parent row is locked FOR SHARE before its state is read, so a concurrent `draft -> active` UPDATE of the
    -- same release waits here (and this INSERT waits for it), and the two can never interleave: a component can only
    -- land while the release is still `draft`, and the activation cannot commit with a component written after it.
    perform 1
       from public.release r
      where r.org_id = new.org_id
        and r.id = new.release_id
        for share;

    if exists (select 1
                 from public.release r
                where r.org_id = new.org_id
                  and r.id = new.release_id
                  and r.state = 'active') then
      raise exception 'release_component_closed: the release is active, so its components are final'
        using errcode = '23514';
    end if;

    -- The composite FK to `environment_revision` keeps the revision inside the org; this keeps it inside the release's
    -- own project, so a release of project A can never cite a revision built for project B of the same org. Only a
    -- revision that IS in this org but in another project is refused here (23514); a missing or another org's revision
    -- is left to the composite FK, so it answers the one 23503 every tenant reference answers.
    if exists (select 1
                 from public.release r
                 join public.environment_revision er
                   on er.org_id = r.org_id
                  and er.id = new.environment_revision_id
                where r.org_id = new.org_id
                  and r.id = new.release_id
                  and er.project_id <> r.project_id) then
      raise exception 'release_component_project: the environment revision belongs to another project'
        using errcode = '23514';
    end if;

    return new;
  end;
  $$;

revoke all on function evidgate.orchestration_release_component_guard() from public;

drop trigger if exists release_component_guard on public.release_component;
create trigger release_component_guard
  before insert or update or delete on public.release_component
  for each row execute function evidgate.orchestration_release_component_guard();

-- A binding is appended once and never rewritten: unbind does not exist in V1, and a re-bind of the same tuple is the
-- unique key's no-op, not an UPDATE.
create or replace function evidgate.orchestration_release_run_immutable() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    raise exception 'release_run_immutable: a release binding is append-only (%)', tg_op
      using errcode = '23514';
  end;
  $$;

revoke all on function evidgate.orchestration_release_run_immutable() from public;

drop trigger if exists release_run_immutable on public.release_run;
create trigger release_run_immutable
  before update or delete on public.release_run
  for each row execute function evidgate.orchestration_release_run_immutable();
