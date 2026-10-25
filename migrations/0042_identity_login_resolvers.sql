-- 0042_identity_login_resolvers.sql
--
-- Owner: 02.3.1-T1.4. Two things a completed federated login needs and 0041
-- deliberately left out (0041:26-38, 0041:16-18):
--
--   * the session credential columns — `session.cookie_hmac` and
--     `session.pepper_version`, plus the unique index a cookie-authenticated
--     request looks a session up by. The cookie VALUE is never stored: the login
--     mints 32 random bytes, hands them to the browser as the `__Host-` cookie and
--     keeps only HMAC-SHA256(pepper_vN, value), so a database read yields no usable
--     credential (02.3.1 §8, 02.3.2 §4, TL T5 — the same construction as the ingest
--     tokens and binding challenges of 0016/0018). `pepper_version` travels with the
--     hash so a rotation can keep the old pepper alive while its sessions drain.
--   * the SECURITY DEFINER writers of the platform identity tables. `evidgate_app`
--     holds NO privilege on `user`, `user_identity` and `session` (0041:214-224), and
--     0041 has no `TO evidgate_app` policy either, because a direct read or write of
--     a human's identity from a tenant-scoped request is exactly the hole the
--     resolver path exists to close. The login path is pre-tenant and pre-session, so
--     its two writes go through owner-owned functions of `evidgate_resolve`, the
--     schema 0002 created for this purpose:
--
--       - `login_upsert_identity(...)` — the ONLY writer of `user` and
--         `user_identity` (02.3.1 §5). Identity is `(provider, subject)` and nothing
--         else: the same address at two providers stays two rows and two users until
--         an explicit confirm merges them (D3, AC6), so this function never looks at
--         `user.email`. A first login creates both rows; a repeat login refreshes
--         `last_login_at`, `email_at_provider` and (only when the provider supplies a
--         new non-null one) `display_name`, and never rewrites `user.email` — the
--         address stored with the account is the one it was created with. Two
--         concurrent first logins of one identity must end with exactly one user and
--         one identity: the unique key `(provider, subject)` decides the winner, the
--         loser deletes the user row it just created (no orphan user, no "empty"
--         account whose email the next login would rewrite) and re-reads the winner.
--       - `session_create(...)` — the ONLY writer of `session` (AC1 needs the row at
--         login; 02.3.2-T1 owns the lifecycle). It re-checks the user inside the
--         function rather than trusting the caller: a disabled account gets status
--         `disabled` and no row, an unknown user id gets `unknown_user` and no row.
--         The client address is untrusted text (a header-derived value), so a bad
--         `inet` literal is stored as NULL instead of aborting a login that has
--         already passed every other gate.
--
-- Why both are SECURITY DEFINER and why that is safe: they are owned by
-- `evidgate_owner`, whose `*_owner_backfill` policies (0041:193-212) let it write
-- under FORCE row level security; `evidgate_app` gets EXECUTE only. Hygiene of every
-- SECURITY DEFINER function (TL T80, identical to 0014/0016/0018): `set search_path =
-- ''`, schema-qualified references only, no dynamic SQL, no EXECUTE for PUBLIC.
--
-- The two new `session` columns are `not null` without a default, which is safe here
-- and only here: 0041 created the table for this story and nothing writes it yet —
-- there is no row to backfill, and a writer that forgets the credential cannot insert
-- a session at all.
--
-- Deliberately absent, each owned by another task:
--
--   * middleware, cookie verification on the request path, rotation, idle/absolute
--     expiry sweeps and the partial hot-path index (`where revoked_at is null`) —
--     02.3.2-T1's `NNNN_identity_session_hardening.sql` (02.3.2 §4). This file adds
--     the columns those writes need and the unique lookup key; it changes no existing
--     index, so 02.3.2-T1 still decides the shape of the sliding-expiry index over
--     0041's `session_user_revoked_expires_idx` (0041:158-180).
--   * the invite bind and `allowed_email_domains` gate (02.1.2-T2, 02.3.1-T2.1) and the
--     second-provider link flow (02.3.1-T2.2) — both call these functions, neither is
--     written here.
--
-- lint: audit-deferred 02.7.1-T2.2 evidgate_resolve.login_upsert_identity, evidgate_resolve.session_create
-- The per-call audit stub of 02.6.1 AC6 is not written here, exactly like 0014: `audit_log`
-- (0015) is tenant-scoped and its writer is 02.7.1-T2.2's retrofit of every resolver — a
-- pre-tenant login has no org to attribute an audit row to until then.

-- NOT NULL without a default on purpose (the header records why it is safe: no row exists yet).
alter table public.session
  add column if not exists cookie_hmac bytea not null,
  add column if not exists pepper_version integer not null;

-- The bound of each value is a constraint, not a convention: a 32-byte HMAC-SHA256 (the digest length
-- is what makes "stored only as a hash" meaningful) and a pepper version that can be looked up.
alter table public.session
  drop constraint if exists session_cookie_hmac_length_check,
  drop constraint if exists session_pepper_version_check;

alter table public.session
  add constraint session_cookie_hmac_length_check check (octet_length(cookie_hmac) = 32),
  add constraint session_pepper_version_check check (pepper_version >= 1);

