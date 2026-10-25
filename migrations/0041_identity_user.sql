-- 0041_identity_user.sql
--
-- Owner: 02.3.1-T1.1. `user`, `user_identity` and `session` (area plan 02 §4.1,
-- feature plan 02.3.1 §4): the federated-login identity of a human — the
-- provider-verified account, its `(provider, subject)` binding, and the
-- server-side credential row a completed login inserts.
--
-- Platform scope, not tenant scope (D12, area plan 02 §4.5, CR-1; the `platform`
-- kind of contracts/rls-exceptions.txt): none of the three tables carries an `org_id`. They
-- hold no customer business data, a user may be a member of several orgs, and the
-- callback runs before any tenant scope exists (02.3.1 §4.3), so an org-scoped
-- policy would have nothing to compare a row against. Access is therefore never a
-- direct read: `evidgate_app` holds no privilege on any of the three (0001's
-- `alter default privileges` hands every new `public` table's DML to it, so the
-- `revoke all` statements below are load-bearing, not belt-and-braces), and the
-- only paths are the owner-owned SECURITY DEFINER functions of `evidgate_resolve`
-- — `member_orgs` (02.6.1-T3) and the login/session functions that 02.3.1-T1.4
-- and 02.3.2 add. ENABLE + FORCE row level security with one permissive
-- `*_owner_backfill` policy per table is still required: DECISIONS F5 rejects
-- BYPASSRLS, `force` applies to the owner too (so an expand/contract backfill
-- would otherwise change zero rows without failing), and the boot self-check of
-- 02.6.1-T1.4 plus 02.9.1-T3 refuse a table that lacks the flags or a policy.
-- No `TO evidgate_app` policy exists on purpose: a policy the app role cannot
-- reach is not a hole, and the privilege revoke is what keeps it unreachable.
--
-- Deliberately absent, each owned by another task:
--
--   * `user.preferred_locale` and `user.version` plus the shared
--     `set_updated_at_and_version` trigger (0004) — 02.3.1-T3's expand-only
--     migration and its `GET`/`PATCH /v1/me` contract (00 §7). `updated_at` is
--     created here because the login path stamps it from the first write; only
--     the trigger that maintains it is T3's.
--   * `session.cookie_hmac`, `session.pepper_version` and their two indexes
--     (unique, and the partial `where revoked_at is null` lookup) — 02.3.2-T1's
--     `NNNN_identity_session_hardening.sql` (02.3.2 §4). The table itself ships
--     here because AC1 needs an INSERT at login (02.3.1 §3); 02.3.2-T1 owns the
--     lifecycle — rotation, idle and absolute expiry, sweeps — and adds columns,
--     it never re-creates the table.
--   * `session.rotated_from -> session (id)` is the plain self-FK the model names,
--     with no ON DELETE action. 02.3.2-T1's `session-cleanup` hard-deletes rows
--     expired by more than 30 days (02.3.2 §7), and a live successor references
--     its revoked predecessor, so that sweep must delete a chain child-first —
--     or 02.3.2-T1 relaxes this key to `on delete set null` in its own
--     expand-only file. Recorded so the sweep is not written as one blanket
--     delete and aborts with 23503 halfway through.
--   * No foreign key from `membership.user_id` to `user (id)`. A nullable column
--     may carry a foreign key — NULL passes it — so the blocker is data, not
--     nullability: 0011 left the reference by value, the invite path writes a
--     `user_id` before any `user` row exists for it (02.1.2-T2 binds on a
--     verified-email match), and the existing membership fixtures insert
--     arbitrary user ids that no `user` row holds. Adding the key therefore needs
--     a backfill of those rows plus the fixture updates, which is 02.3.1-T2 /
--     02.3.1-T1.4's change with its own migration, not this file's.
--
-- No password, hash, salt or verifier column exists anywhere (AC7, FR-02-3): V1
-- has no password credential, so there is nothing to store, hashed or otherwise.
-- `email` is `text`, not `citext` — the extension is not installed in this
-- database (0011:43) and normalisation is the command layer's job (02.1.2 §5) —
-- which is also why the index below is a plain index on `lower(email)`: email is
-- never an identity key (D3/T6), `(provider, subject)` is, and two users may
-- legitimately share an address across providers (AC6).
--
-- Two column decisions the feature plan leaves open (02.3.1 §4), recorded so the
-- diff reads as deliberate: `email` is NOT NULL, because a `user` row is only ever
-- created from a provider-verified address (FR-02-3/FR-02-4, AC4) and every later
-- read — `GET /v1/me`, the invite bind of 02.1.2-T2 — expects one; and
-- `email_at_provider` is `text`, not the plan's `citext`, for the same reason
-- `user.email` is: the extension is not installed (0011:43), so the column states
-- its own 320-character bound and the command layer normalises.

create table if not exists public."user" (
  -- App-generated UUIDv7 (00 §7): no default, the command layer creates the id.
  id uuid not null,
  email text not null,
  -- The provider profile name, and only that (02 §6: never a second copy of
  -- provider profile data). Nullable — neither provider guarantees one.
  display_name text,
  status text not null default 'active',
  last_login_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint user_pkey primary key (id),
  -- Input bounds of this contract (02.3.1 §4, T1.1), mirroring the invite address
  -- of 0011:81: an address is capped at 320 characters and a display name at 200.
  constraint user_email_length_check check (char_length(email) between 3 and 320),
  constraint user_display_name_length_check check (
    display_name is null or char_length(display_name) between 1 and 200),
  -- 02.3.1 §6: `active -> disabled` is the only transition (admin path, later
  -- stories). There is no `deleted` state — erasure is 11's retention job.
  constraint user_status_check check (status in ('active', 'disabled'))
);

