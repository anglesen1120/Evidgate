-- 0085_identity_session_revoke_all_admin.sql
--
-- WHY: an org administrator had no way to end EVERY live session of one member in one call. The single
-- `DELETE /v1/auth/sessions/{s}` ends one row, and `session_revoke_others` is self-service only (it never
-- touches another account). The member-removal command ends a member's sessions through
-- `session_revoke_all_for_user`, but that is a side effect of a membership change, not an administrative act
-- of its own — the ASVS 7.4.5 requirement (a compromised member credential must be endable without waiting
-- for the session to lapse or for the member to be removed) had no route.
--
-- SHAPE: one new owner-owned definer, `evidgate_resolve.session_revoke_all_admin`, returning
-- `table (status text, revoked_count integer)` and answering exactly three statuses:
--
--   * `unauthenticated` — no live session carries the presented cookie HMAC; the route answers 401.
--   * `not_found` — the target is the caller, the org is absent, the caller holds no ACTIVE
--     `membership:write` in that org, or the target holds no ACTIVE membership in it (an unknown user
--     included). ONE answer for every one of those, so the route is not an oracle over other accounts.
--   * `revoked` — the target is an active member of the org and the caller administers it; the target's
--     live sessions are revoked and `revoked_count` names how many rows this call ended. Zero is a
--     success: a member with no live session is already in the desired state, and nothing is recorded.
--
-- AUTHORIZATION runs before any write, from inside the definer: the acting user is resolved from the
-- cookie HMAC (never from a parameter), and the `membership:write` join is 0051's, unchanged — the role
-- itself and its `role_permission` rows included, a custom role only inside its own org. The target's
-- membership is then proved ACTIVE in the SAME org.
--
-- AUDIT: exactly one `auth.session_revoke_admin` row per call that revoked at least one session, appended
-- to the org's chain INSIDE this definition's transaction (the rule `session_revoke_one` already follows),
-- so the revoke and its record commit or roll back together: a lock timeout or an `audit_log` refusal
-- aborts the call and leaves every addressed session live. The row is actor `user` (the admin), object
-- `user` / the target's user id (the act is about the member, and one call covers N sessions), metadata
-- `{"revoked_count":"N","target_user_id":"<target>"}`. `revoked_count` is a JSON STRING, not a number:
-- every audit row the platform writes keeps its metadata as string values (AuditRow.Metadata is
-- IReadOnlyDictionary<string, string>, AuditHashing.MetadataNode serializes it, and AuditChainVerifier
-- deserializes it back into the same map), so a JSON number here would make the chain unverifiable.
--
-- LOCKS, in this order (the invariant 0065 leaves implicit and this file makes explicit): FIRST
-- `evidgate.session_user_lock(p_target_user_id)`, THEN the org chain lock
-- `pg_advisory_xact_lock(hashtext('audit:' || p_org_id))`. A concurrent login or rotation of the target
-- therefore serialises with this revoke, and a concurrent member removal (which takes the user lock, then
-- the org audit lock through its own caller) takes the two locks in the same order, so the pair cannot
-- deadlock. `set lock_timeout = '2000'` bounds each wait as a function GUC, exactly like 0051.
--
-- TIMESTAMP: `p_now` is clamped to the server clock (`v_now := least(p_now, clock_timestamp())`, the 0048
-- rule), so a caller that names the future cannot revoke rows whose deadlines have not passed, and the
-- audit row's `created_at` is floored at the chain tip (the 0015 chain guard refuses a row that moves the
-- chain back in time). The two correlation ids are caller-supplied, so they are validated here exactly as
-- 0051 validates them — a safe request id (`^[!-~]{1,128}$`) or null, a 32-lowercase-hex non-zero trace id
-- or null — and an unacceptable value is nulled, never an error.
--
-- DEPLOY ORDER: MIGRATE BEFORE APP. The function is new, so an application build that already calls it
-- fails with 42883 until this file lands; nothing that existed before changes.
--
-- GRANTS: `revoke all ... from public` closes the built-in PUBLIC grant, and EXECUTE stays with
-- `evidgate_app`, the one role that calls the route. `evidgate_owner` owns the definition (the migrator).
--
-- Rollback: `drop function if exists evidgate_resolve.session_revoke_all_admin(bytea[], uuid, uuid,
-- timestamptz, text, text);` and remove the route; no table, column, row or index changes, and rows this
-- function already wrote stay in their chains (they record revocations that happened).
--
-- Scope: ONE function definition. No table, no column, no row, no index, no backfill, no data change.
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
  -- The two correlation ids after validation and their canonical JSON spellings (`null` when absent):
  -- the row and its hash must carry the same pair, so the canonical string is built from the validated
  -- values, never from the arguments.
  v_request_id text;
  v_trace_id text;
  v_request_json text;
  v_trace_json text;
  v_tip_seq bigint;
  v_tip_hash bytea;
  v_tip_at timestamptz;
  v_seq bigint;
  v_prev_hash bytea;
  v_created_at timestamptz;
  v_id uuid;
  v_canonical text;
