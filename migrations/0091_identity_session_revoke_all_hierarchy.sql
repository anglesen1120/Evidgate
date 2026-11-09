-- 0091_identity_session_revoke_all_hierarchy.sql
--
-- WHY: `session_revoke_all_admin` (0085, 0090) let any caller holding an active `membership:write` in the org end
-- every session of ANY other active member, including a `tenant_admin`. A member administrator could therefore
-- sign out the org's administrators. The route now enforces a hierarchy:
--
--   * a caller may end all sessions of a target only when the target's highest rank in the acting org is not
--     above the caller's; only a `tenant_admin` (the org's top role, the one its creator gets) can end a
--     `tenant_admin`'s sessions;
--   * a caller addressing itself is refused here too (it already was, by the definer; the self-service routes
--     are the way to end one's own sessions).
--
-- RANK: the platform has no role-ordering table, so the order lives in ONE function,
-- `evidgate.session_admin_rank(p_org_id, p_user_id)`: 2 = holds the built-in `tenant_admin` role, 1 = holds
-- `membership:write` through some other role (a custom administrator), 0 = anything else. It is computed from
-- the ACTING org's grants only: a `tenant_admin` of another org is rank 0 here. Rank is derived from the
-- role and its permission at call time, never stored in a column or table. The `membership:write` join
-- that 0090 kept inside `session_admin_may_revoke` moves into `session_admin_holds_write` so both callers share
-- one copy; `session_admin_may_revoke` keeps its signature and answers exactly as before (the single-session
-- admin revoke, `session_revoke_one`, still uses it unchanged: the hierarchy is for revoke-all only).
--
-- ENFORCEMENT: `session_admin_may_revoke_all` = `session_admin_may_revoke` AND rank(actor) >= rank(target). The
-- revoke-all definer asks it twice, as 0090 did: before and again after the target's user lock is held, so a
-- role change that committed during the lock wait is seen. Every refusal is the same `not_found`.
--
-- AUDIT: unchanged. The one `auth.session_revoke_admin` row stays in the ACTING org's chain, written in the
-- revoke's transaction; it is not copied into any other org of the target.
--
-- GRANTS: the three new/changed helpers are invoker-rights functions reachable only through the owner-run
-- definers; EXECUTE is revoked from PUBLIC and every platform role, exactly as 0090 does.
--
-- Rollback: re-state the 0090 body of `session_revoke_all_admin` and of `session_admin_may_revoke`, then drop
-- `session_admin_may_revoke_all`, `session_admin_rank` and `session_admin_holds_write`. No table, column, row or
-- index changes.
create or replace function evidgate.session_admin_holds_write(
  p_actor uuid,
  p_org_id uuid)
returns boolean
language sql
volatile
set search_path = ''
as $$
  select exists (
    select 1
      from public.membership m
      join public.membership_role mr
           on mr.membership_id = m.id
          and mr.org_id = p_org_id
      join public.role r
           on r.id = mr.role_id
          and r.kind = 'member'
          -- `membership_role.role_id` carries no foreign key, so a row written around the commands could
          -- point a membership at another org's custom role; a built-in role (`org_id is null`) is
          -- platform-wide, a custom role only grants inside its own org.
          and (r.org_id is null or r.org_id = p_org_id)
      join public.role_permission rp
           on rp.role_id = r.id
          and rp.org_id is not distinct from r.org_id
      join public.permission p
           on p.code = rp.permission_code
     where m.org_id = p_org_id
       and m.user_id = p_actor
       and m.status = 'active'
       and p.tenant_assignable
       and rp.permission_code = 'membership:write')
$$;

create or replace function evidgate.session_admin_may_revoke(
  p_actor uuid,
  p_org_id uuid,
  p_target_user_id uuid)
returns boolean
language sql
volatile
set search_path = ''
as $$
  select evidgate.session_admin_holds_write(p_actor, p_org_id)
  -- An unknown user has no membership here, so it is refused the same way a non-member is.
  and exists (
    select 1
      from public.membership m
     where m.org_id = p_org_id
       and m.user_id = p_target_user_id
       and m.status = 'active')
$$;

create or replace function evidgate.session_admin_rank(
  p_org_id uuid,
  p_user_id uuid)
returns integer
language sql
volatile
set search_path = ''
as $$
  select case
           when exists (
             select 1
               from public.membership m
               join public.membership_role mr
                    on mr.membership_id = m.id
                   and mr.org_id = p_org_id
               join public.role r
                    on r.id = mr.role_id
                   and r.org_id is null
                   and r.code = 'tenant_admin'
              where m.org_id = p_org_id
                and m.user_id = p_user_id
                and m.status = 'active') then 2
           when evidgate.session_admin_holds_write(p_user_id, p_org_id) then 1
           else 0
         end
$$;

create or replace function evidgate.session_admin_may_revoke_all(
  p_actor uuid,
  p_org_id uuid,
  p_target_user_id uuid)
returns boolean
language sql
volatile
set search_path = ''
as $$
  select p_actor is distinct from p_target_user_id
     and evidgate.session_admin_may_revoke(p_actor, p_org_id, p_target_user_id)
     and evidgate.session_admin_rank(p_org_id, p_actor) >= evidgate.session_admin_rank(p_org_id, p_target_user_id)
$$;

revoke all on function evidgate.session_admin_holds_write(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
revoke all on function evidgate.session_admin_may_revoke(uuid, uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
revoke all on function evidgate.session_admin_rank(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
revoke all on function evidgate.session_admin_may_revoke_all(uuid, uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;

create or replace function evidgate_resolve.session_revoke_all_admin(
  p_cookie_hmacs bytea[],
  p_target_user_id uuid,
  p_org_id uuid,
  p_now timestamptz,
  p_request_id text,
  p_trace_id text)
returns table (status text, revoked_count integer)
language plpgsql
volatile
security definer
set search_path = ''
set lock_timeout = '2000'
as $$
declare
  v_now timestamptz;
  v_actor uuid;
  v_revoked integer := 0;
begin
  -- The server clock is the ceiling: `p_now` may look back, never ahead (0048's rule). `least` ignores a NULL,
  -- so a NULL means the clock, never "nothing live"; the app role is the only caller and always passes one.
  v_now := least(p_now, clock_timestamp());

  select s.user_id
    into v_actor
    from public.session s
   where s.cookie_hmac = any (p_cookie_hmacs)
     and s.revoked_at is null
     and s.expires_at > v_now
     and s.absolute_expires_at > v_now;

  if v_actor is null then
    return query select 'unauthenticated'::text, 0;
    return;
  end if;

  -- Every refusal below is the SAME `not_found`: the target is the caller (who has the self-service route),
  -- the org is absent, the caller does not administer it, the target is not an active member, or the target
  -- ranks above the caller in this org (only a `tenant_admin` may end a `tenant_admin`'s sessions).
  if p_org_id is null or p_target_user_id is null or p_target_user_id = v_actor
     or evidgate.session_admin_may_revoke_all(v_actor, p_org_id, p_target_user_id) is not true
  then
    return query select 'not_found'::text, 0;
    return;
  end if;

  -- The first of the two locks (see 0085): every writer of this user's sessions takes it, so a concurrent
  -- login or rotation waits here instead of adding a live row behind the revoke.
  perform evidgate.session_user_lock(p_target_user_id);

  -- The answer above predates the lock wait: a removal that committed meanwhile must stop this call, so the
  -- caller and the target are proved again from a fresh snapshot.
  if evidgate.session_admin_may_revoke_all(v_actor, p_org_id, p_target_user_id) is not true then
    return query select 'not_found'::text, 0;
    return;
  end if;

  -- The same predicates `session_revoke_all_for_user` uses for a live row: only a session that is neither
  -- revoked nor past either deadline is ended, so the count is exact and a second call changes nothing.
  update public.session s
     set revoked_at = v_now,
         rotation_required_at = null
   where s.user_id = p_target_user_id
     and s.revoked_at is null
     and s.expires_at > v_now
     and s.absolute_expires_at > v_now;
  get diagnostics v_revoked = row_count;

  if v_revoked > 0 then
    -- The second lock is taken inside the helper, under the same wait bound.
    perform evidgate.session_revoke_audit_append(
      p_org_id, v_actor, 'user', p_target_user_id::text,
      jsonb_build_object('target_user_id', p_target_user_id::text, 'revoked_count', v_revoked::text),
      p_request_id, p_trace_id);
  end if;

  -- Zero revoked is a success: the member had no live session, so there was nothing to record.
  return query select 'revoked'::text, v_revoked;
end
$$;

revoke all on function evidgate_resolve.session_revoke_all_admin(bytea[], uuid, uuid, timestamptz, text, text) from public;
grant execute on function evidgate_resolve.session_revoke_all_admin(bytea[], uuid, uuid, timestamptz, text, text) to evidgate_app;
