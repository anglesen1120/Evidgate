-- 0101_identity_revoke_rank_and_atomic_link_audit.sql
--
-- WHY (two defects of the CR-22 register row):
--
--   (a) `evidgate_resolve.session_revoke_one` (0051, 0090) let any active `membership:write` holder of the acting
--       org end ONE session of any active member, including a `tenant_admin`'s, while `session_revoke_all_admin`
--       (0091) refuses a target that outranks the caller. The single-session path now asks
--       `evidgate.session_admin_may_revoke_all` (membership:write + active target + rank(actor) >= rank(target)),
--       so both admin routes answer the same question. A refusal is the same `not_found` as an unknown id.
--       A self revoke keeps its own branch and writes no audit row. Sessions stay user-wide (0041, platform
--       scoped): an administrator of an org the target belongs to at a lower rank can still end the target's
--       sessions everywhere; that residual is the accepted owner decision recorded in 0091.
--
--   (b) Identity linking committed in `login_link_identity`, and the `auth.link` / `auth.link_move` audit rows
--       were written afterwards by the application, one transaction per org, so a failed audit left a committed
--       link without its row. `evidgate_resolve.login_link_identity_audited` calls `login_link_identity` and, in
--       the same transaction, appends the rows to the chain of every active org of the acting user through the
--       new helper `evidgate.audit_append_user_event`; a failing append (lock timeout, chain guard) aborts the
--       call and the link rolls back with it. Only a first-time `linked` or `moved` outcome owes rows: an
--       `already_linked` retry belongs to a link whose rows committed with it.
--
-- HELPER: `evidgate.audit_append_user_event` is the 0090 chain append with the action as an argument.
-- `session_revoke_audit_append` keeps its signature and delegates to it with `auth.session_revoke_admin`, so the
-- canonical bytes of every existing row are unchanged and there is ONE copy of the chain-append logic.
--
-- GRANTS: both helpers are invoker-rights and owner-only (EXECUTE revoked from PUBLIC and every platform role),
-- as in 0090. The new definer is `security definer`, `search_path = ''`, `lock_timeout = 2000`, EXECUTE to
-- `evidgate_app` only.
--
-- DEPLOY ORDER: migrate before the application (the new application calls the new definer).
-- Rollback: application first, then `drop function evidgate_resolve.login_link_identity_audited(bytea[],
-- timestamptz, text, text, text, uuid, text, text)`, re-state the 0090 bodies of `session_revoke_one` and
-- `session_revoke_audit_append`, then drop `evidgate.audit_append_user_event`. No table or row changes.
create or replace function evidgate.audit_append_user_event(
  p_action text,
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
    '{"action":%s,"actor_id":"%s","actor_type":"user","after_hash":null,"before_hash":null,'
    || '"created_at":"%s","id":"%s","metadata":%s,'
    || '"object_id":%s,"object_type":%s,"org_id":"%s","request_id":%s,"seq":%s,"trace_id":%s}',
    pg_catalog.to_jsonb(p_action)::text,
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
    (v_id, p_org_id, v_seq, 'user', p_actor::text, p_action, p_object_type,
     p_object_id, null, null, v_request_id, v_trace_id, p_metadata,
     v_prev_hash, pg_catalog.sha256(v_prev_hash || pg_catalog.convert_to(v_canonical, 'UTF8')), v_created_at);
end
$$;

revoke all on function evidgate.audit_append_user_event(text, uuid, uuid, text, text, jsonb, text, text)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;

create or replace function evidgate.session_revoke_audit_append(
  p_org_id uuid,
  p_actor uuid,
  p_object_type text,
  p_object_id text,
  p_metadata jsonb,
  p_request_id text,
  p_trace_id text)
returns void
language sql
volatile
set search_path = ''
as $$
  select evidgate.audit_append_user_event(
    'auth.session_revoke_admin', p_org_id, p_actor, p_object_type, p_object_id, p_metadata, p_request_id, p_trace_id)
$$;

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
       or evidgate.session_admin_may_revoke_all(v_actor, p_org_id, v_target_user) is not true
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

create or replace function evidgate_resolve.login_link_identity_audited(
  p_cookie_hmacs bytea[],
  p_now timestamptz,
  p_provider text,
  p_subject text,
  p_email text,
  p_identity_id uuid,
  p_request_id text default null,
  p_trace_id text default null)
returns table (outcome text, moved_from_user_id uuid)
language plpgsql
volatile
security definer
set search_path = ''
set lock_timeout = '2000'
as $$
declare
  v_outcome text;
  v_moved uuid;
  v_actor uuid;
  v_org uuid;
begin
  select l.outcome, l.moved_from_user_id
    into v_outcome, v_moved
    from evidgate_resolve.login_link_identity(
           p_cookie_hmacs, p_now, p_provider, p_subject, p_email, p_identity_id) as l;

  if v_outcome in ('linked', 'moved') then
    -- The same credential the link just accepted, so the acting user is the one it linked.
    select s.user_id
      into v_actor
      from public.session s
     where s.cookie_hmac = any (p_cookie_hmacs)
       and s.revoked_at is null
       and s.expires_at > p_now
       and s.absolute_expires_at > p_now;

    -- One chain per active org of the user, in org-id order (the application writer's order), so two
    -- concurrent links of one user take the chain locks in the same sequence.
    for v_org in
      select m.org_id
        from public.membership m
        join public.organization o on o.id = m.org_id
       where m.user_id = v_actor
         and m.status = 'active'
         and o.status = 'active'
       order by m.org_id
    loop
      perform evidgate.audit_append_user_event(
        'auth.link', v_org, v_actor, 'user', v_actor::text,
        jsonb_build_object('provider', p_provider), p_request_id, p_trace_id);

      if v_outcome = 'moved' then
        perform evidgate.audit_append_user_event(
          'auth.link_move', v_org, v_actor, 'user', v_actor::text,
          jsonb_build_object('provider', p_provider, 'previous_id', v_moved::text), p_request_id, p_trace_id);
      end if;
    end loop;
  end if;

  return query select v_outcome, v_moved;
end
$$;

revoke all on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz, text, text) from public;
grant execute on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz, text, text) to evidgate_app;
revoke all on function evidgate_resolve.login_link_identity_audited(bytea[], timestamptz, text, text, text, uuid, text, text) from public;
grant execute on function evidgate_resolve.login_link_identity_audited(bytea[], timestamptz, text, text, text, uuid, text, text) to evidgate_app;