begin
  -- The server clock is the ceiling: `p_now` may look back, never ahead (0048's rule). `least` ignores a NULL,
  -- so a NULL means the clock, never "nothing live"; the app role is the only caller and always passes one.
  v_now := least(p_now, clock_timestamp());

  -- Untrusted input, validated before anything else (CorrelationNames.IsSafeRequestId): an unacceptable
  -- value becomes an absent one and can neither fail the revoke nor reach the chain.
  v_request_id := case when p_request_id ~ '^[!-~]{1,128}$' then p_request_id end;
  v_trace_id := case
                  when p_trace_id ~ '^[0-9a-f]{32}$' and p_trace_id <> repeat('0', 32)
                  then p_trace_id
                end;

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
  -- the org is absent, or the caller does not administer it.
  if p_org_id is null or p_target_user_id is null or p_target_user_id = v_actor then
    return query select 'not_found'::text, 0;
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
       and m.user_id = v_actor
       and m.status = 'active'
       and p.tenant_assignable
       and rp.permission_code = 'membership:write')
  then
    return query select 'not_found'::text, 0;
    return;
  end if;

  -- An unknown user has no membership here, so it answers the same `not_found` a non-member does.
  if not exists (
    select 1
      from public.membership m
     where m.org_id = p_org_id
       and m.user_id = p_target_user_id
       and m.status = 'active')
  then
    return query select 'not_found'::text, 0;
    return;
  end if;

  -- The first of the two locks (see the header): every writer of this user's sessions takes it, so a
  -- concurrent login or rotation waits here instead of adding a live row behind the revoke.
  perform evidgate.session_user_lock(p_target_user_id);

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
    -- The second lock, under the same wait bound: the org chain append is serialised with every other
    -- writer of this chain, and a timeout (55P03) aborts the statement, so the revoke above rolls back.
    perform pg_advisory_xact_lock(hashtext('audit:' || p_org_id::text));

    select a.seq, a.entry_hash, a.created_at
      into v_tip_seq, v_tip_hash, v_tip_at
      from public.audit_log a
     where a.org_id = p_org_id
     order by a.seq desc
     limit 1;

    v_seq := coalesce(v_tip_seq, 0) + 1;
    v_prev_hash := coalesce(v_tip_hash, decode(repeat('00', 32), 'hex'));

    -- Never earlier than the tip: the chain guard of 0015 refuses a row that moves the chain back in time.
    v_created_at := greatest(clock_timestamp(), coalesce(v_tip_at, '-infinity'::timestamptz));

    v_id := uuidv7();

    -- `AuditHashing.CanonicalFields` hashes the raw column values, so an absent id is the literal `null`,
    -- a present one is a JSON string (visible ASCII, so only `"` and `\` need an escape) and the count is a
    -- JSON string because every audit metadata value is one.
    v_request_json := coalesce(to_jsonb(v_request_id)::text, 'null');
    v_trace_json := coalesce(to_jsonb(v_trace_id)::text, 'null');

    -- The RFC 8785 canonical bytes of this row: keys in UTF-16 code-unit order (so `revoked_count` before
    -- `target_user_id`), no whitespace, nulls spelled `null`, the timestamp with six fractional digits in
    -- UTC. Every value is ASCII and needs no escape beyond the two the id spellings already carry.
    v_canonical := format(
      '{"action":"%s","actor_id":"%s","actor_type":"user","after_hash":null,"before_hash":null,'
      || '"created_at":"%s","id":"%s","metadata":{"revoked_count":"%s","target_user_id":"%s"},'
      || '"object_id":"%s","object_type":"user","org_id":"%s","request_id":%s,"seq":%s,"trace_id":%s}',
      'auth.session_revoke_admin',
      v_actor::text,
      to_char(v_created_at at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
      v_id::text,
      v_revoked,
      p_target_user_id::text,
      p_target_user_id::text,
      p_org_id::text,
      v_request_json,
      v_seq,
      v_trace_json);

    insert into public.audit_log
      (id, org_id, seq, actor_type, actor_id, action, object_type, object_id,
       before_hash, after_hash, request_id, trace_id, metadata, prev_hash, entry_hash, created_at)
    values
      (v_id, p_org_id, v_seq, 'user', v_actor::text, 'auth.session_revoke_admin', 'user',
       p_target_user_id::text, null, null, v_request_id, v_trace_id,
       jsonb_build_object('target_user_id', p_target_user_id::text, 'revoked_count', v_revoked::text),
       v_prev_hash, sha256(v_prev_hash || convert_to(v_canonical, 'UTF8')), v_created_at);
  end if;

  -- Zero revoked is a success: the member had no live session, so there was nothing to record.
  return query select 'revoked'::text, v_revoked;
end
$$;

-- The default privileges of the schemas grant more than this function needs, so both statements are
-- required, not decorative: PUBLIC must not call it and `evidgate_app` is the only caller (the route).
revoke all on function evidgate_resolve.session_revoke_all_admin(bytea[], uuid, uuid, timestamptz, text, text) from public;
grant execute on function evidgate_resolve.session_revoke_all_admin(bytea[], uuid, uuid, timestamptz, text, text) to evidgate_app;
