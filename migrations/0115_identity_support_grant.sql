-- 0115_identity_support_grant.sql
--
-- The support-grant model: the table a tenant admin's approval lives in, the owner functions that
-- turn one approved grant into a row filter, and the first operator read policies that use them.
-- Until this file `evidgate_operator` held SELECT on every tenant table and read zero rows because no
-- policy named it (0004/0005 posture); after it, the operator reads a row only while a valid grant
-- covers it. No command, route or pipeline step writes or presents a grant yet: the model is
-- reachable by SQL only, and the tests drive it as `evidgate_operator` by direct statements.
--
-- STATES. Stored: `requested` (approved_at and revoked_at null), `approved`, `revoked`.
-- `expired` is derived from `expires_at` and never stored, so no sweeper has to cut access. The write
-- guard allows requested -> approved, requested -> revoked, approved -> revoked and nothing else:
-- `approved_*` and `revoked_at` are write-once, a revoked row accepts no change, a requested grant
-- whose expiry has passed cannot be approved, an approver cannot be the requester, and a row is never
-- deleted (the table is the audit of who asked to see what).
--
-- SCOPE: `scopes` holds permission strings (`resource:action`) and
-- `project_ids` the projects they cover. The database predicate checks the operator, the org, the
-- approval, the expiry, the revocation, project membership in `project_ids` (an org-scoped table: any
-- valid grant of that org) and only the action CLASS (`:read` or `:*` for a read; any other action
-- suffix or `:*` for a write). Matching one permission code against `scopes` is the pipeline's job.
--
-- THE POLICY SHAPE. `org_id = (select evidgate.operator_grant_org()) and <project key> = any
-- ((select evidgate.operator_grant_read_projects())::uuid[])`. Both subqueries are one InitPlan per
-- statement: the first confines the tenant by a value the session cannot choose (the grant row) and is
-- the comparison shape scripts/check-rls.py accepts as a gate; the second is the grant's project list
-- when its scope allows a read. `organization` calls operator_grant_ok once per row instead. `support_grant` itself shows the operator
-- only its own valid grant row (`org_id` as above and `id = (select evidgate.operator_grant_id())`). The operator is identified by two settings,
-- `evidgate.support_grant_id` and `evidgate.operator_user_id`. The role may set both itself, so they
-- name a claim, not a credential: a claim only opens a row when an approved, unrevoked, unexpired
-- grant of that very operator exists, and the connection string of the role is held by the operator
-- ingress alone.
--
-- FIRST SET. Policies are added here for `support_grant`, `organization`, `project` and `test_case`
-- (a project-keyed table: it has `project_id` and a composite tenant FK). Every other tenant table
-- keeps SELECT with no operator policy and reads zero rows for the operator: fail closed until the
-- rest of the set lands. Tables holding a credential hash or secret material never get
-- an operator policy.
--
-- READ-ONLY. There is no write policy and no DML grant for `evidgate_operator`: a policy nothing can
-- reach is not shipped, and the operator write path is owed to the operator access task.
--
-- DEFINERS. `evidgate.operator_grant_org`, `evidgate.operator_grant_id`, `evidgate.operator_grant_read_projects`,
-- `evidgate.operator_grant_ok` and
-- `evidgate.resolve_active_grant` are owner-owned, SECURITY DEFINER, `search_path` pinned empty,
-- schema-qualified, STABLE and write nothing. EXECUTE is revoked from PUBLIC and granted to
-- `evidgate_operator` only: the app role has no use for them. A per-call audit row is impossible
-- from a policy predicate, so the file carries an audit-deferred marker; the per-read audit row is
-- written by the operator pipeline and the deviation is recorded in
-- docs/security/resolver-review.md.
--
-- LOCKS. One new table and new functions: `create table` and `create function` take no lock on an
-- existing relation, but the foreign keys take SHARE ROW EXCLUSIVE on organization and "user", and
-- `create policy` takes ACCESS EXCLUSIVE on organization, project and test_case (a catalogue write only,
-- no row is read). The runner uses one transaction per script, so those locks last to the end of this
-- file; the policies are its last statements to keep that window short. `lock_timeout` bounds each wait.
--
-- ROLLBACK (application first: nothing may present a grant to the database):
--   drop policy if exists organization_operator_grant_read on public.organization;
--   drop policy if exists project_operator_grant_read on public.project;
--   drop policy if exists test_case_operator_grant_read on public.test_case;
--   drop policy if exists support_grant_operator_grant_read on public.support_grant;
--   drop function if exists evidgate.resolve_active_grant(uuid, uuid);
--   drop function if exists evidgate.operator_grant_ok(uuid, uuid, text);
--   drop function if exists evidgate.operator_grant_read_projects();
--   drop function if exists evidgate.operator_grant_id();
--   drop function if exists evidgate.operator_grant_org();
--   drop table if exists public.support_grant;
--   drop function if exists public.support_grant_guard();
-- The operator returns to SELECT with no policy (zero rows). Grants already filed are lost with the
-- table: export them first if the audit trail must survive.
set local lock_timeout = '5s';

