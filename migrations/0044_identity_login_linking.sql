-- 0044_identity_login_linking.sql
--
-- Owner: 02.3.1-T2. The schema half of AC4/AC5/AC6 (feature plan 02.3.1 §2, §4, §10
-- `02.3.1-T2`; invite plan 02.1.2 §3c/§4): the foreign key the bind makes checkable,
-- the read-only session lookup a cookie-authenticated request needs, and the one
-- writer of the second-provider link.
--
--   * `membership_user_fk` — `membership.user_id -> public."user" (id)`. 0011 left the
--     reference by value and 0041:46-53 recorded why (the invite path wrote a
--     `user_id` before any `user` row existed, and the membership fixtures inserted
--     arbitrary ids). The bind of 02.1.2-T2.2 now writes `user_id` for the id the
--     login upsert of 0042 created, in the same call, so the column is checkable:
--     [DECISION D-S78-Q4] and the T2 note at 02.3.1:167 both hand the key to this
--     task. The column STAYS NULLABLE — NULL passes a foreign key (MATCH SIMPLE), and
--     a pending invite has no user yet; what the key adds is that a non-null value
--     names a real account.
--
--     Added the 0031 way (`artifact_run_fk`, 0031:225-256), because `membership` is a
--     live tenant table and not a table this story created:
--       1. `add constraint ... not valid` writes the catalogue only — it does not scan
--          the table — so the step that can fail for data reasons is the explicit one
--          below, and not an implicit scan inside the `add`. Honest caveat: the
--          Migrator applies one file in ONE transaction (`WithTransactionPerScript`,
--          this README), so the ACCESS EXCLUSIVE lock the `add` takes is held until
--          the file commits and the scan runs under it either way. What the two-step
--          form buys here is the named failure (next point) and the shape an operator
--          needs when the same statements are applied to a large live table outside
--          the runner: add + commit (momentary lock), verify, then
--          `validate constraint` alone (SHARE UPDATE EXCLUSIVE, which blocks neither
--          reads nor writes).
--       2. The `$orphans$` block counts rows whose `user_id` has no `user` row and
--          raises 23503 with the count and this key's name, instead of letting the
--          check fail with a bare `23503` and no instruction. It never deletes:
--          an orphan row is a fact someone must look at (the invite fixtures, a
--          pre-`user`-history row), and a migration that silently drops memberships
--          would be worse than one that stops.
--       3. `validate constraint` is the last statement, so the key is in force
--          (`convalidated`) when the file ends, and it fails loudly if the check and
--          the data still disagree.
--     Fixture impact, recorded here so the change is not read as self-contained: a
--     test fixture that writes `membership.user_id` with an id that has no `user`
--     row now fails with 23503 (`membership_user_fk`). Every such site is updated in
--     the same change as this file (the hand-over list of 02.3.1-T2), and the dev seed
--     (`DevSeed`) seeds its `user` row before its membership row.
--
--     `membership_user_idx` is new and not redundant with 0011's
--     `membership_org_user_key unique (org_id, user_id) where user_id is not null`:
--     that key leads with `org_id`, so the parent-side check PostgreSQL runs when a
--     `user` row is deleted or its key updated — `where user_id = $1`, no org — could
--     not use it and would scan every membership row. The index is plain, not
--     partial: a foreign-key check is planned from a parameter whose non-nullness a
--     generic plan does not prove, and a partial predicate it cannot prove is a
--     predicate it cannot use.
--
--     No ON DELETE action, like every neighbour (0011's `membership_organization_fk`,
--     0041's `user_identity_user_fk` and `session_user_fk`): NO ACTION. Erasure is
--     11's retention job and decides its own order; a key that cascaded would delete
--     memberships of a removed account without an audit row.
--
--     The key's checks bypass row level security by design — "referential integrity
--     checks … always bypass row security to ensure that data integrity is
--     maintained" — which is what lets `evidgate_app` insert a membership (it holds
--     INSERT on `membership`, 0011:210) while holding no privilege at all on
--     `public."user"` (0041:214-224). The write path therefore needs no new grant,
--     and no policy is added to either table.
--
--   * `evidgate_resolve.session_user(cookie_hmac, pepper_version)` — the read-only
--     half of the session path: "which user does this cookie belong to?" 0042 stores
--     a cookie only as HMAC-SHA256(pepper_vN, value) and made `cookie_hmac` unique;
--     this function turns the presented hash into the user, and nothing else. It is
--     `stable` and writes nothing on purpose: sliding expiry (`last_seen_at`,
--     `expires_at`), rotation and revocation are 02.3.2-T1's writes on its own path,
--     and a read-only function can run inside a caller's read-only transaction and be
--     called as often as a request pipeline likes without an update per call. It
--     resolves only a session that is unrevoked, unexpired on BOTH clocks (idle and
--     absolute — a session past either is dead) and belongs to an `active` user, so
--     disabling an account ends its sessions' usefulness without a sweep.
--
--   * `evidgate_resolve.login_link_identity(...)` — the only writer of the link
--     (AC6, "explicit confirm only, never auto-merge"). The identity key is
--     `(provider, subject)` (0041:112-114) and one subject may only be on one user, so
--     attaching a second provider to an account is either an insert or a move, and
--     the move is the dangerous branch:
--       - the target user is locked and must exist and be `active` — otherwise
--         `unknown_user` / `disabled`, no exception, no row;
--       - the identity row (if any) is locked `for update`, which serialises two
--         concurrent confirms of the same `(provider, subject)`; the unique key is the
--         backstop when it is not there yet, so a doubled first confirm ends with ONE
--         identity row and the loser answers `already_linked`;
--       - already on the target → `already_linked`, nothing written;
--       - already on another user B → `moved` only when B is empty of both
--         memberships (any status) and live sessions (unrevoked and unexpired on both
--         clocks). Anything else is `conflict` and changes nothing: an identity that
--         still belongs to a real member or a live session is never stolen, which is
--         what makes "never auto-merge" (AC6, D3/T6) hold at the database, not only in
--         a handler.
--     It never rewrites `user.email`, never merges two users, and writes no other
--     row: `user_identity.email_at_provider` travels with the moved row (it is the
--     address the provider reported when that identity was linked), and
--     `p_display_name` is accepted for the signature the linking handler builds from
--     the profile but deliberately not written — a second provider's profile name must
--     not rename the account, whose name is the login upsert's (0042).
--
-- Why both definers are safe (TL T80, identical hygiene to 0014/0016/0042/0043):
-- owned by `evidgate_owner`, whose `*_owner_backfill` policies (0041:193-212,
-- 0011:191-203) let it read and write under FORCE row level security;
-- `set search_path = ''` with schema-qualified references only, so no object the
-- caller creates can shadow a table, type or operator; no dynamic SQL; the built-in
-- EXECUTE grant to PUBLIC is revoked per function and EXECUTE goes to `evidgate_app`
-- alone, which is the only role that runs a login callback.
--
-- Deliberately absent, each owned by another task:
--
--   * the invite-token transport, the link confirm cookie, the endpoints
--     `POST /v1/auth/link/{provider}`, `GET|DELETE /v1/auth/link/pending`,
--     `POST /v1/auth/link/confirm`, the canonical-address rule of D-S78-M1 and the
--     `allowed_email_domains` gate — the application half of this task. This file is
--     the storage and the two statements those handlers are allowed to rely on.
--   * the `auth.login` / `auth.link` / `membership.bind` audit rows — the application
--     writes them through the per-org tenant transaction of the task's audit writer
--     (`IIdentityAuditWriter`, contract C5), which is why the lint deferral below
--     names these two functions and their retrofit receiver `02.7.1-T2.2` — the task
--     that adds audit rows to the pre-tenant definers, the same receiver 0042:65 and
--     0043:84 declare (D-S75-Q4). A pre-tenant definer has no org to attribute a row
--     to and `audit_log` (0015) is org-scoped; writing one here would put a login into
--     a chain that does not own it.
--   * sliding expiry, rotation and the sweeps — 02.3.2-T1 (02.3.2 §4, §7).
--   * no new table, so `contracts/rls-exceptions.txt` is unchanged: `public.user`,
--     `public.user_identity` and `public.session` are already listed as `platform`,
--     and `membership` stays the ordinary tenant table 0011 made it.
--
-- Rollback (playbook `docs/migrations-expand-contract.md`, mechanical, never executed
-- in place): the file is additive, so the contract step drops what it added —
--   alter table public.membership drop constraint membership_user_fk;
--   drop index if exists public.membership_user_idx;
--   drop function if exists evidgate_resolve.login_link_identity(uuid, text, text, text, text, uuid);
--   drop function if exists evidgate_resolve.session_user(bytea, integer);
-- Dropping the key needs no data change: a NULL `user_id` was always legal and a
-- non-null one stays readable by value, exactly as 0011 left it.
--
-- lint: audit-deferred 02.7.1-T2.2 evidgate_resolve.session_user, evidgate_resolve.login_link_identity
-- The per-call audit stub of 02.6.1 AC6 is not written here, exactly like 0014/0042/0043: these
-- two functions run before any tenant scope exists, `audit_log` (0015) is org-scoped, and the
-- application's `IIdentityAuditWriter` writes the `auth.login` / `auth.link` rows inside the
-- per-org tenant transaction the login already opened (contract C5).

