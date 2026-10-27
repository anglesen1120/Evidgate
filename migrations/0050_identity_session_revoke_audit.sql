-- 0050_identity_session_revoke_audit.sql
--
-- Owner: F55 (finishes the audit half of 02.3.2-T2.1 AC6, the receiver coordinator row F48
-- left open; for this ONE function it ends the 02.7.1-T2.2 deferral that 0048 carried). The
-- administrative session revoke now writes its audit row INSIDE the definer's own
-- transaction: F48 (S118) wrote it from the handler AFTER the revoke had committed, so a
-- client abort or a fault between the two statements left a revocation with no record, and
-- the handler could not know WHICH member the ended session belonged to (the definer
-- answered a status only). Both holes close here.
--
--   * `evidgate_resolve.session_revoke_one` is re-created with the SAME arguments and the
--     SAME return type (`p_cookie_hmacs bytea[], p_session_id uuid, p_org_id uuid,
--     p_now timestamptz` -> `table (status text)`), so its owner and its grants from
--     0048:559/565 are unchanged; they are re-stated at the bottom, idempotently. The body
--     is 0048:282-390 verbatim — the same three refusals, the same authorization join, the
--     same read-without-lock then guarded `update` as the arbiter — plus ONE append to the
--     org's audit chain when, and only when, an admin ended ANOTHER member's session.
--
--   * The audit row: `actor_type 'user'`, `actor_id` the acting admin, action
--     `auth.session_revoke_admin`, `object_type 'session'`, `object_id` the revoked session,
--     `org_id = p_org_id`, `metadata = {"target_user_id": "<the member>"}`. Nothing else:
--     no cookie value, no `cookie_hmac`, no pepper, no address, no user agent. A SELF
--     revoke (`v_target_user = v_actor`) writes no row — it is every caller's own logout,
--     not an administrative act — and neither does any refusal.
--
-- SINGLE-TRANSACTION RULE. The revoke `update` and the audit `insert` run in one
-- statement, hence in one transaction: they commit together or roll back together, so the
-- chain can never gain a row for a revocation that did not happen, and a revocation can
-- never commit without its row. A fault inside the append aborts the whole call, which is
-- what the store already maps to 503 `unavailable` (SessionEndpoints' database-fault
-- mapping, kept by F55's handler cutover). That is the property F48 could not have with a
-- second, post-commit write.
--
-- WHY A DEFINER CAN CHAIN, WHERE 0048 SAID IT COULD NOT. 0048's header ("a definer cannot
-- extend a hash chain") and its `-- lint: audit-deferred` line (0048:112, D-S75-Q4) are
-- SUPERSEDED FOR THIS FUNCTION by this file; the other four 0048 definers keep that
-- deferral, and `scripts/check-rls.py` now sees a real per-call row in this body
-- (`definer_audit`). The chain is extendable in SQL because its hash rule is plain SHA-256
-- over bytes, and this body reproduces the three sources of truth EXACTLY:
--
--   * the canonical field set and its shape: `AuditHashing.CanonicalFields`
--     (src/BuildingBlocks/Persistence/Audit/AuditHashing.cs:21-44) — the RFC 8785 object of
--     the fourteen hashed fields, `prev_hash`/`entry_hash` deliberately excluded;
--   * the canonicalisation itself, including the UTF-16-sorted key order and the
--     `yyyy-MM-ddTHH:mm:ss.ffffffZ` timestamp with exactly six fractional digits:
--     `JsonCanonicalizer` (src/BuildingBlocks/Canonicalization/JsonCanonicalizer.cs);
--     `AuditHashing.FormatTimestamp` (same file, :66-67) is the timestamp format, and
--     `to_char(... at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')` is its SQL twin;
--   * the chain rule `entry_hash = sha256(prev_hash || canonical UTF-8 bytes)` with the
--     genesis `prev_hash` of 32 zero bytes: `AuditHashing.EntryHash` (:49-59) and `Genesis`
--     (:17), the same function the writer and `AuditChainVerifier` share — so the row this
--     body appends verifies with the existing verifier, unchanged.
--
-- SERIALISATION. `seq` is dense per org and the tip read must not race a concurrent writer,
-- so the append takes exactly the lock the application's writer takes, under the same key:
--   `AuditSql.AdvisoryLock` — "select pg_advisory_xact_lock(hashtext('audit:' || @org))"
--   (src/BuildingBlocks/Persistence/Audit/AuditTrail.cs:245), called with
--   `new { org = orgId.ToString("D") }` (same file, :131);
--   `hashtext('audit:' || p_org_id::text)` below is that expression: PostgreSQL renders a
--   `uuid` as the lowercase hyphenated "D" form, so both sides hash the same text.
-- The bounded wait is the writer's too, and it is a property of the FUNCTION, not of a
-- statement: `set lock_timeout = '2000'` sits in the header beside `set search_path = ''`,
-- the same bound as `AuditSql.LockTimeout` (:243). A function-level GUC covers BOTH waits
-- this body takes — the row lock of the session `update` and the org chain's advisory lock —
-- and PostgreSQL restores the caller's value when the function exits, so it can neither leak
-- into the caller's transaction nor depend on a statement running first (S120 review S1: a
-- `set_config(..., true)` after the update would have left the revoke's own row-lock wait
-- unbounded and the setting parked on the caller's transaction). A busy org therefore answers
-- `55P03` (lock_not_available) instead of parking a pooled connection — and because the
-- exception aborts this statement, the revoke rolls back with it (the design's fail-closed
-- rule: no revocation without its record).
-- The tip is read AFTER the lock and in the same transaction, so `seq = tip + 1`,
-- `prev_hash = tip.entry_hash` and `created_at >= tip.created_at` hold against every other
-- writer of this org. `created_at` is `clock_timestamp()` (microsecond precision, what
-- PostgreSQL stores and what the hash formats) raised to the tip's instant, mirroring the
-- writer's monotone floor (AuditTrail.cs:144-156). The chain guard of 0015:89-122 then
-- re-tests the same invariants on insert; this body does NOT bypass it — the owner INSERT
-- policy of 0015:75-79 is what lets the definer append at all under FORCE row level
-- security, and no UPDATE/DELETE policy exists, so history stays append-only.
--
-- Scope of this file: ONE function body, no table, no column, no row, no index and no new
-- schema object, so applying it changes no data and needs no backfill. It is safe on 0049:
-- nothing here reads `evidence_revision`, so the relative order of the two files is
-- immaterial — 0049 stays where it is and this file only appends the next number.
--
-- Idempotent: `create or replace function` (same argument list, so no drop and no ownership
-- change), plus `revoke all` / `grant execute`, which are re-statements of the 0048 grants;
-- a second apply of this file is a no-op. One file runs in one transaction
-- (README "File contract"), and `create or replace function` takes no table lock beyond the
-- catalogue rows it edits.
--
-- DECLARED LOSS (one, non-blocking, receiver named). These rows carry `request_id` and
-- `trace_id` as NULL, because a definition cannot see the caller's ambient context: the C#
-- writer stamps them from `Activity` (`AuditTrail.RequestIdBaggage` for `request_id`, the
-- trace id for `trace_id`, src/BuildingBlocks/Persistence/Audit/AuditTrail.cs:95-101), and no
-- argument of this function carries either. Both columns stay in the canonical string and in
-- the hash as `null`, so the chain is unaffected and the two ARE still correlated by
-- `org_id` + `seq` + `object_id` (the session) — what is missing is the join to the HTTP
-- request that caused it. Carrying them needs an argument the definition cannot gain
-- (`create or replace` may not change the signature; that is a new function plus a call-site
-- change), so it is NOT in this file: receiver = a coordinator queue row (queue row F58,
-- "thread the request/trace ids into the session-revoke definer").
--
-- Rollback (playbook `docs/migrations-expand-contract.md`, a NEW file, never an edit):
-- re-create 0048's body (`0048_identity_session_routes.sql:282-391`), which is the same
-- signature with the audit append removed; the rows already written stay in the chain, which
-- is correct — they record revocations that happened.
create or replace function evidgate_resolve.session_revoke_one(
  p_cookie_hmacs bytea[],
  p_session_id uuid,
  p_org_id uuid,
  p_now timestamptz)
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

    -- The RFC 8785 canonical bytes `AuditHashing.CanonicalFields` yields for this row: keys in
    -- UTF-16 code-unit order, no whitespace, nulls spelled `null`, the timestamp with six
    -- fractional digits in UTC, `metadata` an object. Every value below is ASCII and none needs
    -- an escape, so the text is exactly the canonical serialisation of the row.
    v_canonical := format(
      '{"action":"%s","actor_id":"%s","actor_type":"user","after_hash":null,"before_hash":null,'
      || '"created_at":"%s","id":"%s","metadata":{"target_user_id":"%s"},"object_id":"%s",'
      || '"object_type":"session","org_id":"%s","request_id":null,"seq":%s,"trace_id":null}',
      'auth.session_revoke_admin',
      v_actor::text,
      to_char(v_created_at at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
      v_id::text,
      v_target_user::text,
      p_session_id::text,
      p_org_id::text,
      v_seq);

    insert into public.audit_log
      (id, org_id, seq, actor_type, actor_id, action, object_type, object_id,
       before_hash, after_hash, request_id, trace_id, metadata, prev_hash, entry_hash, created_at)
    values
      (v_id, p_org_id, v_seq, 'user', v_actor::text, 'auth.session_revoke_admin', 'session',
       p_session_id::text, null, null, null, null,
       jsonb_build_object('target_user_id', v_target_user::text),
       v_prev_hash, sha256(v_prev_hash || convert_to(v_canonical, 'UTF8')), v_created_at);
  end if;

  return query select 'revoked'::text;
end
$$;

-- 0048:559/565 re-stated: the signature did not change, so these are the grants the function
-- already carries; `revoke all ... from public` keeps the built-in PUBLIC grant closed and
-- EXECUTE stays with the one role that calls the route (TL T80).
revoke all on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz) from public;
grant execute on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz) to evidgate_app;