create table if not exists public.support_grant (
  id uuid not null default uuidv7(),
  org_id uuid not null,
  operator_user_id uuid not null,
  scopes text[] not null,
  project_ids uuid[] not null default '{}',
  reason text not null,
  approved_by uuid,
  approved_at timestamptz,
  expires_at timestamptz not null,
  revoked_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint support_grant_pkey primary key (id),
  constraint support_grant_org_id_key unique (org_id, id),
  constraint support_grant_org_fk foreign key (org_id) references public.organization (id),
  constraint support_grant_operator_fk foreign key (operator_user_id) references public."user" (id),
  constraint support_grant_approver_fk foreign key (approved_by) references public."user" (id),
  constraint support_grant_scopes_check check (
    pg_catalog.array_ndims(scopes) = 1 and cardinality(scopes) between 1 and 64
    and pg_catalog.array_position(scopes, null) is null
    and pg_catalog.array_to_string(scopes, ',')
        ~ '^[a-z_]+:([a-z_]+|\*)(,[a-z_]+:([a-z_]+|\*))*$'
    and pg_catalog.char_length(pg_catalog.array_to_string(scopes, ',')) <= 4096
    and pg_catalog.cardinality(pg_catalog.string_to_array(pg_catalog.array_to_string(scopes, ','), ',')) = cardinality(scopes)),
  constraint support_grant_project_ids_check check (
    (project_ids = '{}' or pg_catalog.array_ndims(project_ids) = 1) and cardinality(project_ids) <= 100 and pg_catalog.array_position(project_ids, null) is null),
  constraint support_grant_reason_check check (char_length(reason) between 1 and 1000),
  constraint support_grant_expiry_check check (expires_at > created_at),
  constraint support_grant_approval_pair_check check ((approved_at is null) = (approved_by is null)),
  constraint support_grant_approver_differs_check check (approved_by is null or approved_by <> operator_user_id),
  constraint support_grant_approved_after_created_check check (approved_at is null or approved_at >= created_at),
  constraint support_grant_revoked_after_created_check check (revoked_at is null or revoked_at >= created_at),
  constraint support_grant_version_check check (version >= 1)
);

-- The request-start lookup (operator, expiry) and the tenant's list (newest first).
create index if not exists support_grant_operator_expires_idx
  on public.support_grant (operator_user_id, expires_at);
create index if not exists support_grant_org_created_idx
  on public.support_grant (org_id, created_at, id);
create index if not exists support_grant_approved_by_idx
  on public.support_grant (approved_by) where approved_by is not null;

-- One guard for every statement that reaches a row. 23514 is the shape of the other state guards.
create or replace function public.support_grant_guard() returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if tg_op = 'DELETE' then
    raise exception 'support_grant % is never deleted', old.id using errcode = '23514';
  end if;

  if tg_op = 'INSERT' then
    if new.approved_at is not null or new.approved_by is not null or new.revoked_at is not null then
      raise exception 'support_grant is filed as requested; approval and revocation are updates' using errcode = '23514';
    end if;
    if cardinality(new.project_ids) > 0
       and (select count(*) from public.project p where p.org_id = new.org_id and p.id = any (new.project_ids))
           <> (select count(distinct x) from pg_catalog.unnest(new.project_ids) as x) then
      raise exception 'support_grant lists a project that is not in the organization' using errcode = '23503';
    end if;
    return new;
  end if;

  if (to_jsonb(new) - array['approved_by', 'approved_at', 'revoked_at', 'updated_at', 'version'])
     is distinct from
     (to_jsonb(old) - array['approved_by', 'approved_at', 'revoked_at', 'updated_at', 'version']) then
    raise exception 'support_grant % identity, scope and expiry are immutable', old.id using errcode = '23514';
  end if;

  if old.revoked_at is not null then
    raise exception 'support_grant % is revoked; a revoked grant accepts no change', old.id using errcode = '23514';
  end if;

  if old.approved_at is not null
     and (new.approved_at is distinct from old.approved_at or new.approved_by is distinct from old.approved_by) then
    raise exception 'support_grant % approval is write-once', old.id using errcode = '23514';
  end if;

  if old.approved_at is null and new.approved_at is not null then
    if new.revoked_at is not null then
      raise exception 'support_grant % cannot be approved and revoked in one change', old.id using errcode = '23514';
    end if;
    if new.expires_at <= statement_timestamp() then
      raise exception 'support_grant % has expired and cannot be approved', old.id using errcode = '23514';
    end if;
    -- The decision time is the server's, never a value the caller picked.
    new.approved_at := statement_timestamp();
  end if;

  if old.revoked_at is null and new.revoked_at is not null then
    new.revoked_at := statement_timestamp();
  end if;

  return new;
