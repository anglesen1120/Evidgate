-- 0045_identity_session_hardening.sql
--
-- Owner: 02.3.2-T1.1. The session lifecycle half of the feature plan (02.3.2 §4, §6,
-- §7, §10 T1.1; 00 §8.9 `session: active → revoked | expired`, §9.1 cookie + XSRF):
-- the cookie-authenticated request path, rotation, idle sliding and the two
-- revocation writes. 0041 created the table, 0042 gave it the credential columns
-- (`cookie_hmac`, `pepper_version`) and the unique lookup key, and 0044 added the
-- read-only `session_user` lookup the linking routes used. This file is the
-- lifecycle: everything that WRITES a session after it was minted.
--
--   * `session.rotation_required_at` — set by a privilege change (a member's roles
--     changed, 02.1.2), read by the next request with that session, which rotates
--     it (D5b, AC7). NULL means "no rotation pending", which is every row 0041/0042
--     already holds: the column is nullable and needs no backfill.
--
--   * `array_cookie_hmac` lookups and one writer per operation, all SECURITY
--     DEFINER, because `evidgate_app` holds NO privilege on `session` (0041:214-224)
--     and 0041's owner-only policy is what lets the definers work under FORCE row
--     level security:
--
--       - `session_authenticate(p_cookie_hmacs, p_current_hmac, p_current_version,
--         p_now, p_idle_seconds)` — ONE statement that answers "which session is
--         this cookie, and is it usable?" and applies the two lifecycle writes that
--         belong on the request path (D3):
--           * it looks the row up by `cookie_hmac = any (p_cookie_hmacs)`, i.e. the
--             hash of the presented value under the current pepper and — during a
--             rotation window — under the previous one. The comparison happens in
--             the database against the unique index, never in application code, so
--             there is no secret compared app-side and no timing side channel.
--           * it classifies the row with the D2 precedence — unknown, revoked,
--             expired (either clock), disabled (the account is not `active`), then
--             active — so a dead session is answered before any handler runs
--             (AC2/AC3/AC4, AC8).
--           * it slides `last_seen_at`/`expires_at` only for an ACTIVE session and
--             only when `last_seen_at < p_now - 60 s`, and never past
--             `absolute_expires_at` (AC2/AC3, P1/P3: at most one write per 60 s per
--             session). The throttle is re-tested in the `update`'s own predicate,
--             not only on the statement snapshot: two concurrent requests on one
--             idle session both read "due", and only the one that takes the row
--             lock first still passes the test when the statement re-evaluates it
--             — the loser matches no row, so it writes nothing and answers
--             `slid = false`. A request that is not due for a slide writes nothing.
--           * it re-hashes the row to the current pepper when the row matched the
--             previous pepper's hash (`cookie_hmac`/`pepper_version` updated in the
--             same statement), which is what drains a retired pepper without a
--             sweep or a forced re-login (D4).
--         Every timestamp is the caller's `p_now` (a `TimeProvider`), never
--         `now()`, so a test moves the clocks instead of waiting.
--
--         Why one statement and not a read followed by an update: the read and the
--         writes must see one snapshot, a slide that is not due must cost no write,
--         and the request path pays one round trip (P1/P3). The statement is a
--         single `with` chain of data-modifying CTEs: `matched` → `decided` →
--         `actions` → one `update … returning` (`written`), and the final select
--         projects the decision plus what was written. The data-modifying CTE always
--         executes, so an unchanged row still yields its status. The snapshot alone
--         cannot decide a WRITE (two requests may both read "due" and one of them
--         would then slide a deadline the other just slid), which is why the update
--         re-tests its own throttle against the row version it locks; see the
--         `written` CTE.
--
--       - `session_create(...)` gains `p_predecessor_hmacs bytea[]` (D5a) and
--         `p_now timestamptz`: the cookie the browser presented to the login callback
--         is revoked in the same statement as the insert, and `rotated_from` points at
--         it only when that row belongs to the same user. A client-supplied SESSION ID
--         is never honoured anywhere in this file — the predecessor is looked up by the
--         hash of a value only its holder could present, which is what makes session
--         fixation impossible. `p_now` stamps `created_at` and `last_seen_at`, because
--         `last_seen_at` is the value `session_authenticate` compares against ITS
--         `p_now`: stamping the row from the server's `now()` would judge one column
--         with two clocks, and any app/database skew would slide on the first request.
--         The previous signatures are dropped, not overloaded.
--
--       - `session_rotate(p_old_session_id, p_new_session_id, p_cookie_hmac,
--         p_pepper_version, p_now, p_idle_seconds, p_ip, p_user_agent)` — the
--         privilege-change rotation (D5b, AC7). ATOMIC AND SINGLE-WINNER: the
--         predecessor is revoked by one `update … where id = <old> and revoked_at is
--         null and the session is still live`, and the successor is inserted only
--         when that update returned a row (the insert selects FROM the update's
--         `returning`). Of two concurrent rotations of the same old session exactly
--         one inserts a successor; the loser's update matches no row and the caller
--         answers 401 `session_revoked` (the old cookie is dead — that is the truth
--         of the row, not an internal error). The successor inherits the old row's
--         `absolute_expires_at` (an absolute deadline is not extended by a rotation),
--         its `ip`/`user_agent` unless the caller supplies new ones, and its
--         `rotation_required_at` is cleared.
--
--       - `session_revoke_all_for_user(p_user_id, p_now)` — "kill every session of
--         this user" (member removal, 02.1.2-T3.2 / `ISessionRevocation`). Runs on
--         the CALLER's connection and transaction, so a rolled-back membership
--         change rolls the revocation back with it; it therefore returns the count
--         instead of committing anything.
--
--       - `session_require_rotation(p_user_id, p_now)` — stamps
--         `rotation_required_at` on every live (unrevoked and unexpired) session of
--         the user, inside the role-change transaction (`ISessionPrivilegeChange`).
--         A session that is already flagged is stamped again: the flag must reflect
--         the LATEST privilege change, and a second change during the same request
--         must not leave an older timestamp behind.
--
--   * `login_link_identity` keeps its two result columns but takes the presented
--     cookie hash candidates and `p_now` instead of a caller-supplied `p_user_id`
--     (D8, residual SEC2 of 02.3.1-T2): the function resolves the acting user from
--     a live session itself and answers `unauthenticated` when there is none. A
--     caller can no longer move an identity onto an arbitrary user id by passing it.
--     `unknown_user` disappears with it — the user is the session row's, and
--     `session.user_id` is a foreign key — while `disabled` stays reachable (a live
--     session of an account that was disabled afterwards).
--
--   * `evidgate_resolve.session_user(bytea, integer)` is DROPPED (clean cutover):
--     the read-only lookup of 0044 cannot express "revoked" vs "expired" vs
--     "disabled", could not slide, and was the only reason the linking routes held
--     a second authentication path. `session_authenticate` replaces it and the
--     request authenticator is the one caller.
--
-- Index shape — plan §4 records "partial `(cookie_hmac) where revoked_at is null`
-- for the hot lookup", and this file DELIBERATELY DEVIATES (recorded for review):
-- the full unique key `session_cookie_hmac_key` of 0042 is KEPT as the hot-path
-- index. A partial index can only serve rows that are still live, so a revoked or
-- expired cookie would find no row and be indistinguishable from a cookie that was
-- never issued — the request would answer 401 `unauthenticated` where AC4/AC8
-- require `session_revoked` (and an operator could not tell a replay after logout
-- from a bogus value). The full key serves both, at the cost of also indexing dead
-- rows that `session-cleanup` deletes after 30 days.
--
-- The per-user paths (`session_revoke_all_for_user`, `session_require_rotation`) and
-- the future session list are served by a NEW partial index `(user_id) where
-- revoked_at is null`, which REPLACES 0041's `session_user_revoked_expires_idx
-- (user_id, revoked_at, expires_at)`. `expires_at` is dropped from the index on
-- purpose: it is the column the sliding update rewrites, and an indexed column in an
-- UPDATE makes the new row version non-HOT — one index-entry update per request per
-- session. With the column out of every index, the slide is a heap-only update, and
-- `fillfactor = 80` keeps a page's worth of room so it stays that way instead of
-- spilling to a new page. 0041's header already handed this decision to 02.3.2-T1.
--
-- `fillfactor` applies to pages written AFTER this file runs, and only to the freedom
-- a page keeps: the pages the table already holds keep their 100 % fill until their
-- rows are rewritten or the table is rebuilt, so the slide only becomes reliably HOT
-- as those pages are reused. That is the intended steady state — the switch costs no
-- rewrite (an `alter table … set` is a catalog change), sessions are short-lived, and
-- the slide writes one heap tuple per request per session either way. A plain
-- `VACUUM public.session` after the deploy is advisable: it turns the dead versions
-- the pre-0045 writer left on those pages into reusable space and records it in the
-- free space map, which is the room the slide's next tuple needs. NEVER
-- `VACUUM FULL` — it rewrites and exclusively locks the table that every authenticated
-- request touches, and buys nothing `VACUUM` does not.
--
-- Why every definer here is safe (TL T80, identical hygiene to 0014/0016/0042/0044):
-- owned by `evidgate_owner`, whose `session_owner_backfill` policy (0041) lets it
-- read and write under FORCE row level security; `set search_path = ''` with
-- schema-qualified references only, so no object the caller creates can shadow a
-- table, type or operator; no dynamic SQL; the built-in EXECUTE grant to PUBLIC is
-- revoked per function and EXECUTE goes to `evidgate_app` alone.
--
-- Deliberately absent, each owned by another task:
--
--   * the antiforgery pair, the cookie writer and the principal construction — the
--     application half of this task (02.3.2-T1.2), which calls these statements.
--   * the session list, logout, per-session revoke and revoke-all ROUTES and their
--     cleanup job — 02.3.2-T2 (§5, §7 `session-cleanup`). This file provides the two
--     revocation writes they delegate to.
--   * the audit rows. A per-call audit row is TL T80's default, and these functions
--     have no actor to attribute one to: `session_authenticate` runs before any
--     tenant exists (`audit_log` of 0015 is org-scoped) and the two revocation
--     writes run inside the caller's transaction, where the APPLICATION already
--     writes the `membership.remove` / role-change row that explains them. The
--     deferral line below names the same receiver as 0042:65, 0043:84 and 0044:144
--     (D-S75-Q4).
--
-- Rollback (playbook `docs/migrations-expand-contract.md`, mechanical, never executed
-- in place). The file is additive and the previous writer versions are in 0042/0044,
-- so the contract step restores them:
--   drop function if exists evidgate_resolve.session_authenticate(bytea[], bytea, integer, timestamptz, integer);
--   drop function if exists evidgate_resolve.session_rotate(uuid, uuid, bytea, integer, timestamptz, integer, text, text);
--   drop function if exists evidgate_resolve.session_revoke_all_for_user(uuid, timestamptz);
--   drop function if exists evidgate_resolve.session_require_rotation(uuid, timestamptz);
--   drop function if exists evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz, bytea[], timestamptz);
--   drop function if exists evidgate_resolve.login_link_identity(bytea[], timestamptz, text, text, text, uuid);
--   -- then re-apply 0042's `session_create` and 0044's `session_user` +
--   -- `login_link_identity` statements, and
--   drop index if exists public.session_user_live_idx;
--   create index if not exists session_user_revoked_expires_idx
--     on public.session (user_id, revoked_at, expires_at);
--   alter table public.session set (fillfactor = 100);
--   alter table public.session drop column if exists rotation_required_at;
-- Dropping `rotation_required_at` discards nothing that is not recomputed by the next
-- privilege change; `rotated_from` already records every rotation that happened.
--
-- lint: audit-deferred 02.7.1-T2.2 evidgate_resolve.session_authenticate, evidgate_resolve.session_rotate, evidgate_resolve.session_create, evidgate_resolve.session_revoke_all_for_user, evidgate_resolve.session_require_rotation, evidgate_resolve.login_link_identity
-- See this header's "Deliberately absent" section: the pre-tenant authenticator has no
-- org to attribute a row to (0015's `audit_log` is org-scoped), and the two revocation
-- writes are per-call effects the application already audits in the caller's
-- transaction.

-- Locking and the maintenance window. The runner executes one file in ONE transaction
-- (migrations/README.md "File contract"), so the `set local` below bounds every lock
-- this file waits for to 5 s and disappears with the transaction — it replaces nothing
-- outside this file and needs no reset. Both `alter table` statements and the
-- `drop index` take ACCESS EXCLUSIVE on `public.session` for the length of the
-- transaction, i.e. for every session-authenticated request in flight: `add column`
-- of a nullable column with no default is catalog-only (no table rewrite) and
-- `set (fillfactor = 80)` is a catalog change too, so each statement is fast. The
-- `create index` of `session_user_live_idx` is the longer one — a plain (not
-- `concurrent`) build takes SHARE and therefore blocks every WRITE to the table,
-- slides included, until it is done; on a table of live sessions that is seconds, but
-- it is real. Expect a short maintenance window — apply the migration while traffic is
-- drained or between request bursts — and treat exit 3 (lock timeout, file rolled back
-- and unjournalled) as "retry when the table is quiet", not as a defect. The function
-- definitions below take only an AccessShareLock on `public.session` while PostgreSQL
-- validates their bodies, so they block no request and are safe whatever the traffic.
set local lock_timeout = '5s';

-- A privilege change flags every live session of the user; the next request with a
-- flagged session rotates it (D5b, AC7). Nullable, no default, no backfill: every
-- row that exists now has nothing pending.
alter table public.session
  add column if not exists rotation_required_at timestamptz;

-- Room for the sliding update to stay on its page (see the header): the two rewritten
-- columns are in no index, so a slide is a heap-only update as long as the page has
-- space for a second version.
alter table public.session set (fillfactor = 80);

-- The per-user paths only ever ask for live rows (`revoked_at is null`), and
-- `expires_at` leaves the index so the slide does not touch an indexed column.
drop index if exists public.session_user_revoked_expires_idx;
create index if not exists session_user_live_idx
  on public.session (user_id)
  where revoked_at is null;

-- The ONLY writer of `session` that also records the rotation chain (02.3.1-T1.4's
-- 0042 version gains `p_predecessor_hmacs`; D5a). Status contract unchanged: `ok`
-- (row inserted), `disabled` (no row), `unknown_user` (no row).
--
-- The forwarded cookie's row is revoked in the same statement as the insert — never
-- by a second round trip, which could leave two live sessions if the process died
-- between them — and `rotated_from` names it only when it is this user's session: a
-- stale cookie of a previous account in the same browser is killed like any other
-- forwarded session, but it is not this account's predecessor.
--
-- `revoked_at = now()` (not a parameter) HERE: the predecessor revoke below compares no
-- timestamp (only the HMAC candidates), so the column's only meaning to its readers is
-- "not null", and a caller's instant would let a bad clock make a revoked session look live
-- without buying anything; `session_rotate` and the revoke-all take it from `p_now` because
-- their statements DO compare deadlines against it. `created_at`/`last_seen_at` are the
-- opposite case: `last_seen_at` is what the idle slide of `session_authenticate` compares
-- against the caller's `p_now`, so BOTH are written from `p_now` — one clock for the row, and
-- no skew can slide on the first request (a direct caller that omits `p_now` gets the server
-- clock, which is then the only clock in play).
drop function if exists evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz);
drop function if exists evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz, bytea[]);

create or replace function evidgate_resolve.session_create(
  p_session_id uuid,
  p_user_id uuid,
  p_cookie_hmac bytea,
  p_pepper_version integer,
  p_ip text,
  p_user_agent text,
  p_expires_at timestamptz,
  p_absolute_expires_at timestamptz,
  -- Defaulted so a caller that has no forwarded cookie (every non-login writer, and the
  -- resolver tests that write a session directly) keeps calling the eight-argument form;
  -- `create or replace` cannot widen a signature, hence the drop above.
  p_predecessor_hmacs bytea[] default null,
  -- The instant `created_at`/`last_seen_at` carry. Defaulted to the server clock so a
  -- direct caller (the resolver tests of 0042/0044 write a row themselves) keeps working,
  -- while the real writer passes the clock every other instant of the login came from.
  p_now timestamptz default null)
returns table (session_id uuid, expires_at timestamptz, status text)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_status text;
  v_ip inet;
begin
  v_status := (select u.status from public."user" u where u.id = p_user_id);

  if v_status is null then
    return query select null::uuid, null::timestamptz, 'unknown_user'::text;
    return;
  end if;

  if v_status <> 'active' then
    return query select null::uuid, null::timestamptz, 'disabled'::text;
    return;
  end if;

  -- The address is client input: a literal that is not a valid address becomes NULL (the column is
  -- nullable) instead of failing a login every other gate has already admitted.
  begin
    v_ip := p_ip::inet;
  exception
    when others then
      v_ip := null;
  end;

  return query
  with predecessor as (
    update public.session s
       set revoked_at = now(),
           rotation_required_at = null
     where s.cookie_hmac = any (p_predecessor_hmacs)
       and s.revoked_at is null
    returning s.id, s.user_id
  ),
  created as (
    insert into public.session as ns (
      id, user_id, cookie_hmac, pepper_version, ip, user_agent, expires_at, absolute_expires_at,
      rotated_from, created_at, last_seen_at)
    values (
      p_session_id, p_user_id, p_cookie_hmac, p_pepper_version, v_ip, left(p_user_agent, 512),
      p_expires_at, p_absolute_expires_at,
      -- Only this user's forwarded session is the predecessor; another account's stale cookie is
      -- revoked (above) and this stays NULL.
      (select pr.id from predecessor pr where pr.user_id = p_user_id),
      coalesce(p_now, now()), coalesce(p_now, now()))
    returning ns.id, ns.expires_at
  )
  select c.id, c.expires_at, 'ok'::text
    from created c;
end
$$;

-- The cookie-authenticated request path (D2/D3/D4, AC2/AC3/AC4/AC8): ONE statement per
-- authenticated request, answering status + identity + rotation flag, sliding the idle
-- deadline at most once per 60 s and draining a retired pepper in the same statement.
-- Status vocabulary is closed: `unknown`, `revoked`, `expired`, `disabled`, `active`,
-- in exactly that precedence (an unknown hash is not an oracle, a revoked row is dead
-- even if it is also expired, and a disabled account is the caller's answer to render
-- as 403 only after the two expiry checks passed).
--
-- `session_id`/`user_id` are the zero uuid for `unknown` (no row to name), which is what
-- the caller maps to `Guid.Empty`.
create or replace function evidgate_resolve.session_authenticate(
  p_cookie_hmacs bytea[],
  p_current_hmac bytea,
  p_current_version integer,
  p_now timestamptz,
  p_idle_seconds integer)
returns table (
  status text,
  session_id uuid,
  user_id uuid,
  rotation_required boolean,
  slid boolean,
  rehashed boolean)
language sql
volatile
security definer
set search_path = ''
as $$
  -- One index probe on the __Host- cookie's unique key (`session_cookie_hmac_key`); at most one row,
  -- because the key is unique and the two candidates are two hashes of one presented value.
  with matched as (
    select s.id,
           s.user_id,
           s.revoked_at,
           s.expires_at,
           s.absolute_expires_at,
           s.last_seen_at,
           s.pepper_version,
           s.rotation_required_at,
           u.status as user_status
      from public.session s
      join public."user" u on u.id = s.user_id
     where s.cookie_hmac = any (p_cookie_hmacs)
  ),
  decided as (
    select m.id,
           m.user_id,
           m.absolute_expires_at,
           m.last_seen_at,
           m.pepper_version,
           m.rotation_required_at,
           case
             when m.revoked_at is not null then 'revoked'
             when m.expires_at <= p_now or m.absolute_expires_at <= p_now then 'expired'
             when m.user_status <> 'active' then 'disabled'
             else 'active'
           end as status
      from matched m
  ),
  -- The two lifecycle writes of the request path, decided before the update so the update can
  -- match nothing at all when neither is due (an unchanged request costs no write).
  actions as (
    select d.id,
           (d.status = 'active'
            and d.last_seen_at < p_now - interval '60 seconds') as slide,
           (d.status = 'active'
            and p_current_hmac is not null
            and d.pepper_version <> p_current_version) as rehash,
           d.absolute_expires_at
      from decided d
  ),
  -- The throttle is decided twice on purpose. `actions` above is the decision on the statement
  -- snapshot; the update repeats both predicates against `s` — the row version it actually takes —
  -- so under READ COMMITTED a request that loses the race writes NOTHING. Two requests on the same
  -- idle session both read `slide = true`, the first to reach the row writes it, and the second,
  -- after waiting for the row lock, re-evaluates this predicate on the committed version, sees
  -- `last_seen_at = p_now`, matches no row at all and answers `slid = false`. One slide write per
  -- 60 s per session, whoever asks (P1/P3).
  --
  -- The two SET cases repeat the same tests so that a request which is only due for the re-hash can
  -- never slide on top of a peer's write. `returning` reads the NEW row, so a slide is
  -- `last_seen_at = p_now` (the value the slide writes) and a re-hash is `pepper_version =
  -- p_current_version`; a request that matched no row never reaches `written`, so both flags report
  -- the writes THIS statement applied — the peer's slide is not this request's `slid`.
  written as (
    update public.session s
       set last_seen_at = case
                            when a.slide and s.last_seen_at < p_now - interval '60 seconds'
                              then p_now
                            else s.last_seen_at
                          end,
           -- The idle deadline slides but never past the hard one (AC2/AC3).
           expires_at = case
                          when a.slide and s.last_seen_at < p_now - interval '60 seconds'
                            then least(p_now + make_interval(secs => p_idle_seconds), a.absolute_expires_at)
                          else s.expires_at
                        end,
           cookie_hmac = case when a.rehash then p_current_hmac else s.cookie_hmac end,
           pepper_version = case when a.rehash then p_current_version else s.pepper_version end
      from actions a
     where s.id = a.id
       and ((a.slide and s.last_seen_at < p_now - interval '60 seconds')
            or (a.rehash and s.pepper_version <> p_current_version))
    returning s.id as id,
              (a.slide and s.last_seen_at = p_now) as slide,
              (a.rehash and s.pepper_version = p_current_version) as rehash
  )
  select coalesce(d.status, 'unknown'::text),
         coalesce(d.id, '00000000-0000-0000-0000-000000000000'::uuid),
         coalesce(d.user_id, '00000000-0000-0000-0000-000000000000'::uuid),
         coalesce(d.status = 'active' and d.rotation_required_at is not null, false),
         coalesce(w.slide, false),
         coalesce(w.rehash, false)
    from (select 1) as one(x)
    left join decided d on true
    left join written w on w.id = d.id
$$;

-- Rotation on privilege change (D5b, AC7). Status contract: `rotated` (successor
-- inserted, predecessor revoked, `rotated_from` set), `not_live` (no such session, or
-- it is revoked/expired — including the LOSER of two concurrent rotations, whose
-- answer must be the 401 `session_revoked` the old cookie deserves), `disabled` (the
-- account is not `active`, so no successor is minted).
-- The result tuple also carries the inherited hard deadline, so the cookie writer can bound a rotated
-- cookie's Max-Age by the row's real absolute lifetime rather than by its sliding one (02.3.2 COORDINATOR
-- addendum (b)). `create or replace` cannot widen a return type, hence the drop.
drop function if exists evidgate_resolve.session_rotate(uuid, uuid, bytea, integer, timestamptz, integer, text, text);

create or replace function evidgate_resolve.session_rotate(
  p_old_session_id uuid,
  p_new_session_id uuid,
  p_cookie_hmac bytea,
  p_pepper_version integer,
  p_now timestamptz,
  p_idle_seconds integer,
  p_ip text,
  p_user_agent text)
returns table (session_id uuid, expires_at timestamptz, absolute_expires_at timestamptz, status text)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_user_status text;
  v_ip inet;
begin
  -- `session.user_id` is a foreign key (0041), so a null status here means "no session row".
  v_user_status := (select u.status
                      from public.session s
                      join public."user" u on u.id = s.user_id
                     where s.id = p_old_session_id);

  if v_user_status is null then
    return query select null::uuid, null::timestamptz, null::timestamptz, 'not_live'::text;
    return;
  end if;

  if v_user_status <> 'active' then
    return query select null::uuid, null::timestamptz, null::timestamptz, 'disabled'::text;
    return;
  end if;

  begin
    v_ip := p_ip::inet;
  exception
    when others then
      v_ip := null;
  end;

  -- Single winner: the update is the lock. The first rotation of this row finds it live and revokes
  -- it; a concurrent one blocks on the row lock, re-evaluates the predicate against the committed
  -- version, matches nothing, and inserts no successor (the insert selects FROM the update).
  return query
  with revoked as (
    update public.session s
       set revoked_at = p_now,
           rotation_required_at = null
     where s.id = p_old_session_id
       and s.revoked_at is null
       and s.expires_at > p_now
       and s.absolute_expires_at > p_now
    returning s.user_id, s.ip, s.user_agent, s.absolute_expires_at
  ),
  successor as (
    insert into public.session as ns (
      id, user_id, cookie_hmac, pepper_version, ip, user_agent, expires_at, absolute_expires_at,
      rotated_from, created_at, last_seen_at)
    select p_new_session_id,
           r.user_id,
           p_cookie_hmac,
           p_pepper_version,
           coalesce(v_ip, r.ip),
           coalesce(left(nullif(p_user_agent, ''), 512), r.user_agent),
           least(p_now + make_interval(secs => p_idle_seconds), r.absolute_expires_at),
           r.absolute_expires_at,
           p_old_session_id,
           -- The rotation instant, from the SAME clock that judged the old row (`p_now`): the successor's
           -- throttle window starts here, and the next slide decision must not see a server-clock value.
           p_now,
           p_now
      from revoked r
    returning ns.id, ns.expires_at, ns.absolute_expires_at
  )
  -- Always exactly one row: a rotation that matched no live predecessor (the loser of a race, a
  -- revoked or an expired session) answers `not_live` and inserts nothing.
  select n.id,
         n.expires_at,
         n.absolute_expires_at,
         case when n.id is null then 'not_live' else 'rotated' end
    from (select 1) as one(x)
    left join successor n on true;
end
$$;

-- "Kill every session of this user" (AC4/AC6, 02.1.2 AC3): `ISessionRevocation` runs it on
-- the caller's connection and transaction, so a rolled-back member removal rolls the
-- revocation back with it. Returns the number of rows revoked; an expired-but-unrevoked
-- row is stamped too (the state machine is explicit, and the next request answers
-- `revoked`, which is the truth).
create or replace function evidgate_resolve.session_revoke_all_for_user(
  p_user_id uuid,
  p_now timestamptz)
returns table (revoked_count integer)
language sql
volatile
security definer
set search_path = ''
as $$
  with revoked as (
    update public.session s
       set revoked_at = p_now,
           rotation_required_at = null
     where s.user_id = p_user_id
       and s.revoked_at is null
    returning 1 as one
  )
  select count(*)::integer
    from revoked
$$;

-- Mark the user's live sessions for rotation (D5b): the role-change transaction calls it,
-- so the next request with each session rotates it (AC7). Every live row is stamped, an
-- already-flagged one included — the flag must carry the LATEST privilege change.
create or replace function evidgate_resolve.session_require_rotation(
  p_user_id uuid,
  p_now timestamptz)
returns table (marked_count integer)
language sql
volatile
security definer
set search_path = ''
as $$
  with marked as (
    update public.session s
       set rotation_required_at = p_now
     where s.user_id = p_user_id
       and s.revoked_at is null
       and s.expires_at > p_now
       and s.absolute_expires_at > p_now
    returning 1 as one
  )
  select count(*)::integer
    from marked
$$;

-- The one writer of a provider link, re-signed (D8, residual SEC2 of 02.3.1-T2): the acting
-- user is resolved from the presented cookie hash here, inside the definer, and never
-- accepted as an argument. Status contract, always a row, never an exception for a
-- business outcome: `linked`, `already_linked`, `moved` (returned in
-- moved_from_user_id), `conflict`, `unauthenticated` (no live session for any candidate,
-- which covers unknown/revoked/expired alike) and `disabled` (a live session whose
-- account is no longer active). `unknown_user` is gone: the user is the session's.
drop function if exists evidgate_resolve.login_link_identity(uuid, text, text, text, uuid);

create or replace function evidgate_resolve.login_link_identity(
  p_cookie_hmacs bytea[],
  p_now timestamptz,
  p_provider text,
  p_subject text,
  p_email text,
  p_identity_id uuid)
returns table (outcome text, moved_from_user_id uuid)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_user_id uuid;
  v_user_status text;
  v_owner uuid;
begin
  -- The acting user, from the credential itself: unrevoked, unexpired on both clocks. A NULL or
  -- empty array matches no row (a NULL comparison is never true), so a request without a usable
  -- cookie never reaches the identity table.
  v_user_id := (select s.user_id
                  from public.session s
                 where s.cookie_hmac = any (p_cookie_hmacs)
                   and s.revoked_at is null
                   and s.expires_at > p_now
                   and s.absolute_expires_at > p_now);

  if v_user_id is null then
    return query select 'unauthenticated'::text, null::uuid;
    return;
  end if;

  -- The target account, locked, `for no key update` rather than `for update`: the move below writes
  -- `user_identity.user_id`, whose foreign-key check takes FOR KEY SHARE on the referenced `user` row,
  -- and FOR UPDATE would wait on it — with two crossed link attempts that is a deadlock (the
  -- reasoning D-S78-Q5 records for the same choice on the organization row). The lock still
  -- serialises against a concurrent disable or key change of the account.
  v_user_status := (select u.status
                      from public."user" u
                     where u.id = v_user_id
                       for no key update);

  if v_user_status is distinct from 'active' then
    return query select 'disabled'::text, null::uuid;
    return;
  end if;

  -- The identity row is the serializer for two concurrent confirms of one (provider, subject): whoever
  -- gets here first decides, the other sees the committed shape and answers `already_linked`. When the
  -- row does not exist yet the unique key (0041:112-114) is the arbiter instead — the insert below
  -- loses to it and the re-read sees the winner. `user_identity.user_id` is NOT NULL, so a null here
  -- means the identity is free.
  v_owner := (select ui.user_id
                from public.user_identity ui
               where ui.provider = p_provider
                 and ui.subject = p_subject
                 for update);

  if v_owner is null then
    insert into public.user_identity (id, user_id, provider, subject, email_at_provider)
    values (p_identity_id, v_user_id, p_provider, p_subject, p_email)
    on conflict (provider, subject) do nothing;

    if found then
      return query select 'linked'::text, null::uuid;
      return;
    end if;

    -- Lost the race: the winner's row is visible now (ON CONFLICT waited for it), and it is locked so
    -- the decision below cannot change under us.
    v_owner := (select ui.user_id
                  from public.user_identity ui
                 where ui.provider = p_provider
                   and ui.subject = p_subject
                   for update);

    if v_owner is null then
      -- Unreachable: the conflicting row was committed, or our own insert would have succeeded. Fail
      -- closed rather than answer `moved` for a row nobody owns. The message carries no identifier.
      raise exception 'the linked identity row vanished between the conflict and the re-read'
        using errcode = 'P0002';
    end if;
  end if;

  if v_owner = v_user_id then
    return query select 'already_linked'::text, null::uuid;
    return;
  end if;

  -- The identity is on another account: it may be moved only from a user that has nothing to lose.
  -- A membership (any status: `removed` still records a person) or a live session means the other
  -- account is in use, and the answer is `conflict` — never a merge, never a silent takeover.
  if exists (
    select 1
      from public.membership m
     where m.user_id = v_owner)
  then
    return query select 'conflict'::text, null::uuid;
    return;
  end if;

  if exists (
    select 1
      from public.session s
     where s.user_id = v_owner
       and s.revoked_at is null
       and s.expires_at > p_now
       and s.absolute_expires_at > p_now)
  then
    return query select 'conflict'::text, null::uuid;
    return;
  end if;

  update public.user_identity ui
     set user_id = v_user_id
   where ui.provider = p_provider
     and ui.subject = p_subject;

  return query select 'moved'::text, v_owner;
end
$$;

-- The read-only lookup of 0044 is gone with the authentication path it served (D8): it could not
-- express `revoked` vs `expired` vs `disabled`, could not slide the idle deadline, and was the only
-- reason the linking routes held a second way to authenticate a cookie.
drop function if exists evidgate_resolve.session_user(bytea, integer);

revoke all on function evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz, bytea[], timestamptz) from public;
revoke all on function evidgate_resolve.session_authenticate(bytea[], bytea, integer, timestamptz, integer) from public;
revoke all on function evidgate_resolve.session_rotate(uuid, uuid, bytea, integer, timestamptz, integer, text, text) from public;
revoke all on function evidgate_resolve.session_revoke_all_for_user(uuid, timestamptz) from public;
revoke all on function evidgate_resolve.session_require_rotation(uuid, timestamptz) from public;
revoke all on function evidgate_resolve.login_link_identity(bytea[], timestamptz, text, text, text, uuid) from public;

grant execute on function evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz, bytea[], timestamptz) to evidgate_app;
grant execute on function evidgate_resolve.session_authenticate(bytea[], bytea, integer, timestamptz, integer) to evidgate_app;
grant execute on function evidgate_resolve.session_rotate(uuid, uuid, bytea, integer, timestamptz, integer, text, text) to evidgate_app;
grant execute on function evidgate_resolve.session_revoke_all_for_user(uuid, timestamptz) to evidgate_app;
grant execute on function evidgate_resolve.session_require_rotation(uuid, timestamptz) to evidgate_app;
grant execute on function evidgate_resolve.login_link_identity(bytea[], timestamptz, text, text, text, uuid) to evidgate_app;
