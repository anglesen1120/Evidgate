-- 0065_identity_session_user_lock.sql
--
-- Owner: 02.3.2 (session store) and 02.1.2 (member removal); closes the S207 carried rows
-- "Session rotate/login vs revoke-all race on member removal" and "Partial index for the
-- removed-member invite lookup".
--
--   * Race. `ISessionRevocation.RevokeAllForUserAsync` (member removal) stamps the user's live
--     sessions, but a `session_rotate` or `session_create` that took its snapshot before that
--     statement committed could still insert a live successor afterwards: the row lock of the
--     predecessor protects one session, not the user. The three 0045 definers that write
--     sessions of a user (`session_create`, `session_rotate`, `session_revoke_all_for_user`)
--     now take one transaction-scoped advisory lock per user, through ONE helper
--     (`evidgate.session_user_lock`), before they read or write session rows. Under
--     READ COMMITTED the statements after the lock take a fresh snapshot, so a rotation that
--     waited behind a committed revoke-all matches no live predecessor and answers `not_live`,
--     and a login that waited creates its session after the removal (which grants nothing: the
--     org routes re-check the active membership per request).
--   * Lock key. `pg_advisory_xact_lock(hashtext('evidgate.session_user'), hashtext(user_id::text))`
--     is the two-int form, a different key space from the one-bigint audit chain lock
--     (`hashtext('audit:' || org)`, 0050) and from the case-prefix lock (first key
--     `evidgate.case_prefix`, 0039); the first key is this feature's namespace. Different users
--     hash to different keys (a collision only serialises two users for one statement group).
--     The lock is held to commit, so the caller's transaction (member removal) keeps it until the
--     removal itself is durable. Lock order: this lock first, then the session row locks of the
--     single UPDATE; none of the three definers takes the audit chain lock, so no cycle exists
--     with the callers that take `audit:` afterwards.
--   * Signatures, return types, `security definer`, `search_path = ''` and grants of the three
--     functions are those of 0045; the bodies differ only by the lock lines (and, for
--     `session_rotate`, the user-id pre-read). `session_revoke_all_for_user` changes language
--     from sql to plpgsql so the lock is its own statement and the UPDATE snapshot follows it.
--   * Index. `InviteMember` refuses an address with a removed membership that carries a user
--     (`org_id = $1 and invited_email = $2 and status = 'removed' and user_id is not null`);
--     the partial index below serves that probe. Admin path, low rate.
--
-- Not changed: `session_authenticate` (the S85 residual (a) predicate stays an accepted
-- residual, the row is untouched).

-- Plain (invoker) function in `evidgate`, not `evidgate_resolve`, on purpose: every function of
-- that schema is a definer the app role may call (ResolverTests), and this one touches no table,
-- only the lock manager, and must be reachable solely from the owner-run definers below.
create or replace function evidgate.session_user_lock(p_user_id uuid)
returns void
language sql
volatile
set search_path = ''
as $$
  select pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtext('evidgate.session_user'),
    pg_catalog.hashtext(p_user_id::text))
$$;

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
  perform evidgate.session_user_lock(p_user_id);

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
  v_user_id uuid;
  v_user_status text;
  v_ip inet;
begin
  -- `session.user_id` is a foreign key (0041), so a null user here means "no session row". The
  -- owner is read first only to know whose lock to take; the status is read again under it.
  v_user_id := (select s.user_id from public.session s where s.id = p_old_session_id);

  if v_user_id is null then
    return query select null::uuid, null::timestamptz, null::timestamptz, 'not_live'::text;
    return;
  end if;

  perform evidgate.session_user_lock(v_user_id);

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
-- revocation back with it. Returns the number of rows revoked. Now plpgsql so the per-user
-- lock is a statement of its own and the UPDATE takes its snapshot after the lock.
create or replace function evidgate_resolve.session_revoke_all_for_user(
  p_user_id uuid,
  p_now timestamptz)
returns table (revoked_count integer)
language plpgsql
volatile
security definer
set search_path = ''
as $$
begin
  perform evidgate.session_user_lock(p_user_id);

  return query
  with revoked as (
    update public.session s
       set revoked_at = p_now,
           rotation_required_at = null
     where s.user_id = p_user_id
       and s.revoked_at is null
    returning 1 as one
  )
  select count(*)::integer
    from revoked;
end
$$;

-- The schema's default privileges grant EXECUTE to the app role on every new function; the helper
-- must not be one of them.
revoke all on function evidgate.session_user_lock(uuid) from public, evidgate_app;
revoke all on function evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz, bytea[], timestamptz) from public;
revoke all on function evidgate_resolve.session_rotate(uuid, uuid, bytea, integer, timestamptz, integer, text, text) from public;
revoke all on function evidgate_resolve.session_revoke_all_for_user(uuid, timestamptz) from public;

grant execute on function evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz, bytea[], timestamptz) to evidgate_app;
grant execute on function evidgate_resolve.session_rotate(uuid, uuid, bytea, integer, timestamptz, integer, text, text) to evidgate_app;
grant execute on function evidgate_resolve.session_revoke_all_for_user(uuid, timestamptz) to evidgate_app;

-- A SHARE lock on a busy membership table queues its writers: fail the migration (retryable)
-- instead of waiting without bound.
set local lock_timeout = '5s';

create index if not exists membership_removed_invited_email_idx
  on public.membership (org_id, invited_email)
  where status = 'removed';