end
$$;

revoke all on function public.support_grant_guard() from public;

drop trigger if exists support_grant_guard on public.support_grant;
create trigger support_grant_guard before insert or update or delete on public.support_grant
  for each row execute function public.support_grant_guard();
drop trigger if exists support_grant_set_updated_at_and_version on public.support_grant;
create trigger support_grant_set_updated_at_and_version before update on public.support_grant
  for each row execute function public.set_updated_at_and_version();

alter table public.support_grant enable row level security;
alter table public.support_grant force row level security;

drop policy if exists support_grant_app_tenant on public.support_grant;
create policy support_grant_app_tenant on public.support_grant for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists support_grant_owner_backfill on public.support_grant;
create policy support_grant_owner_backfill on public.support_grant for all to evidgate_owner
  using (true) with check (true);

-- 0001's default privileges hand the app role full DML and the operator SELECT: take both back to
-- what the code needs. The app files a grant, approves it and revokes it (three columns); nothing
-- deletes. The operator reads grants only through the policy below.
revoke all on public.support_grant from public;
revoke all on public.support_grant from evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant select on public.support_grant to evidgate_app;
grant insert (id, org_id, operator_user_id, scopes, project_ids, reason, expires_at) on public.support_grant to evidgate_app;
grant update (approved_by, approved_at, revoked_at) on public.support_grant to evidgate_app;
grant select on public.support_grant to evidgate_operator;

-- lint: audit-deferred policy-predicate evidgate.operator_grant_org, evidgate.operator_grant_id, evidgate.operator_grant_read_projects, evidgate.operator_grant_ok, evidgate.resolve_active_grant

-- The org of the caller's valid grant, or NULL. A malformed setting is NULL, not an error, so a bad
-- client string never turns a read into a server error that differs from "no grant".
create or replace function evidgate.operator_grant_org() returns uuid
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_grant text := nullif(current_setting('evidgate.support_grant_id', true), '');
  v_user text := nullif(current_setting('evidgate.operator_user_id', true), '');
  v_org uuid;
begin
  if v_grant is null or v_user is null
     or v_grant !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     or v_user !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then
    return null;
  end if;

  select g.org_id
    into v_org
    from public.support_grant g
   where g.id = v_grant::uuid
     and g.operator_user_id = v_user::uuid
     and g.approved_at is not null
     and g.revoked_at is null
     and g.expires_at > statement_timestamp();

  return v_org;
end
$$;

-- The id of the caller's valid grant, or NULL: the one grant row an operator may see of the support_grant
-- table, never another operator's or the org's other grants.
create or replace function evidgate.operator_grant_id() returns uuid
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_grant text := nullif(current_setting('evidgate.support_grant_id', true), '');
  v_user text := nullif(current_setting('evidgate.operator_user_id', true), '');
  v_id uuid;
begin
  if v_grant is null or v_user is null
     or v_grant !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     or v_user !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then
    return null;
  end if;

  select g.id
    into v_id
    from public.support_grant g
   where g.id = v_grant::uuid
     and g.operator_user_id = v_user::uuid
     and g.approved_at is not null
     and g.revoked_at is null
     and g.expires_at > statement_timestamp();

  return v_id;
end
$$;

-- The projects the caller's valid grant lets it read, or an empty array: one value per statement, so a
-- project-keyed policy compares each row with it instead of validating the grant once per row.
create or replace function evidgate.operator_grant_read_projects() returns uuid[]
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_grant text := nullif(current_setting('evidgate.support_grant_id', true), '');
  v_user text := nullif(current_setting('evidgate.operator_user_id', true), '');
  v_projects uuid[];
begin
  if v_grant is null or v_user is null
     or v_grant !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     or v_user !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then
    return '{}';
  end if;

  select g.project_ids
    into v_projects
    from public.support_grant g
   where g.id = v_grant::uuid
     and g.operator_user_id = v_user::uuid
     and g.approved_at is not null
     and g.revoked_at is null
     and g.expires_at > statement_timestamp()
     and exists (
       select 1
         from pg_catalog.unnest(g.scopes) as s(scope)
        where pg_catalog.split_part(s.scope, ':', 2) in ('read', '*'));

  return coalesce(v_projects, '{}');
end
$$;