-- The cookie-authenticated request path (02.3.2-T1) resolves a session by the hash of the presented
-- cookie, so the hash is unique by construction: two rows for one value would make "the" session of a
-- cookie ambiguous, and a duplicate would only ever come from a broken mint.
create unique index if not exists session_cookie_hmac_key
  on public.session (cookie_hmac);

-- The only writer of `user` and `user_identity` (02.3.1 §5): upsert on `(provider, subject)`.
-- Returns the owning user and its status, which is what the caller maps (active: proceed,
-- disabled: 403 account_disabled). `p_user_id`/`p_identity_id` are app-generated UUIDv7 ids (00 §7);
-- passing them in keeps the id decision in the command layer and this function free of a generator.
create or replace function evidgate_resolve.login_upsert_identity(
  p_provider text,
  p_subject text,
  p_email text,
  p_display_name text,
  p_user_id uuid,
  p_identity_id uuid)
returns table (user_id uuid, status text)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_user_id uuid;
  v_status text;
begin
  -- The identity key, and only it (D3/AC6): `email` is never a lookup key here.
  v_user_id := (select ui.user_id
                  from public.user_identity ui
                 where ui.provider = p_provider
                   and ui.subject = p_subject);

  if v_user_id is null then
    -- A provider `name` is unbounded and `user.display_name` is 1..200 (0041), so the value is cut here,
    -- the same way a bad address is nulled in `session_create`: a long profile name must not abort a login
    -- that every other gate admitted. An empty or absent name stays NULL rather than violating the lower bound.
    insert into public."user" (id, email, display_name, last_login_at)
    values (
      p_user_id,
      p_email,
      nullif(left(coalesce(p_display_name, ''), 200), ''),
      now());

    -- The unique key decides a concurrent first login of the same identity (also validated by
    -- `user_identity_provider_check` on `provider`, so an unknown provider never lands here).
    insert into public.user_identity (id, user_id, provider, subject, email_at_provider)
    values (p_identity_id, p_user_id, p_provider, p_subject, p_email)
    on conflict (provider, subject) do nothing;

    if found then
      v_user_id := p_user_id;
    else
      -- Lost the race: the user this call created has no identity and must not survive as an orphan.
      delete from public."user" u
       where u.id = p_user_id
         and not exists (select 1 from public.user_identity ui where ui.user_id = p_user_id);
      v_user_id := (select ui.user_id
                      from public.user_identity ui
                     where ui.provider = p_provider
                       and ui.subject = p_subject);
    end if;
  end if;

  if v_user_id is null then
    -- Unreachable: the insert above either created the identity or found the winner. Fail closed
    -- rather than hand back a session for an account nobody owns. The message carries no identifier.
    raise exception 'the verified identity has no owning user after upsert'
      using errcode = 'P0002';
  end if;

  v_status := (select u.status from public."user" u where u.id = v_user_id);

  if v_status = 'active' then
    update public."user" u
       set last_login_at = now(),
           -- A provider that stopped sending a name (or sent an empty one) must not erase the one we have.
           display_name = coalesce(
             nullif(left(coalesce(p_display_name, ''), 200), ''),
             u.display_name),
           updated_at = now()
     where u.id = v_user_id;

    update public.user_identity ui
       set email_at_provider = p_email
     where ui.provider = p_provider
       and ui.subject = p_subject;
  end if;

  return query select v_user_id, v_status;
end
$$;

-- The only writer of `session` (02.3.1-T1.4). Status contract: `ok` (row inserted), `disabled` (the
-- account is disabled: no row), `unknown_user` (no such user: no row). A disabled or unknown account is
-- a value here, never an exception, so the caller renders a typed 00 §9.2 answer.
create or replace function evidgate_resolve.session_create(
  p_session_id uuid,
  p_user_id uuid,
  p_cookie_hmac bytea,
  p_pepper_version integer,
  p_ip text,
  p_user_agent text,
  p_expires_at timestamptz,
  p_absolute_expires_at timestamptz)
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
  -- nullable) instead of failing a login every other gate has already admitted. `user_agent` is bounded
  -- to the column's 512 characters below for the same reason (the store already cuts it surrogate-safely).
  begin
    v_ip := p_ip::inet;
  exception
    when others then
      v_ip := null;
  end;

  insert into public.session (
    id, user_id, cookie_hmac, pepper_version, ip, user_agent, expires_at, absolute_expires_at)
  values (
    p_session_id, p_user_id, p_cookie_hmac, p_pepper_version, v_ip, left(p_user_agent, 512),
    p_expires_at, p_absolute_expires_at);

  return query select p_session_id, p_expires_at, 'ok'::text;
end
$$;

revoke all on function evidgate_resolve.login_upsert_identity(text, text, text, text, uuid, uuid) from public;
revoke all on function evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz) from public;
grant execute on function evidgate_resolve.login_upsert_identity(text, text, text, text, uuid, uuid) to evidgate_app;
grant execute on function evidgate_resolve.session_create(uuid, uuid, bytea, integer, text, text, timestamptz, timestamptz) to evidgate_app;
