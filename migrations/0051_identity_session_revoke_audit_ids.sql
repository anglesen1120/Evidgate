-- 0051_identity_session_revoke_audit_ids.sql
--
-- Owner: F58 (closes the ONE declared loss of 0050; F58 is the coordinator row opened for
-- exactly that loss). 0050 moved the admin-revoke audit row INSIDE
-- `evidgate_resolve.session_revoke_one`, but the row had to leave `request_id` and `trace_id`
-- NULL: a function body cannot see the caller's ambient context, since 0050 no argument carried
-- either value, and `create or replace function` may not change a signature. This file changes
-- it the way the platform allows — a NEW six-argument definition, with the two correlation ids
-- as trailing arguments that default to NULL.
--
--   * `evidgate_resolve.session_revoke_one` gains `p_request_id text default null` and
--     `p_trace_id text default null`, keeping its return type (`table (status text)`), its
--     `SECURITY DEFINER` body, its pinned `set search_path = ''` and its writer-bound
--     `set lock_timeout = '2000'` exactly as 0050 left them. Everything else in the body is
--     0050's verbatim: the same three refusals, the same authorization join, the same
--     read-without-lock then guarded `update` as the arbiter, the same single-transaction
--     append to the org's chain under `hashtext('audit:' || p_org_id::text)`, the same
--     `greatest(clock_timestamp(), tip)` floor.
--
--   * The ids land in the audit row AND in its canonical string, exactly as the application
--     writer hashes them. AuditTrail.Record stamps the same two values from the ambient
--     Activity (src/BuildingBlocks/Persistence/Audit/AuditTrail.cs:95-101: the
--     `evidgate.request_id` baggage item and `Activity.TraceId.ToString()`), and
--     AuditHashing.CanonicalFields hashes them as the raw strings of the fourteen canonical
--     fields (src/BuildingBlocks/Persistence/Audit/AuditHashing.cs:38,40) — a JSON string, or
--     the literal `null` when absent. The body reproduces both spellings with
--     `to_jsonb(<text>)::text`, which is RFC 8785's string form for any ASCII-only value:
--     `"` and `\` escaped, everything else (visible ASCII) literal. The chain rule and the
--     entry hash are untouched, so `AuditChainVerifier` — the same verifier the
--     `audit.verify_chain` task runs — accepts the row without a second implementation.
--
--   * VALIDATION, and why it is this strict. Both arguments are caller-supplied (the request
--     path forwards a value an inbound header may have produced), so they are treated as
--     untrusted input and never reach the row unvalidated:
--       - `p_request_id` is accepted only when it matches `^[!-~]{1,128}$` — visible ASCII
--         0x21-0x7E, length 1..128 — which is `CorrelationNames.IsSafeRequestId`
--         (src/BuildingBlocks/Observability/Correlation/CorrelationNames.cs:53,70-88) and its
--         128-character bound `MaxRequestIdLength` (:53): the identical rule the edge and
--         AuditTrail's reader apply, so a value that is stored here is a value those accept.
--       - `p_trace_id` is accepted only when it matches `^[0-9a-f]{32}$` AND is not the
--         all-zero id. Lowercase 32 hex digits is what `Activity.TraceId.ToString()` yields,
--         and the all-zero value is W3C-invalid (`ActivityTraceId.CreateFromString` refuses it,
--         so no live span can produce it) — a client-supplied zero id would be a value the
--         application never writes and could not correlate with anything.
--     An invalid value is NULLED, and nothing else: no error, no refusal, no audit suppression.
--     A malformed correlation id must never be able to block a revocation, and an attacker must
--     never be able to steer what the chain records beyond "absent". The revoke itself answers
--     exactly the statuses it answered in 0050.
--
-- DEPLOY ORDER: MIGRATE BEFORE APP. The old application binds four arguments (positions 1-4);
-- they still bind, through the new defaults, so this file is deploy-safe in that direction and
-- an already-running API keeps working the moment it is applied. The NEW application binds six
-- (positions 5 and 6 are `p_request_id`/`p_trace_id`), so applying 0051 AFTER deploying the new
-- build is what fails: not deployed yet means ids are lost; deployed first means every
-- administrative revoke answers `42883` (undefined_function) until the migration lands. Hence
-- migrate, then deploy — the expand direction is the only one that works.
--
-- GRANTS. `drop function` (required: a signature change cannot be `create or replace`) also
-- drops the function's ACL, so the 0048:559/565 grants are re-stated for the NEW signature:
-- `revoke all ... from public` keeps the built-in PUBLIC grant closed, and EXECUTE stays with
-- `evidgate_app`, the one role that calls the route (TL T80). `evidgate_owner` owns the new
-- definition exactly as it owned the old one (the migration runner creates it).
--
-- Idempotent: `drop function if exists` names ONLY the four-argument predecessor (0050's), and
-- the six-argument definition is `create or replace`, so a second apply re-states the same body
-- in place — no drop of the live function, no ACL window. `revoke all` / `grant execute` at the
-- bottom are re-statements for the same reason. One file runs in one transaction (README
-- "File contract", `WithTransactionPerScript`), so the drop and the create cannot be observed
-- apart, and a first apply never leaves a moment with no `session_revoke_one` in the catalogue.
--
-- Scope: ONE function definition. No table, no column, no row, no index, no backfill — applying
-- this file changes no data, and rows already written by 0050 keep their NULL ids and still
-- verify (they are hashed with `null`, which is exactly what they contain).
--
-- Rollback (playbook `docs/migrations-expand-contract.md`, a NEW file, never an edit): apply the
-- reverse — `drop function if exists evidgate_resolve.session_revoke_one(bytea[], uuid, uuid,
-- timestamptz, text, text);` and re-state 0050's four-argument definition
-- (`0050_identity_session_revoke_audit.sql:108-290`), which restores the old signature and its
-- grants. The rows already written stay in the chain, with their ids: they record revocations
-- that happened, with the correlation they had. Reverting needs the OLD application build
-- first, since the six-argument calls stop binding.
--
-- A signature change cannot be `create or replace`, so 0050's four-argument function goes first;
-- the drop also discards its ACL, which is why the grants are re-stated at the bottom.
drop function if exists evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz);

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
  v_tip_seq bigint;
  v_tip_hash bytea;
  v_tip_at timestamptz;
  v_seq bigint;
  v_prev_hash bytea;
  v_created_at timestamptz;
  v_id uuid;
  v_canonical text;
  -- The two correlation ids AFTER validation, and their canonical JSON spellings (`null` when
  -- absent, a quoted and escaped string otherwise): the row and the hash must carry the same
  -- pair, and the canonical string is built from the validated values, never the arguments.
  v_request_id text;
  v_trace_id text;
  v_request_json text;
  v_trace_json text;
begin
  -- Untrusted input, validated before anything else (CorrelationNames.IsSafeRequestId,
  -- src/BuildingBlocks/Observability/Correlation/CorrelationNames.cs:53,70-88): an unacceptable
  -- value becomes an absent one, and can neither fail the revoke nor reach the chain.
  v_request_id := case when p_request_id ~ '^[!-~]{1,128}$' then p_request_id end;

  -- A W3C trace id, lowercase hex of exactly 32 characters and never the all-zero id, which
  -- is invalid by the standard and is not a value `Activity.TraceId.ToString()` can produce.
  v_trace_id := case
                  when p_trace_id ~ '^[0-9a-f]{32}$' and p_trace_id <> repeat('0', 32)
                  then p_trace_id
                end;

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
    if p_org_id is null then
      return query select 'not_found'::text;
      return;
    end if;

    if not exists (
      select 1
        from public.membership m
        join public.membership_role mr
             on mr.membership_id = m.id
            and mr.org_id = p_org_id
        join public.role r
             on r.id = mr.role_id
            and r.kind = 'member'
            -- `membership_role.role_id` carries no foreign key (0011:116-134), so a row written around
            -- the commands could point a membership at another org's custom role; the app path is
            -- protected by RLS (`role_catalogue_read`, 0010:184-188) and this definer runs under the
            -- owner policy, so the same scope test has to be spelled out here. A built-in role
            -- (`org_id is null`) is platform-wide; a custom role only ever grants inside its own org.
            and (r.org_id is null or r.org_id = p_org_id)
        join public.role_permission rp
             on rp.role_id = r.id
            and rp.org_id is not distinct from r.org_id
        join public.permission p
             on p.code = rp.permission_code
       where m.org_id = p_org_id
         and m.user_id = v_actor
         and m.status = 'active'
         and p.tenant_assignable
         and rp.permission_code = 'membership:write')
    then
      return query select 'not_found'::text;
      return;
    end if;

    if not exists (
      select 1
        from public.membership m
       where m.org_id = p_org_id
         and m.user_id = v_target_user
         and m.status = 'active')
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
    -- The org chain is extended under the application writer's own lock (`AuditSql.AdvisoryLock`,
    -- AuditTrail.cs:245), with the wait bound the header's `lock_timeout` sets for this whole call
    -- (the same 2000 ms as `AuditSql.LockTimeout`, :243).
    -- A timeout is 55P03 and aborts this statement, so the revoke above rolls back with it.
    perform pg_advisory_xact_lock(hashtext('audit:' || p_org_id::text));

    select a.seq, a.entry_hash, a.created_at
      into v_tip_seq, v_tip_hash, v_tip_at
      from public.audit_log a
     where a.org_id = p_org_id
     order by a.seq desc
     limit 1;

    v_seq := coalesce(v_tip_seq, 0) + 1;
    v_prev_hash := coalesce(v_tip_hash, decode(repeat('00', 32), 'hex'));

    -- Never earlier than the tip: the chain guard of 0015 refuses a row that moves the chain
    -- back in time, and the application writer keeps the same floor (AuditTrail.cs:144-156).
    v_created_at := greatest(clock_timestamp(), coalesce(v_tip_at, '-infinity'::timestamptz));

    v_id := uuidv7();

    -- The canonical spelling of the two ids: `AuditHashing.CanonicalFields` hashes the raw
    -- column strings (src/BuildingBlocks/Persistence/Audit/AuditHashing.cs:38,40), so a present
    -- id is a JSON string and an absent one is the literal `null`. `to_jsonb(<text>)::text` is
    -- RFC 8785's string form for the accepted values (visible ASCII, so only `"` and `\` need an
    -- escape), and a NULL argument yields a NULL jsonb, hence the coalesce.
    v_request_json := coalesce(to_jsonb(v_request_id)::text, 'null');
    v_trace_json := coalesce(to_jsonb(v_trace_id)::text, 'null');

    -- The RFC 8785 canonical bytes `AuditHashing.CanonicalFields` yields for this row: keys in
    -- UTF-16 code-unit order, no whitespace, nulls spelled `null`, the timestamp with six
    -- fractional digits in UTC, `metadata` an object. Every value below is ASCII and none needs
    -- an escape beyond the two the id spellings already carry, so the text is exactly the
    -- canonical serialisation of the row.
    v_canonical := format(
      '{"action":"%s","actor_id":"%s","actor_type":"user","after_hash":null,"before_hash":null,'
      || '"created_at":"%s","id":"%s","metadata":{"target_user_id":"%s"},"object_id":"%s",'
      || '"object_type":"session","org_id":"%s","request_id":%s,"seq":%s,"trace_id":%s}',
      'auth.session_revoke_admin',
      v_actor::text,
      to_char(v_created_at at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
      v_id::text,
      v_target_user::text,
      p_session_id::text,
      p_org_id::text,
      v_request_json,
      v_seq,
      v_trace_json);

    insert into public.audit_log
      (id, org_id, seq, actor_type, actor_id, action, object_type, object_id,
       before_hash, after_hash, request_id, trace_id, metadata, prev_hash, entry_hash, created_at)
    values
      (v_id, p_org_id, v_seq, 'user', v_actor::text, 'auth.session_revoke_admin', 'session',
       p_session_id::text, null, null, v_request_id, v_trace_id,
       jsonb_build_object('target_user_id', v_target_user::text),
       v_prev_hash, sha256(v_prev_hash || convert_to(v_canonical, 'UTF8')), v_created_at);
  end if;

  return query select 'revoked'::text;
end
$$;

-- 0048:559/565 re-stated for the NEW signature: `drop function` above discarded the old ACL, so
-- these are not decorative. `revoke all ... from public` keeps the built-in PUBLIC grant closed
-- and EXECUTE stays with the one role that calls the route (TL T80).
revoke all on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz, text, text) from public;
grant execute on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz, text, text) to evidgate_app;