-- The parent-side index first, so the key's checks use it from the moment the key exists.
create index if not exists membership_user_idx
  on public.membership (user_id);

-- `drop ... if exists` is deliberately not the idempotency mechanism here: 0031's existence
-- check keeps a re-run from re-adding (and re-locking) a key that is already in place.
do $fk$
begin
  if not exists (
    select 1
      from pg_catalog.pg_constraint
     where conrelid = 'public.membership'::regclass
       and conname = 'membership_user_fk')
  then
    alter table public.membership
      add constraint membership_user_fk foreign key (user_id) references public."user" (id) not valid;
  end if;
end
$fk$;

do $orphans$
declare
  orphan_count bigint;
begin
  select count(*) into orphan_count
    from public.membership m
   where m.user_id is not null
     and not exists (select 1 from public."user" u where u.id = m.user_id);

  if orphan_count > 0 then
    raise exception
      'membership_user_fk: % membership row(s) have a user_id with no user row; repair or delete them before this migration can validate the key (a membership written before the user existed is the invite path of 0011, and the bind now writes an id the login upsert created)',
      orphan_count
      using errcode = '23503';
  end if;
end
$orphans$;

-- The scan itself takes SHARE UPDATE EXCLUSIVE, which blocks neither reads nor writes (the
-- add above already holds its ACCESS EXCLUSIVE until this file's transaction commits).
alter table public.membership validate constraint membership_user_fk;

-- Which user a presented session cookie belongs to (02.3.1 §4, AC1/AC3). The lookup is the
-- unique index `session_cookie_hmac_key` of 0042; everything after it is a filter, so a bogus
-- or stale hash costs one index probe. `stable`, no write, no side effect: sliding expiry is
-- 02.3.2-T1's. NULL arguments match nothing (a NULL comparison is never true), which is the
-- fail-closed answer for a request that carried no usable cookie.
create or replace function evidgate_resolve.session_user(
  p_cookie_hmac bytea,
  p_pepper_version integer)
returns table (user_id uuid)
language sql
stable
security definer
set search_path = ''
as $$
  select s.user_id
    from public.session s
    join public."user" u on u.id = s.user_id
   where s.cookie_hmac = p_cookie_hmac
     and s.pepper_version = p_pepper_version
     and s.revoked_at is null
     and s.expires_at > now()
     and s.absolute_expires_at > now()
     and u.status = 'active'
$$;

-- The one writer of a provider link (AC6). Status contract, always a row, never an exception for a
-- business outcome: `linked` (identity inserted), `already_linked` (already on this user),
-- `moved` (taken from an empty user, returned in moved_from_user_id), `conflict` (the other user
-- still holds a membership or a live session), `unknown_user`, `disabled`.
create or replace function evidgate_resolve.login_link_identity(
  p_user_id uuid,
  p_provider text,
  p_subject text,
  p_email text,
  p_display_name text,
  p_identity_id uuid)
returns table (outcome text, moved_from_user_id uuid)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_target_status text;
  v_owner uuid;
begin
  -- The target account first, `for no key update` rather than `for update`: the move below writes
  -- `user_identity.user_id`, whose foreign-key check takes FOR KEY SHARE on the referenced `user` row,
  -- and FOR UPDATE would wait on it — with two crossed link attempts that is a deadlock (the
  -- reasoning D-S78-Q5 records for the same choice on the organization row). The lock still serialises
  -- against a concurrent disable or key change of the account.
  --
  -- Assignment (`:= (select …)`) rather than `select … into`, like 0042's two writers: the migration's own
  -- lint (scripts/check-rls.py) reads every `into`-led relation as a table reference.
  v_target_status := (select u.status
                        from public."user" u
                       where u.id = p_user_id
                         for no key update);

  -- `user.status` is NOT NULL (0041), so a null here means the account does not exist.
  if v_target_status is null then
    return query select 'unknown_user'::text, null::uuid;
    return;
  end if;

  if v_target_status <> 'active' then
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
    values (p_identity_id, p_user_id, p_provider, p_subject, p_email)
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

  if v_owner = p_user_id then
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
       and s.expires_at > now()
       and s.absolute_expires_at > now())
  then
    return query select 'conflict'::text, null::uuid;
    return;
  end if;

  update public.user_identity ui
     set user_id = p_user_id
   where ui.provider = p_provider
     and ui.subject = p_subject;

  return query select 'moved'::text, v_owner;
end
$$;

revoke all on function evidgate_resolve.session_user(bytea, integer) from public;
revoke all on function evidgate_resolve.login_link_identity(uuid, text, text, text, text, uuid) from public;
grant execute on function evidgate_resolve.session_user(bytea, integer) to evidgate_app;
grant execute on function evidgate_resolve.login_link_identity(uuid, text, text, text, text, uuid) to evidgate_app;
