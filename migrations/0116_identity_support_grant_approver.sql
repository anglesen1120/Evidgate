-- 0116_identity_support_grant_approver.sql
--
-- Approval gains a holder check: the approver must be an active member of the grant's own organization
-- and hold the built-in `tenant_admin` role there. Until this file `approved_by` accepted any user id,
-- so a caller could record an approval by someone who never gave one, or approve while holding no
-- tenant role at all. The rule runs inside the existing guard trigger, so it changes no table, policy
-- or grant; the function body is 0115's plus one clause.
--
-- WHY EVERY ROLE. The check is a trigger, not a policy or a command: `evidgate_app`, `evidgate_owner`
-- and a superuser all fire it, so a row cannot become approved through any path unless the membership
-- and its role grant are committed and visible. A guard that only the app role could bypass would fail
-- open for the migration owner and for a hand-run statement.
--
-- WHY FOR SHARE OF m, mr. The clause takes a share lock on the membership row and on the
-- `membership_role` row it matched. A concurrent removal (`status = 'removed'`) or role revoke then
-- either waits for the approving transaction to finish, or has already committed and is seen by this
-- statement, so an approval and a removal cannot interleave into "approved by a non-admin".
--
-- ANOTHER ORG. `m.org_id = new.org_id` is part of the predicate: a user who is only a member of a
-- different organization yields no row and the approval is refused. For the app role that row is also
-- hidden by the `membership` policy (`org_id = evidgate.current_org_id()`), so both readings refuse.
-- The same holds for a membership whose `membership_role` names a custom role that happens to be
-- called `tenant_admin`: the clause requires `r.org_id is null and r.is_builtin`.
--
-- THE MESSAGE IS THE CONTRACT. SQLSTATE 23514 with this exact text is what the approve command maps
-- to the API error `approver_not_tenant_admin`; change one without the other and the mapping breaks.
--
-- LOCKS. `create or replace function` takes no relation lock, and the trigger keeps its binding, so
-- neither the trigger nor the table is touched. `lock_timeout` bounds a wait for a concurrent
-- replacement of the same function.
--
-- ROLLBACK: restore 0115's `public.support_grant_guard()` body (the same function without the
-- approver clause); no table, index, policy, privilege or trigger changes, so nothing else is undone.
-- Re-running this file re-tightens the rule.
set local lock_timeout = '5s';

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
    -- The approver's authority is read at approval time, under a share lock, so a removal that
    -- overlaps this transaction cannot land underneath it.
    if not exists (
      select 1
        from public.membership m
        join public.membership_role mr
          on mr.org_id = m.org_id and mr.membership_id = m.id
        join public.role r
          on r.id = mr.role_id
       where m.org_id = new.org_id
         and m.user_id = new.approved_by
         and m.status = 'active'
         and r.org_id is null
         and r.is_builtin
         and r.code = 'tenant_admin'
       for share of m, mr) then
      raise exception 'support_grant % approver is not an active tenant_admin of the organization', old.id using errcode = '23514';
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
