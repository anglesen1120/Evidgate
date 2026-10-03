-- 0014_identity_resolve_member_orgs.sql
--
-- Owner: 02.6.1-T3.1 (first resolvers of the `evidgate_resolve` schema that 0002
-- created empty). A request authenticates before any tenant exists, so "which orgs
-- is this user a member of" cannot be asked under RLS: the app policy shows a
-- caller exactly one org. These two functions are the sanctioned way across that
-- boundary (00 §5.4.8, TL T1):
--
--   * `evidgate_resolve.member_orgs(user_id)` — the pre-tenant check. One row per
--     org where the user holds an ACTIVE membership, in the T1 tuple shape
--     `(org_id, project_id, binding_id, status)`; `project_id` and `binding_id` are
--     NULL for a member (they exist for the CI resolvers of 02.4.x), `status` is the
--     ORG's status so the entry point can tell an active org from a suspended one.
--     The tenant resolver of 02.6.2 calls it on EVERY request (no cache): removing a
--     membership takes effect on the next call.
--   * `evidgate_resolve.member_organizations(user_id, limit)` — the same set with the
--     slug and name `GET /v1/orgs` shows, bounded by `limit`. Kept apart so the hot
--     path above returns the contract tuple and nothing else.
--
-- Why this works: the functions are owned by `evidgate_owner`, and the
-- `*_owner_backfill` policies of 0004/0011 let that role read `organization` and
-- `membership` under FORCE ROW LEVEL SECURITY. `evidgate_app` gets EXECUTE only
-- (default privileges of 0002, restated below) and still has no way to read the
-- other org's rows directly.
--
-- Hygiene of every SECURITY DEFINER function (TL T80): `SET search_path = ''` and
-- schema-qualified references only, so an object the caller creates in `public`
-- cannot shadow a table or operator; no dynamic SQL; PUBLIC has no EXECUTE.
--
-- lint: audit-deferred 02.7.1-T2.2
-- The per-call audit stub of 02.6.1 AC6 is not written here: `audit_log` is 02.7.1-T1's
-- table and does not exist at 0014. 02.7.1-T2.2 replaces these functions with
-- audited versions (a new migration, never an edit of this file).

create index if not exists membership_user_active_idx
  on public.membership (user_id, org_id)
  where status = 'active' and user_id is not null;

create or replace function evidgate_resolve.member_orgs(p_user_id uuid)
returns table (org_id uuid, project_id uuid, binding_id uuid, status text)
language sql
stable
security definer
set search_path = ''
as $$
  select m.org_id, null::uuid, null::uuid, o.status
    from public.membership m
    join public.organization o on o.id = m.org_id
   where m.user_id = p_user_id
     and m.status = 'active'
     and o.status <> 'deleted'
   order by m.org_id
   limit 200
$$;

create or replace function evidgate_resolve.member_organizations(p_user_id uuid, p_limit integer)
returns table (org_id uuid, slug text, name text, status text)
language sql
stable
security definer
set search_path = ''
as $$
  select o.id, o.slug, o.name, o.status
    from public.membership m
    join public.organization o on o.id = m.org_id
   where m.user_id = p_user_id
     and m.status = 'active'
     and o.status <> 'deleted'
   order by o.id
   limit least(greatest(coalesce(p_limit, 50), 1), 200)
$$;

revoke all on function evidgate_resolve.member_orgs(uuid) from public;
revoke all on function evidgate_resolve.member_organizations(uuid, integer) from public;
grant execute on function evidgate_resolve.member_orgs(uuid) to evidgate_app;
grant execute on function evidgate_resolve.member_organizations(uuid, integer) to evidgate_app;
