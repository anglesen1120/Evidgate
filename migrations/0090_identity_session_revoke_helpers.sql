-- 0090_identity_session_revoke_helpers.sql
--
-- WHY: `session_revoke_all_admin` (0085) carried a copy of two pieces of security logic that
-- `session_revoke_one` (0051) already owned: the `membership:write` join that proves the caller administers the
-- org (plus the target's ACTIVE membership), and the hash-chain append of the `auth.session_revoke_admin` audit
-- row. A fix to one copy had to be remembered in the other. This file moves both into two helpers of the
-- `evidgate` schema and has both definers call them.
--
-- SHAPE: two plain (invoker) functions, the shape `evidgate.session_user_lock` (0065) established. They run with
-- the privileges of the owner-run definer that calls them, and they are reachable from nothing else:
--
--   * `evidgate.session_admin_may_revoke(p_actor, p_org_id, p_target_user_id) returns boolean` is true only when
--     the actor holds an ACTIVE `membership:write` grant in the org (the role itself and its `role_permission`
--     rows included, a custom role only inside its own org) AND the target holds an ACTIVE membership in it.
--   * `evidgate.session_revoke_audit_append(...) returns void` takes the org chain lock, reads the tip, builds
--     the RFC 8785 canonical row and inserts it, exactly as 0051 and 0085 did. It also validates the two
--     correlation ids (a safe request id or null, a non-zero 32-hex trace id or null; an unacceptable value is
--     nulled, never an error). The metadata is passed as jsonb and serialised in key order (code-unit order), so
--     every existing row keeps the same bytes and the same entry hash.
--
-- ONE BEHAVIOUR FIX: `session_revoke_all_admin` authorised the caller BEFORE it waited for the target's user lock,
-- and trusted that answer afterwards. A member removal that committed during the wait (the removal definer takes
-- the same lock) left a revoked administrator able to end the sessions. The check now runs again right after the
-- lock is held, in a new statement and therefore a new snapshot; a caller who no longer qualifies gets the same
-- `not_found` every other refusal gets and nothing is revoked or recorded. This narrows the window to the
-- statements after the check; it does not make the check and the update one atomic step.
--
-- RESULTS: statuses, error codes, audit payloads and entry hashes of every existing case are unchanged. Both
-- definers keep their signatures, bodies' control flow, `security definer`, pinned `search_path` and
-- `lock_timeout`; `create or replace` keeps their ACLs and the grants are restated below anyway.
--
-- GRANTS: the audit helper must never be callable by a tenant-facing role (it writes a chain row for any org),
-- so EXECUTE is restated as revoked from PUBLIC and
-- from every non-owner role the platform runs as; only the definers' owner reaches the helpers.
--
-- DEPLOY ORDER: any order; the definers are replaced in the same transaction that creates the helpers.
--
-- Rollback: re-state the 0051 and 0085 bodies with `create or replace`, then `drop function` the two helpers.
-- No table, column, row or index changes; rows already written stay in their chains.
create or replace function evidgate.session_admin_may_revoke(
  p_actor uuid,
  p_org_id uuid,
  p_target_user_id uuid)
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
  -- An unknown user has no membership here, so it is refused the same way a non-member is.
  and exists (
    select 1
      from public.membership m
     where m.org_id = p_org_id
       and m.user_id = p_target_user_id
       and m.status = 'active')
$$;

create or replace function evidgate.session_revoke_audit_append(
  p_org_id uuid,
  p_actor uuid,
  p_object_type text,
  p_object_id text,
  p_metadata jsonb,
  p_request_id text,
  p_trace_id text)
returns void
language plpgsql
volatile
set search_path = ''
as $$
declare
  -- The correlation ids after validation and their canonical JSON spellings (`null` when absent): the row and
  -- its hash carry the same pair, so the canonical string is built from the validated values.
  v_request_id text;
  v_trace_id text;
  v_request_json text;
  v_trace_json text;
  v_metadata_json text;
  v_tip_seq bigint;
  v_tip_hash bytea;
  v_tip_at timestamptz;
  v_seq bigint;
  v_prev_hash bytea;
  v_created_at timestamptz;
  v_id uuid;
  v_canonical text;
begin
  -- Untrusted input (CorrelationNames.IsSafeRequestId): an unacceptable value becomes an absent one.
  v_request_id := case when p_request_id ~ '^[!-~]{1,128}$' then p_request_id end;
  v_trace_id := case
                  when p_trace_id ~ '^[0-9a-f]{32}$' and p_trace_id <> repeat('0', 32)
                  then p_trace_id
                end;

  -- The org chain is extended under the application writer's own lock, with the wait bound of the calling
  -- definer's `lock_timeout`. A timeout is 55P03 and aborts the caller's statement, so its revoke rolls back.
  perform pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtext('audit:' || p_org_id::text));

  select a.seq, a.entry_hash, a.created_at
    into v_tip_seq, v_tip_hash, v_tip_at
    from public.audit_log a
   where a.org_id = p_org_id
   order by a.seq desc
   limit 1;

  v_seq := coalesce(v_tip_seq, 0) + 1;
  v_prev_hash := coalesce(v_tip_hash, pg_catalog.decode(pg_catalog.repeat('00', 32), 'hex'));

  -- Never earlier than the tip: the chain guard of 0015 refuses a row that moves the chain back in time.
  v_created_at := greatest(pg_catalog.clock_timestamp(), coalesce(v_tip_at, '-infinity'::timestamptz));

  v_id := pg_catalog.uuidv7();

  -- `AuditHashing.CanonicalFields` hashes the raw column values: an absent id is the literal `null`, a present
  -- one a JSON string (visible ASCII, so only `"` and `\` need an escape, which `to_jsonb(text)` supplies).
  v_request_json := coalesce(pg_catalog.to_jsonb(v_request_id)::text, 'null');
  v_trace_json := coalesce(pg_catalog.to_jsonb(v_trace_id)::text, 'null');

  -- Metadata values are strings by platform rule (AuditRow.Metadata); keys go in code-unit order.
  select '{' || coalesce(pg_catalog.string_agg(
                  pg_catalog.to_jsonb(e.key)::text || ':' || pg_catalog.to_jsonb(e.value)::text,
                  ',' order by e.key collate "C"), '') || '}'
    into v_metadata_json
    from pg_catalog.jsonb_each_text(p_metadata) as e;

  -- The RFC 8785 canonical bytes of this row: keys in UTF-16 code-unit order, no whitespace, nulls spelled
  -- `null`, the timestamp with six fractional digits in UTC.
  v_canonical := pg_catalog.format(
    '{"action":"%s","actor_id":"%s","actor_type":"user","after_hash":null,"before_hash":null,'
    || '"created_at":"%s","id":"%s","metadata":%s,'
    || '"object_id":%s,"object_type":%s,"org_id":"%s","request_id":%s,"seq":%s,"trace_id":%s}',
    'auth.session_revoke_admin',
    p_actor::text,
    pg_catalog.to_char(v_created_at at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
    v_id::text,
    v_metadata_json,
    pg_catalog.to_jsonb(p_object_id)::text,
    pg_catalog.to_jsonb(p_object_type)::text,
    p_org_id::text,
    v_request_json,
    v_seq,
    v_trace_json);

  insert into public.audit_log
    (id, org_id, seq, actor_type, actor_id, action, object_type, object_id,
     before_hash, after_hash, request_id, trace_id, metadata, prev_hash, entry_hash, created_at)
  values
    (v_id, p_org_id, v_seq, 'user', p_actor::text, 'auth.session_revoke_admin', p_object_type,
     p_object_id, null, null, v_request_id, v_trace_id, p_metadata,
     v_prev_hash, pg_catalog.sha256(v_prev_hash || pg_catalog.convert_to(v_canonical, 'UTF8')), v_created_at);
end
$$;

-- Restated explicitly (owner-only is the intended ACL, pinned by a test).
revoke all on function evidgate.session_admin_may_revoke(uuid, uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
revoke all on function evidgate.session_revoke_audit_append(uuid, uuid, text, text, jsonb, text, text)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;

create or replace function evidgate_resolve.session_revoke_one(
  p_cookie_hmacs bytea[],
  p_session_id uuid,
  p_org_id uuid,
  p_now timestamptz,
  p_request_id text default null,
  p_trace_id text default null)
returns table (status text)
language plpgsql
volatile
security definer
set search_path = ''
set lock_timeout = '2000'
as $$
declare
  v_actor uuid;
  v_target_user uuid;
  v_target_live boolean;
  v_revoked_id uuid;
  -- True once the actor is authorized to end ANOTHER member's session: the only case that
  -- appends an audit row. A self revoke leaves it false and writes nothing.
  v_admin_revoke boolean := false;
begin
  select s.user_id
    into v_actor
    from public.session s
   where s.cookie_hmac = any (p_cookie_hmacs)
     and s.revoked_at is null
     and s.expires_at > p_now
     and s.absolute_expires_at > p_now;

  if v_actor is null then
    return query select 'unauthenticated'::text;
    return;
  end if;

  select s.user_id,
         s.revoked_at is null
         and s.expires_at > p_now
         and s.absolute_expires_at > p_now
    into v_target_user, v_target_live
    from public.session s
   where s.id = p_session_id;

  if v_target_user is null or v_target_live is not true then
    return query select 'not_found'::text;
    return;
  end if;

  if v_target_user <> v_actor then
    if p_org_id is null
       or evidgate.session_admin_may_revoke(v_actor, p_org_id, v_target_user) is not true
    then
      return query select 'not_found'::text;
      return;
    end if;

    -- Every refusal is behind us: this call ends ANOTHER member's session, which is the one
    -- shape that owes an audit row.
    v_admin_revoke := true;
  end if;

  update public.session s
     set revoked_at = p_now,
         rotation_required_at = null
   where s.id = p_session_id
     and s.revoked_at is null
     and s.expires_at > p_now
     and s.absolute_expires_at > p_now
  returning s.id into v_revoked_id;

  if v_revoked_id is null then
    -- Another writer got there first, or a deadline passed since the read above: the same
    -- `not_found` any dead target gets, because this call did not touch the row. No audit row:
    -- nothing was revoked.
    return query select 'not_found'::text;
    return;
  end if;

  if v_admin_revoke then
    perform evidgate.session_revoke_audit_append(
      p_org_id, v_actor, 'session', p_session_id::text,
      jsonb_build_object('target_user_id', v_target_user::text),
      p_request_id, p_trace_id);
  end if;

  return query select 'revoked'::text;
end
$$;

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
  -- the org is absent, the caller does not administer it, or the target is not an active member.
  if p_org_id is null or p_target_user_id is null or p_target_user_id = v_actor
     or evidgate.session_admin_may_revoke(v_actor, p_org_id, p_target_user_id) is not true
  then
    return query select 'not_found'::text, 0;
    return;
  end if;

  -- The first of the two locks (see 0085): every writer of this user's sessions takes it, so a concurrent
  -- login or rotation waits here instead of adding a live row behind the revoke.
  perform evidgate.session_user_lock(p_target_user_id);

  -- The answer above predates the lock wait: a removal that committed meanwhile must stop this call, so the
  -- caller and the target are proved again from a fresh snapshot.
  if evidgate.session_admin_may_revoke(v_actor, p_org_id, p_target_user_id) is not true then
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

-- Restated, not relied on: `create or replace` keeps the ACL, but the grant set is part of this file's contract.
revoke all on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz, text, text) from public;
grant execute on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz, text, text) to evidgate_app;
revoke all on function evidgate_resolve.session_revoke_all_admin(bytea[], uuid, uuid, timestamptz, text, text) from public;
grant execute on function evidgate_resolve.session_revoke_all_admin(bytea[], uuid, uuid, timestamptz, text, text) to evidgate_app;
