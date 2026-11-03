-- 0061_identity_user_profile.sql
--
-- Owner: 02.3.1-T3. The own-profile read and the locale preference write of
-- 02.3.1 §4/§10 T3: the columns `GET /v1/me` projects and the token its
-- `PATCH /v1/me` carries back in `If-Match`.
--
--   * `user.preferred_locale text` — the account's UI language, `vi` or `en`, and
--     NULL until the account has chosen one. Nullable, NO default, NO backfill:
--     "has not chosen" is a real state the read reports as null (AC9), and a
--     default would silently invent a choice nobody made.
--   * `user.version integer not null default 1` — the optimistic-concurrency
--     token of 00 §7 on the one row the account can edit about itself. The
--     default backfills every existing row in the catalogue, so the ALTER is
--     catalog-only in PostgreSQL 11+ (no table rewrite) and the NOT NULL holds
--     from the first read.
--   * the shared `set_updated_at_and_version` trigger (0004:70-87, one BEFORE
--     UPDATE FOR EACH ROW trigger per table): the database owns the increment, so
--     two PATCHes that read the same version cannot both win — the loser's
--     `where version = @expected` matches no row.
--
-- Two SECURITY DEFINER functions, owner-owned, both in `evidgate_resolve`:
--
--   * `user_profile_read(p_cookie_hmacs bytea[], p_now timestamptz)` — the
--     caller's own profile row (id, email, display name, locale, version), or one
--     `unauthenticated` row when the presented proof matches no live session.
--   * `user_profile_update(p_cookie_hmacs bytea[], p_preferred_locale text,
--     p_expected_version integer, p_now timestamptz)` — the guarded write; answers
--     `ok` with the new version, `stale` when the version predicate matched no row
--     (the caller's next read shows the current one), or `unauthenticated`.
--
-- Both resolve the acting user from the presented session proof INSIDE the
-- function, never as an argument (the 0045:624-629 / 0048:160-167 predicate
-- verbatim): `cookie_hmac = any (p_cookie_hmacs)` against the unique
-- `session_cookie_hmac_key`, unrevoked and unexpired on both clocks. No function
-- takes a user id, so no route value and no body can name an account, and
-- `GET`/`PATCH /v1/me` cannot read or write another user's row (AC9). Neither
-- returns `cookie_hmac`, `pepper_version` or any other credential column.
--
-- Why the two definers are safe (TL T80, the same hygiene as 0014/0042/0045/0048):
-- owned by `evidgate_owner`, whose `user_owner_backfill` policy (0041:193-198)
-- lets it read and write `public."user"` under FORCE row level security; no
-- dynamic SQL; `set search_path = ''` with schema-qualified references only, so no
-- object the caller creates can shadow a table, type or operator; the built-in
-- EXECUTE grant to PUBLIC is revoked per function and EXECUTE goes to `evidgate_app`
-- alone, the only role that serves a request.
--
-- Why a definer at all: 0041:222 revokes every privilege on `public."user"` from the
-- runtime roles, so the pre-tenant request path cannot read or write the row
-- directly. These two functions are the only door, exactly as
-- `login_upsert_identity` and the session functions are for their tables.
--
-- The locale grammar is enforced twice on purpose: `contracts/openapi.yaml` names the
-- two values and the route refuses anything else with 422 `locale_invalid` (a caller
-- error, before a transaction), and the CHECK below is the last line of defence for
-- any future writer that skips the route.
--
-- Deliberately absent, each owned by another task:
--
--   * a per-call audit row from either definer. The org-scoped `user.locale_changed`
--     row is written AFTER the commit by the application's `IIdentityAuditWriter`
--     (one chain per active org, fail-closed 503), the `login_link_identity`
--     precedent of DEFINER_AUDIT_EXEMPT: a definer cannot extend a hash chain, and a
--     bare row would break the chain the verifier walks.
--   * a `user.preferred_locale` index. The column is read by primary key only.
--
-- Rollback (playbook `docs/migrations-expand-contract.md`, mechanical, never executed
-- in place). Every statement here is additive and the two functions have one caller:
--   drop function if exists evidgate_resolve.user_profile_update(bytea[], text, integer, timestamptz);
--   drop function if exists evidgate_resolve.user_profile_read(bytea[], timestamptz);
--   drop trigger if exists user_set_updated_at_and_version on public."user";
--   alter table public."user" drop constraint if exists user_preferred_locale_check;
--   alter table public."user" drop column if exists version;
--   alter table public."user" drop column if exists preferred_locale;
-- Dropping the columns discards the locale each account chose and the version every
-- client held in `If-Match`; clients re-read their profile and continue at version 1.
--
-- Locking: both `add column` statements are catalog-only (a NULL column with no
-- default, and a NOT NULL column whose default is catalogue metadata since
-- PostgreSQL 11), so nothing is rewritten and no backfill runs; the CHECK scans
-- every existing row (all NULL), which is the only statement here that touches every row — a
-- full read of the identity table, small, and the one statement here that touches
-- every row. PostgreSQL takes ACCESS EXCLUSIVE on the table for the length of this
-- file's transaction, so `set local lock_timeout` below bounds every lock this
-- transaction waits for to 5 s. Exit 3 (lock timeout, file rolled back and
-- unjournalled) means "retry when the table is quiet", not a defect. The function
-- definitions take only an AccessShareLock while PostgreSQL validates their bodies.
set local lock_timeout = '5s';

alter table public."user"
  add column if not exists preferred_locale text;

alter table public."user"
  add column if not exists version integer not null default 1;

alter table public."user"
  drop constraint if exists user_preferred_locale_check;

-- NULL passes (the account has not chosen yet); anything else must be one of the two
-- languages the contract names, so a writer that skips the route cannot store a third.
-- Plain `add constraint`: the column was added above, so every existing row is NULL and the
-- scan is trivial; splitting NOT VALID and VALIDATE into two migrations would buy no lock
-- benefit on this small platform table.
alter table public."user"
  add constraint user_preferred_locale_check
  check (preferred_locale in ('vi', 'en'));

-- (profile columns and their check are in place; the trigger follows)

drop trigger if exists user_set_updated_at_and_version on public."user";
create trigger user_set_updated_at_and_version
  before update on public."user"
  for each row
  execute function public.set_updated_at_and_version();

-- The profile read (AC9). `status` is a column, not a second result set: an
-- authenticated caller always resolves exactly one live session, so an
-- `unauthenticated` answer and an empty profile can never be confused. The email and
-- display name are the row's own values: the profile of the account itself, never a
-- projection over another member. The owning account must be `active` as well: the
-- request authenticator answers 403 `account_disabled` first, and this predicate is
-- the same door one layer down, so a session that survives a disable resolves no
-- acting user and the function still answers exactly one row.
create or replace function evidgate_resolve.user_profile_read(
  p_cookie_hmacs bytea[],
  p_now timestamptz)
returns table (
  status text,
  user_id uuid,
  email text,
  display_name text,
  preferred_locale text,
  version integer)
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_user_id uuid;
begin
  -- The acting user, from the credential itself (the 0045/0048 predicate verbatim).
  select s.user_id
    into v_user_id
    from public.session s
    join public."user" u on u.id = s.user_id and u.status = 'active'
   where s.cookie_hmac = any (p_cookie_hmacs)
     and s.revoked_at is null
     and s.expires_at > p_now
     and s.absolute_expires_at > p_now;

  if v_user_id is null then
    return query
      select 'unauthenticated'::text, null::uuid, null::text, null::text, null::text, null::integer;
    return;
  end if;

  return query
    select 'ok'::text,
           u.id,
           u.email,
           u.display_name,
           u.preferred_locale,
           u.version
      from public."user" u
     where u.id = v_user_id;
end
$$;

-- The locale write (AC9). The guarded UPDATE is the arbiter, exactly like
-- `session_revoke_one`'s: the version predicate and the liveness predicate run in the
-- statement that takes the row lock, so two concurrent PATCHes that read the same
-- ETag cannot both win — the loser matches nothing and answers `stale`, which the
-- route renders as 412 with the version its next read shows. The trigger increments
-- `version` on the row this statement writes, and `returning` hands the caller the
-- value its next `If-Match` must carry. Like the read, both the session resolution and
-- the UPDATE itself require the owning account to be `active`, so no disable that lands
-- between them can still write a locale for a disabled account.
create or replace function evidgate_resolve.user_profile_update(
  p_cookie_hmacs bytea[],
  p_preferred_locale text,
  p_expected_version integer,
  p_now timestamptz)
returns table (
  status text,
  user_id uuid,
  email text,
  display_name text,
  preferred_locale text,
  version integer)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_user_id uuid;
  v_profile_id uuid;
  v_email text;
  v_display_name text;
  v_locale text;
  v_version integer;
begin
  select s.user_id
    into v_user_id
    from public.session s
    join public."user" u on u.id = s.user_id and u.status = 'active'
   where s.cookie_hmac = any (p_cookie_hmacs)
     and s.revoked_at is null
     and s.expires_at > p_now
     and s.absolute_expires_at > p_now;

  if v_user_id is null then
    return query
      select 'unauthenticated'::text, null::uuid, null::text, null::text, null::text, null::integer;
    return;
  end if;

  update public."user" u
     set preferred_locale = p_preferred_locale
   where u.id = v_user_id
     and u.status = 'active'
     and u.version = p_expected_version
  returning u.id, u.email, u.display_name, u.preferred_locale, u.version
       into v_profile_id, v_email, v_display_name, v_locale, v_version;

  if v_profile_id is null then
    -- Either the PATCH was stale or a concurrent writer won between the read the
    -- client made and this statement; both answer the same way, with the version the
    -- next read carries, so the client can retry once with a fresh `If-Match`.
    return query
      select 'stale'::text,
             v_user_id,
             null::text,
             null::text,
             null::text,
             (select u.version from public."user" u where u.id = v_user_id);
    return;
  end if;

  return query select 'ok'::text, v_profile_id, v_email, v_display_name, v_locale, v_version;
end
$$;

revoke all on function evidgate_resolve.user_profile_read(bytea[], timestamptz) from public;
grant execute on function evidgate_resolve.user_profile_read(bytea[], timestamptz) to evidgate_app;

revoke all on function evidgate_resolve.user_profile_update(bytea[], text, integer, timestamptz) from public;
grant execute on function evidgate_resolve.user_profile_update(bytea[], text, integer, timestamptz) to evidgate_app;