-- True when the caller's valid grant belongs to `p_org_id`, covers `p_project_id` (NULL on an
-- org-scoped table: any valid grant of the org) and names a scope of the action class `p_action`
-- ('read' or 'write'). Any other action is false.
create or replace function evidgate.operator_grant_ok(p_org_id uuid, p_project_id uuid, p_action text)
returns boolean
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_grant text := nullif(current_setting('evidgate.support_grant_id', true), '');
  v_user text := nullif(current_setting('evidgate.operator_user_id', true), '');
begin
  if p_org_id is null or p_action is null or p_action not in ('read', 'write')
     or v_grant is null or v_user is null
     or v_grant !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     or v_user !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then
    return false;
  end if;

  return exists (
    select 1
      from public.support_grant g
     where g.id = v_grant::uuid
       and g.operator_user_id = v_user::uuid
       and g.org_id = p_org_id
       and g.approved_at is not null
       and g.revoked_at is null
       and g.expires_at > statement_timestamp()
       and (p_project_id is null or p_project_id = any (g.project_ids))
       and exists (
         select 1
           from pg_catalog.unnest(g.scopes) as s(scope)
          where pg_catalog.split_part(s.scope, ':', 2) = '*'
             or (pg_catalog.split_part(s.scope, ':', 2) = 'read') = (p_action = 'read')));
end
$$;

-- The operator's active grants in one org, for the request-start lookup: the operator holds no
-- membership, so the table is reached through this owner function (the `evidgate_resolve` shape).
-- The operator id is the caller's claim, the same trust as the settings above.
create or replace function evidgate.resolve_active_grant(p_operator_user_id uuid, p_org_id uuid)
returns table (grant_id uuid, org_id uuid, scopes text[], project_ids uuid[], expires_at timestamptz, approved_by uuid)
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_user text := nullif(current_setting('evidgate.operator_user_id', true), '');
begin
  -- Only the caller's own grants: the argument must equal the operator the session claims to be, so the
  -- function never lists another operator's grant ids.
  if v_user is null or v_user !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     or p_operator_user_id is null or v_user::uuid <> p_operator_user_id then
    return;
  end if;

  return query
    select g.id, g.org_id, g.scopes, g.project_ids, g.expires_at, g.approved_by
      from public.support_grant g
     where g.operator_user_id = p_operator_user_id
       and g.org_id = p_org_id
       and g.approved_at is not null
       and g.revoked_at is null
       and g.expires_at > statement_timestamp()
     order by g.expires_at desc, g.id;
end
$$;

revoke all on function evidgate.operator_grant_org() from public;
revoke all on function evidgate.operator_grant_org() from evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator;
grant execute on function evidgate.operator_grant_org() to evidgate_operator;
revoke all on function evidgate.operator_grant_id() from public;
revoke all on function evidgate.operator_grant_id() from evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator;
grant execute on function evidgate.operator_grant_id() to evidgate_operator;
revoke all on function evidgate.operator_grant_read_projects() from public;
revoke all on function evidgate.operator_grant_read_projects() from evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator;
grant execute on function evidgate.operator_grant_read_projects() to evidgate_operator;
revoke all on function evidgate.operator_grant_ok(uuid, uuid, text) from public;
revoke all on function evidgate.operator_grant_ok(uuid, uuid, text) from evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator;
grant execute on function evidgate.operator_grant_ok(uuid, uuid, text) to evidgate_operator;
revoke all on function evidgate.resolve_active_grant(uuid, uuid) from public;
revoke all on function evidgate.resolve_active_grant(uuid, uuid) from evidgate_app, evidgate_worker, evidgate_worker_sched, evidgate_platform_operator;
grant execute on function evidgate.resolve_active_grant(uuid, uuid) to evidgate_operator;

-- The operator read policies of the first set. Each already has SELECT for the operator.
drop policy if exists support_grant_operator_grant_read on public.support_grant;
create policy support_grant_operator_grant_read on public.support_grant for select to evidgate_operator
  using (org_id = (select evidgate.operator_grant_org())
         and id = (select evidgate.operator_grant_id()));

drop policy if exists organization_operator_grant_read on public.organization;
create policy organization_operator_grant_read on public.organization for select to evidgate_operator
  using (id = (select evidgate.operator_grant_org())
         and evidgate.operator_grant_ok(id, null, 'read'));

drop policy if exists project_operator_grant_read on public.project;
create policy project_operator_grant_read on public.project for select to evidgate_operator
  using (org_id = (select evidgate.operator_grant_org())
         and id = any (((select evidgate.operator_grant_read_projects())::uuid[])));

drop policy if exists test_case_operator_grant_read on public.test_case;
create policy test_case_operator_grant_read on public.test_case for select to evidgate_operator
  using (org_id = (select evidgate.operator_grant_org())
         and project_id = any (((select evidgate.operator_grant_read_projects())::uuid[])));