-- D3/T6: a plain, NON-unique index on the normalised address. It serves the
-- "does a human with this address already exist?" lookup of a login and the
-- invite bind (02.1.2-T2); it must not be unique, because AC6 records the same
-- address at two providers as two identities on one user only after an explicit
-- confirm, never by a merge on email.
create index if not exists user_email_lower_idx
  on public."user" (lower(email));

create table if not exists public.user_identity (
  id uuid not null,
  user_id uuid not null,
  provider text not null,
  -- GitHub's numeric user id or Google's `sub` (T6); never the email.
  subject text not null,
  -- The address the provider reported at link time, for support and audit. Not
  -- an identity key and never a lookup key on its own.
  email_at_provider text,
  linked_at timestamptz not null default now(),
  constraint user_identity_pkey primary key (id),
  -- AC6/FR-02-4: the binding is `(provider, subject)`; a second login at the same
  -- provider and subject is the same human, and nothing else is.
  constraint user_identity_provider_subject_key unique (provider, subject),
  constraint user_identity_user_fk foreign key (user_id) references public."user" (id),
  -- `saml` is added by 02.3.3; V1 identity is these two providers only.
  constraint user_identity_provider_check check (provider in ('github', 'google')),
  constraint user_identity_subject_length_check check (char_length(subject) between 1 and 255),
  constraint user_identity_email_at_provider_length_check check (
    email_at_provider is null or char_length(email_at_provider) <= 320)
);

-- One user's identities, and the count a "this account is linked to N providers"
-- read needs; the unique key above is (provider, subject)-leading, so it cannot
-- serve it.
create index if not exists user_identity_user_idx
  on public.user_identity (user_id);

create table if not exists public.session (
  id uuid not null,
  user_id uuid not null,
  created_at timestamptz not null default now(),
  last_seen_at timestamptz not null default now(),
  -- Idle deadline (sliding, 02.3.2) and the hard deadline that no activity
  -- extends; the cookie itself is 02.3.2's and is not stored here yet.
  expires_at timestamptz not null,
  absolute_expires_at timestamptz not null,
  ip inet,
  user_agent text,
  revoked_at timestamptz,
  -- The predecessor this row was rotated from (02.3.2-T1); a login rotation
  -- writes it, and the predecessor is revoked in the same transaction.
  rotated_from uuid,
  constraint session_pkey primary key (id),
  constraint session_user_fk foreign key (user_id) references public."user" (id),
  constraint session_rotated_from_fk foreign key (rotated_from) references public.session (id),
  -- A session can never outlive its own absolute deadline: the two expiries are
  -- written together, and a swapped pair would make the idle check the stricter
  -- of the two for no reason (the state machine is 00 §8.9; the idle/absolute
  -- pair is 02.3.2 §1/§4).
  constraint session_expiry_check check (expires_at <= absolute_expires_at),
  -- The user agent is bounded at 512 characters (this contract): the value is
  -- client input, so the column is the last line of defence against an unbounded
  -- write.
  constraint session_user_agent_length_check check (
    user_agent is null or char_length(user_agent) <= 512)
);

-- 02.3.2 §4: the session list of one user, and the "is this session still live?"
-- lookup behind every cookie-authenticated request — both filter a user and
-- exclude revoked rows before comparing expiry.
create index if not exists session_user_revoked_expires_idx
  on public.session (user_id, revoked_at, expires_at);

alter table public."user" enable row level security;
alter table public."user" force row level security;
alter table public.user_identity enable row level security;
alter table public.user_identity force row level security;
alter table public.session enable row level security;
alter table public.session force row level security;

-- The migration role's own policy on every table, the 0004/0011 form: `force row
-- level security` binds the table owner too, so without it a later
-- expand/contract backfill would change zero rows and report success. Explicit
-- and permissive on purpose — the alternative would be BYPASSRLS (DECISIONS F5).
drop policy if exists user_owner_backfill on public."user";
create policy user_owner_backfill on public."user"
  for all
  to evidgate_owner
  using (true)
  with check (true);

drop policy if exists user_identity_owner_backfill on public.user_identity;
create policy user_identity_owner_backfill on public.user_identity
  for all
  to evidgate_owner
  using (true)
  with check (true);

drop policy if exists session_owner_backfill on public.session;
create policy session_owner_backfill on public.session
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- 00 §5.4.3 least privilege / 02.9.1 §8 T1: the runtime roles hold NOTHING on the
-- platform identity tables — no SELECT, no write, no TRUNCATE/REFERENCES/TRIGGER.
-- A direct read is the hole the resolver path exists to close, and a write
-- privilege would let the app role mutate an identity row outside the login
-- command and the membership path. The `revoke all` is required in addition to
-- granting nothing: 0001's `alter default privileges` grants `evidgate_app` DML
-- and `evidgate_operator` SELECT on every table `evidgate_owner` creates in
-- `public`, and a `grant` cannot take a privilege away.
revoke all on public."user" from evidgate_app, evidgate_operator, evidgate_worker_sched;
revoke all on public.user_identity from evidgate_app, evidgate_operator, evidgate_worker_sched;
revoke all on public.session from evidgate_app, evidgate_operator, evidgate_worker_sched;
