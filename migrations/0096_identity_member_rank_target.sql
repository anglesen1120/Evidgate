-- 0096_identity_member_rank_target.sql
--
-- WHY: `0093` let the member commands (`RemoveMember`, `UpdateMembership`, `RevokeRole`) ask
-- `evidgate.session_admin_rank(org, user)` for the target's rank. That function counts ACTIVE memberships only,
-- so a removed or pending target ranked 0 and a lower-rank administrator could still edit the grants such a row
-- carries (for example `RevokeRole` on a removed owner's row, or cancelling a pending owner invite). The target of
-- a member command is a MEMBERSHIP, and an unbound invite has no user at all, so the rank of a target is now read
-- from the membership row itself, whatever its status.
--
-- SHAPE: `evidgate.membership_rank(p_org_id, p_membership_id)` = 2 when the row holds the built-in `tenant_admin`
-- of that org, 1 when it holds `membership:write` through another member role, 0 otherwise, with NO status filter.
-- `evidgate.membership_holds_write(p_org_id, p_membership_id)` carries the `membership:write` join that
-- `session_admin_holds_write` used to hold. The user-keyed functions of `0091` keep their signatures and answers
-- (actor rank is "active membership only": a removed actor has no rank) but now delegate to the membership-keyed
-- pair, so the rank rule still has one copy. `session_admin_may_revoke_all` and the revoke-all definer are
-- unchanged and keep asking the user-keyed functions.
--
-- GRANTS: the two new functions are invoker-rights (`set search_path = ''`), so the request's `evidgate.org_id`
-- and the policies of `membership`, `membership_role`, `role` and `role_permission` apply exactly as for `0093`:
-- another org's membership id ranks 0 under this org. EXECUTE goes to `evidgate_app` only; PUBLIC,
-- `evidgate_operator` and `evidgate_worker_sched` are restated as EXECUTE-less. `create or replace` keeps the
-- ACL of the two user-keyed functions, and the file restates it so a re-run is a no-op.
--
-- ATOMICITY: the comparison is made atomic in the commands, not here. They lock the target membership row and the
-- actor's membership row (`for update`, one statement, ordered by id) before the one authoritative comparison; a
-- concurrent grant insert takes a key-share lock on the membership row through its foreign key, so it waits for
-- that lock instead of slipping between check and write.
--
-- Deploy the migration first, then the application (the commands call `membership_rank`); rolling the application
-- back first is safe. Rollback: re-state the `0091` bodies of `session_admin_holds_write` and `session_admin_rank`,
-- then drop `membership_rank` and `membership_holds_write`. No table, column, row or policy changes.
set local lock_timeout = '5s';

create or replace function evidgate.membership_holds_write(
  p_org_id uuid,
  p_membership_id uuid)
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
       and m.id = p_membership_id
       and p.tenant_assignable
       and rp.permission_code = 'membership:write')
$$;

create or replace function evidgate.membership_rank(
  p_org_id uuid,
  p_membership_id uuid)
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
                and m.id = p_membership_id) then 2
           when evidgate.membership_holds_write(p_org_id, p_membership_id) then 1
           else 0
         end
$$;

-- The user-keyed pair answers as before (active memberships only) and now shares the membership-keyed copy.
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
     where m.org_id = p_org_id
       and m.user_id = p_actor
       and m.status = 'active'
       and evidgate.membership_holds_write(p_org_id, m.id))
$$;

create or replace function evidgate.session_admin_rank(
  p_org_id uuid,
  p_user_id uuid)
returns integer
language sql
volatile
set search_path = ''
as $$
  select coalesce(max(evidgate.membership_rank(p_org_id, m.id)), 0)
    from public.membership m
   where m.org_id = p_org_id
     and m.user_id = p_user_id
     and m.status = 'active'
$$;

revoke all on function evidgate.membership_holds_write(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
revoke all on function evidgate.membership_rank(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
grant execute on function evidgate.membership_holds_write(uuid, uuid) to evidgate_app;
grant execute on function evidgate.membership_rank(uuid, uuid) to evidgate_app;

revoke all on function evidgate.session_admin_holds_write(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
grant execute on function evidgate.session_admin_holds_write(uuid, uuid) to evidgate_app;
revoke all on function evidgate.session_admin_rank(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
grant execute on function evidgate.session_admin_rank(uuid, uuid) to evidgate_app;
