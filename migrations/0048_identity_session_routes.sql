-- 0048_identity_session_routes.sql
--
-- Owner: 02.3.2-T2.1. The route half of 02.3.2 §5/§6/§7 and AC4/AC6/AC8, plus the
-- `session-cleanup` sweep the plan places with this task: the session LIST, the three
-- revocation writes the routes call, and the bounded cleanup job. 0041 created
-- `session`, 0042 gave it the credential columns, 0045 the lifecycle writes
-- (`session_authenticate`, `session_rotate`, `session_revoke_all_for_user`,
-- `session_require_rotation`) and this file adds what the USER-facing routes need.
--
--   * `session.expired_at timestamptz` — the `expired` half of the 00 §8.9 state
--     machine (`active → revoked | expired`), stamped for reporting by the sweep.
--     Nullable, NO default, NO backfill: expiry is DERIVED on the request path
--     (`session_authenticate` compares the two deadlines), so every row that exists
--     now is correctly `null` and nothing has to be computed at apply time. A row may
--     be revoked and never stamped (`revoked_at` wins), and a stamped row keeps its
--     `revoked_at` null: the two columns answer two different questions.
--
--   * five functions, all SECURITY DEFINER, owner-owned:
--
--       - `session_list(p_cookie_hmacs bytea[], p_now timestamptz)` — the caller's
--         LIVE sessions (unrevoked, unexpired on both clocks), current one flagged,
--         newest activity first. Answers `unauthenticated` in one row when the
--         presented proof matches no live session, so the route can tell "no session"
--         from "an empty list" without a second statement.
--       - `session_revoke_current(p_cookie_hmacs, p_now)` — logout: the row the
--         presented proof belongs to.
--       - `session_revoke_one(p_cookie_hmacs, p_session_id, p_org_id, p_now)` — one
--         row: the owner's own, or a member's by an admin holding `membership:write`
--         in `p_org_id` (AC6). A foreign, unknown, already-dead or other-org id is
--         `not_found` — ONE answer for every one of them, so the route is never an
--         oracle for which session ids exist.
--       - `session_revoke_others(p_cookie_hmacs, p_keep_current, p_now)` — every live
--         session of the caller, optionally keeping the current one; answers the count.
--       - `session_cleanup(p_now, p_limit)` — the `session-cleanup` sweep: stamps
--         `expired_at` on dead rows once, and hard-deletes rows whose LAST deadline
--         passed more than 30 days ago, in bounded batches, LEAF FIRST.
--
-- The acting user is resolved from the presented credential INSIDE every function,
-- never accepted as an argument (the `login_link_identity` pattern of 0045:603 and
-- [DECISION D-S85-D8]): `cookie_hmac = any (p_cookie_hmacs)` against the unique
-- `session_cookie_hmac_key`, unrevoked and unexpired on both clocks. A NULL or empty
-- array matches no row (a NULL comparison is never true), so a wrapper that forgot
-- the proof cannot reach the table; no function takes a `p_user_id`.
--
-- NO function returns the cookie value, `cookie_hmac`, `pepper_version` or any other
-- credential column: the list projects the seven metadata columns of the plan §5 and
-- nothing else, and the four writes answer a status (and a count) only.
--
-- Why every definer here is safe (TL T80, identical hygiene to 0014/0016/0042/0044/0045):
-- owned by `evidgate_owner`, whose `session_owner_backfill` policy (0041:207-212) lets
-- it read and write `public.session` under FORCE row level security — and the same
-- `*_owner_backfill` policies on `membership`, `membership_role`, `role`,
-- `role_permission` and `permission` (0011:191-203, 0010:243-257) are what let the
-- admin path read the authorization rows BEFORE any tenant setting exists; no dynamic
-- SQL; `set search_path = ''` with schema-qualified references only, so no object the
-- caller creates can shadow a table, type or operator; the built-in EXECUTE grant to
-- PUBLIC is revoked per function and EXECUTE goes to the ONE role that needs it —
-- `evidgate_worker_sched` alone for `evidgate.session_cleanup` (it runs as the sweep
-- role, the `evidgate.expire_pending_invites` posture of 0043:208-211), `evidgate_app`
-- alone for the four route functions. Neither the operator nor the sweep role can list
-- or revoke a session.
--
-- The sweep lives in schema `evidgate`, not `evidgate_resolve`, because USAGE is the
-- precondition of EXECUTE: 0002:24 grants `evidgate` to `evidgate_app`,
-- `evidgate_operator` AND `evidgate_worker_sched`, while 0002:57 grants
-- `evidgate_resolve` to `evidgate_app` only. A definer the sweep role must call
-- belongs where the sweep role can already see it (the `expire_pending_invites`
-- and `purge_expired_ci_token_issue` placement), and widening `evidgate_resolve`
-- would hand the request-path schema to a background role for one function.
--
-- Deliberately absent, each owned by another task:
--
--   * the HTTP endpoints, the queries/commands behind them, the cookie expiry on
--     logout and the `session.cleanup` task handler — 02.3.2-T2.1's application half
--     (`Sessions/SessionRoutesStore.cs` is the only caller of these functions, and the
--     task handler loops `session_cleanup` in batches).
--   * a PER-CALL audit row. The plan's §6 asks for one "where an actor exists", and
--     these routes DO have one — but `audit_log` (0015) is org-scoped and its writer
--     is the application's `IIdentityAuditWriter`, which extends the chain of every
--     org the actor belongs to (`LinkEndpoints.ConfirmAsync` is the pre-tenant
--     precedent). A definer cannot extend a hash chain, and one that wrote a bare row
--     would break the chain the verifier walks. The deferral line below names the same
--     receiver as 0042:65, 0043:84, 0044:144 and 0045:189 (D-S75-Q4).
--   * a new INDEX. The list path is `user_id = <caller> and revoked_at is null` plus
--     the two deadlines, which `session_user_live_idx` (0045:227-229) serves: it leads
--     with the user and filters `revoked_at is null`, and the deadlines are compared on
--     the handful of rows that qualify. The sweep's stamp is `expired_at is null and
--     revoked_at is null` and its delete is `greatest(deadlines) < p_now - 30 days`:
--     both are full scans of a table that only holds LIVE sessions plus the last 30
--     days of dead ones, run by ONE task at a time, hourly at most. A partial index on
--     a deadline — or on the nullable `expired_at` — would be rewritten by every idle
--     slide (`expires_at`) or by every stamp (`expired_at`), i.e. it would move cost
--     from a bounded sweep onto every authenticated request, which is the trade
--     0045:224-229 already made once. Measured need, not precedent, decides a later
--     one.
--
-- Rollback (playbook `docs/migrations-expand-contract.md`, mechanical, never executed
-- in place). Every statement here is additive and nothing calls these functions yet:
--   drop function if exists evidgate_resolve.session_list(bytea[], timestamptz);
--   drop function if exists evidgate_resolve.session_revoke_current(bytea[], timestamptz);
--   drop function if exists evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz);
--   drop function if exists evidgate_resolve.session_revoke_others(bytea[], boolean, timestamptz);
--   drop function if exists evidgate.session_cleanup(timestamptz, integer);
--   alter table public.session drop column if exists expired_at;
-- Dropping `expired_at` discards nothing derived: the deadline columns stay the truth
-- of the state machine, and the next sweep re-stamps what it needs.
--
-- lint: audit-deferred 02.7.1-T2.2 evidgate.session_cleanup, evidgate_resolve.session_list, evidgate_resolve.session_revoke_current, evidgate_resolve.session_revoke_one, evidgate_resolve.session_revoke_others
-- See this header's "Deliberately absent" section: the per-call row needs the org
-- chain the application's `IIdentityAuditWriter` extends, which a definer cannot do.
--
-- Locking: `add column` of a nullable column with no default is catalog-only (no table
-- rewrite, no backfill), so the statement is fast; PostgreSQL takes ACCESS EXCLUSIVE on
-- `public.session` for the length of this file's single transaction, so expect a short
-- window on an otherwise idle table. `set local lock_timeout` below bounds every lock
-- this transaction waits for to 5 s and disappears with it — exit 3 (lock timeout, file
-- rolled back and unjournalled) means "retry when the table is quiet", not a defect.
-- The function definitions take only an AccessShareLock on `public.session` while
-- PostgreSQL validates their bodies, and create no index, so they block no request.
set local lock_timeout = '5s';

alter table public.session
  add column if not exists expired_at timestamptz;

-- The caller's live sessions (AC6). `status` is per row, not a second result set: an
-- authenticated caller always has at least ONE live row (the one the proof resolved),
-- so a caller with `unauthenticated` and a caller with an empty list can never be
-- confused. `ip::text` renders the `inet` of 0041 as the string the wire contract
-- carries ('203.0.113.7', or '203.0.113.7/32' for a network value) — the column stays
-- `inet`, the projection is textual, and a caller that wants the network semantics can
-- parse it back. The credential columns are absent on purpose and the plan §5 list is
-- the whole projection.
create or replace function evidgate_resolve.session_list(
  p_cookie_hmacs bytea[],
  p_now timestamptz)
returns table (
  session_id uuid,
  is_current boolean,
  created_at timestamptz,
  last_seen_at timestamptz,
  expires_at timestamptz,
  absolute_expires_at timestamptz,
  ip text,
  user_agent text,
  status text)
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_session_id uuid;
  v_user_id uuid;
begin
  -- The acting user, from the credential itself (the 0045:624-629 predicate verbatim).
  select s.id, s.user_id
    into v_session_id, v_user_id
    from public.session s
   where s.cookie_hmac = any (p_cookie_hmacs)
     and s.revoked_at is null
     and s.expires_at > p_now
     and s.absolute_expires_at > p_now;

  if v_user_id is null then
    return query
      select null::uuid, false, null::timestamptz, null::timestamptz, null::timestamptz,
             null::timestamptz, null::text, null::text, 'unauthenticated'::text;
    return;
  end if;

  -- Live only: a revoked or expired sibling is not something the user can act on, and
  -- `session_user_live_idx` is the index this predicate was shaped for (0045:224-229).
  -- Ordered by creation, newest first, so the page is a stable sign-in history. Ordering
  -- by activity would not be stable: once a session is older than the 60 s slide throttle
  -- (0045:392,415) the listing request itself bumps its `last_seen_at`, so the order would
  -- depend on which session the client happened to use last.
  return query
    select s.id,
           s.id = v_session_id,
           s.created_at,
           s.last_seen_at,
           s.expires_at,
           s.absolute_expires_at,
           s.ip::text,
           s.user_agent,
           'ok'::text
      from public.session s
     where s.user_id = v_user_id
       and s.revoked_at is null
       and s.expires_at > p_now
       and s.absolute_expires_at > p_now
     order by s.created_at desc, s.id;
end
$$;

-- Logout (AC8): revokes the row the presented proof belongs to. Idempotent from the
-- caller's point of view — a concurrent or repeated logout whose row is already revoked
-- resolves no live session and answers `unauthenticated`, which the route renders as
-- the same 401 `session_revoked` a replay of the old cookie gets (the row still exists
-- and is still dead, D9: revocation is a row state, not a key rotation).
create or replace function evidgate_resolve.session_revoke_current(
  p_cookie_hmacs bytea[],
  p_now timestamptz)
returns table (status text)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_session_id uuid;
  v_revoked_id uuid;
begin
  select s.id
    into v_session_id
    from public.session s
   where s.cookie_hmac = any (p_cookie_hmacs)
     and s.revoked_at is null
     and s.expires_at > p_now
     and s.absolute_expires_at > p_now;

  if v_session_id is null then
    return query select 'unauthenticated'::text;
    return;
  end if;

  -- `rotation_required_at` is cleared with the revocation: a pending flag on a dead
  -- row would only make `session_rotate` look at a session nobody can present.
  update public.session s
     set revoked_at = p_now,
         rotation_required_at = null
   where s.id = v_session_id
     and s.revoked_at is null
     and s.expires_at > p_now
     and s.absolute_expires_at > p_now
  returning s.id into v_revoked_id;

  if v_revoked_id is null then
    -- The row stopped being live between the proof read and this write (a concurrent
    -- logout, a slide that crossed the deadline): the caller gets the answer any dead
    -- session gets, not a `revoked` for a row this call did not touch.
    return query select 'unauthenticated'::text;
    return;
  end if;

  return query select 'revoked'::text;
end
$$;

-- One session id (AC6): the owner's own row, or a member's row when the actor holds
-- `membership:write` in the org named by `p_org_id`. The authorization runs HERE, in the
-- definer, and not in the route: a caller cannot pass a user id, and the route cannot
-- forget the check.
--
-- The grant join is `IdentityAccessSql.MemberGrant`'s (02.2.1-T2.2, AC7) — active
-- membership, `kind = 'member'` role, a grant row that belongs to its role's own org,
-- and only a `tenant_assignable` code — so the read-time T79 filter cannot be bypassed
-- by a corrupted `role_permission` row. The target must ALSO be an active member of the
-- SAME org: an admin is never granted the whole platform's sessions, only the members of
-- the org whose admin they are.
--
-- Every refusal that names a session — foreign, unknown, already revoked or expired,
-- other org, missing `membership:write`, `p_org_id` absent — answers `not_found`, and
-- the route maps it to the same 404 body, so the endpoint cannot be used to enumerate
-- session ids or to learn whether one belongs to somebody else (AC6, plan §8).
--
-- `for update` on the target row serializes two concurrent revokes of the same session
-- (one answers `revoked`, the other re-reads it as dead and answers `not_found`); it
-- takes no lock on anything else, so two admins revoking different members never wait
-- on each other.
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
as $$
declare
  v_actor uuid;
  v_target_user uuid;
  v_target_live boolean;
  v_revoked_id uuid;
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
   where s.id = p_session_id
     for update;

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
    -- Expired between the lock and this write: the same `not_found` any dead target gets.
    return query select 'not_found'::text;
    return;
  end if;

  return query select 'revoked'::text;
end
$$;

-- "All but this one" (AC6, plan §5): every live session of the caller. `p_keep_current`
-- is an OPTION, not a mode: NULL is treated as true (keep), so a route that forwards no
-- body keeps the session the browser is using, and only an explicit `keep_current:
-- false` kills it too. The count is what the endpoint reports; a zero count is a
-- success (the caller has nothing else live), not a refusal.
create or replace function evidgate_resolve.session_revoke_others(
  p_cookie_hmacs bytea[],
  p_keep_current boolean,
  p_now timestamptz)
returns table (status text, revoked_count integer)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_session_id uuid;
  v_user_id uuid;
  v_revoked integer;
begin
  select s.id, s.user_id
    into v_session_id, v_user_id
    from public.session s
   where s.cookie_hmac = any (p_cookie_hmacs)
     and s.revoked_at is null
     and s.expires_at > p_now
     and s.absolute_expires_at > p_now;

  if v_user_id is null then
    return query select 'unauthenticated'::text, 0;
    return;
  end if;

  with revoked as (
    update public.session s
       set revoked_at = p_now,
           rotation_required_at = null
     where s.user_id = v_user_id
       and s.revoked_at is null
       and s.expires_at > p_now
       and s.absolute_expires_at > p_now
       and (not coalesce(p_keep_current, true) or s.id <> v_session_id)
    returning 1 as one
  )
  select count(*)::integer
    into v_revoked
    from revoked;

  return query select 'revoked'::text, v_revoked;
end
$$;

-- The `session-cleanup` sweep (plan §7, §8 retention): bounded, idempotent, and it never
-- touches a live row.
--
--   (a) STAMP: an unrevoked row past idle or absolute expiry gets `expired_at = p_now`
--       once. The predicate is re-tested in the `update`'s own clause (not only on the
--       statement snapshot), the lesson 0043:190-199 records: EvalPlanQual re-evaluates
--       the UPDATE's condition, which is what stops a concurrent request's slide from
--       dragging a row that just became live back to `expired`.
--   (b) PURGE: a row whose LAST deadline — `greatest(expires_at, absolute_expires_at,
--       revoked_at)`, revoked rows included — passed more than 30 days ago is deleted.
--       `greatest` ignores NULLs, so an unrevoked row is judged by its two deadlines and
--       a revoked one by whichever came last.
--
--       LEAF FIRST is a DATA rule, not an ordering hint: `session.rotated_from` is the
--       self-FK of 0041:146 with NO `on delete` action, so a row that a remaining row
--       still references cannot be deleted at all (the FK refuses the statement). The
--       predicate therefore skips any row with a referrer, which means a rotation chain
--       unwinds from its tip across CALLS — each call deletes the rows whose successors
--       are already gone — instead of failing or forcing an `on delete set null` that
--       would rewrite the chain the rotation tests read. A chain entirely inside the
--       30-day window is finished in a few calls, which is exactly what the task's
--       loop (bounded batches until a short one) is for.
--
-- A NULL or non-positive `p_limit` sweeps nothing (fail-closed, the 0043:170-176 rule:
-- an absent batch size must never become an unbounded delete), and the cap is 10 000 per
-- part so one call never holds a long lock. Both parts of one call are bounded by the
-- SAME limit, so the task's "stop on a short batch" test stays meaningful. A NULL `p_now`
-- matches nothing at all — no stamp, no delete — which is the safe reading of a caller
-- that does not know what "now" is.
create or replace function evidgate.session_cleanup(
  p_now timestamptz,
  p_limit integer)
returns table (stamped integer, deleted integer)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_limit integer := least(greatest(coalesce(p_limit, 0), 0), 10000);
  v_cutoff timestamptz;
  v_stamped integer := 0;
  v_deleted integer := 0;
begin
  if v_limit = 0 or p_now is null then
    return query select 0, 0;
    return;
  end if;

  v_cutoff := p_now - interval '30 days';

  with batch as (
    select s.id
      from public.session s
     where s.expired_at is null
       and s.revoked_at is null
       and (s.expires_at <= p_now or s.absolute_expires_at <= p_now)
     order by s.expires_at
     limit v_limit
       for update skip locked
  ), stamped as (
    update public.session s
       set expired_at = p_now
      from batch b
     where s.id = b.id
       and s.expired_at is null
       and s.revoked_at is null
       and (s.expires_at <= p_now or s.absolute_expires_at <= p_now)
    returning s.id
  )
  select count(*)::integer
    into v_stamped
    from stamped;

  with batch as (
    select s.id
      from public.session s
     where greatest(s.expires_at, s.absolute_expires_at, s.revoked_at) < v_cutoff
       and not exists (
             select 1
               from public.session c
              where c.rotated_from = s.id)
     order by greatest(s.expires_at, s.absolute_expires_at, s.revoked_at)
     limit v_limit
       for update skip locked
  ), removed as (
    delete from public.session s
     using batch b
     where s.id = b.id
       and greatest(s.expires_at, s.absolute_expires_at, s.revoked_at) < v_cutoff
       and not exists (
             select 1
               from public.session c
              where c.rotated_from = s.id)
    returning s.id
  )
  select count(*)::integer
    into v_deleted
    from removed;

  return query select v_stamped, v_deleted;
end
$$;

revoke all on function evidgate_resolve.session_list(bytea[], timestamptz) from public;
revoke all on function evidgate_resolve.session_revoke_current(bytea[], timestamptz) from public;
revoke all on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz) from public;
revoke all on function evidgate_resolve.session_revoke_others(bytea[], boolean, timestamptz) from public;
revoke all on function evidgate.session_cleanup(timestamptz, integer) from public;

grant execute on function evidgate_resolve.session_list(bytea[], timestamptz) to evidgate_app;
grant execute on function evidgate_resolve.session_revoke_current(bytea[], timestamptz) to evidgate_app;
grant execute on function evidgate_resolve.session_revoke_one(bytea[], uuid, uuid, timestamptz) to evidgate_app;
grant execute on function evidgate_resolve.session_revoke_others(bytea[], boolean, timestamptz) to evidgate_app;
grant execute on function evidgate.session_cleanup(timestamptz, integer) to evidgate_worker_sched;
