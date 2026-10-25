-- 0043_identity_invite_flow.sql
--
-- Owner: 02.1.2-T2 (W1). The schema half of the invite flow (feature plan 02.1.2
-- §4:70, §6, §7, §8 T5/T75). 0011 created `membership` with the invite columns
-- `invite_secret_prefix`, `invite_token_hash` and `invite_expires_at` but left them
-- to this task (0011:50-54); this file completes them and adds the three functions
-- the invite path calls before any tenant exists. What it adds and why:
--
--   * `membership.invite_pepper_version` — T5 keeps an invite secret only as
--     HMAC-SHA256(pepper_vN, secret), and the column records which pepper made the hash:
--     one pepper is configured and `InviteSecrets.Verify` accepts that version only, so a
--     bump fails every pending invite closed and an admin resend re-mints them. No
--     default: a minted token always names its pepper; a row without a token has none.
--   * `membership_invite_token_all_or_none` — a half-minted invite is not a state any
--     reader handles: `invite_by_secret` would return a row whose hash can never
--     verify. The constraint makes the three columns move together, requires the
--     expiry the resolver filters on, and pins the digest length to 32 bytes (the
--     same "the digest length is what makes `stored only as a hash` meaningful" rule
--     as 0042:75-83). Invite rows without a token stay legal: NULL in all three
--     columns, which is the 0011 shape of a pending invite T1 created. The `add
--     constraint` validates the existing rows in this file's transaction, and it cannot
--     fail on them: 0011 left all three columns unset (0011:50-54), so every row is
--     either the "none" shape or was written by this task's mint, which satisfies the
--     rule by construction.
--   * `organization.invite_autobind_verified_domain` — the per-org opt-in of AC8
--     (02.1.2 §4:70, §8 T75): email-only auto-bind happens only for an org that opted
--     in. It is a boolean, not a domain registry: the domain comes from the provider's
--     verified claim, and a registry is out of this story's scope.
--   * `evidgate_resolve.invite_by_secret(prefix)` and
--     `evidgate_resolve.invite_autobind_candidates(email)` — the two pre-tenant
--     lookups of T75. `evidgate_app`'s policies show it exactly one org's memberships,
--     and an invite arrives before that org is known, so the lookup crosses the tenant
--     boundary through the sanctioned `evidgate_resolve` schema (0002:54-62, 00 §5.4.8).
--     Both return only the T1 tuple `(org_id, membership_id)` and nothing else.
--   * `evidgate.expire_pending_invites(limit)` — the sweep of 02.1.2 §7 behind the
--     `invite-expiry` task: a bounded bulk `pending -> expired` transition run by the
--     sweep role (TL T80), the same shape as `evidgate.purge_expired_ci_token_issue`
--     (0017:66-89) and `evidgate.purge_expired_idempotency` (0007:137).
--
-- The `pending -> expired|revoked|active` transitions need no schema change: 0011's
-- `membership_status_check` already admits all five states and 0011 declares no
-- transition trigger, so the command layer and this sweep may write them directly
-- (checked against 0011:73-75; nothing here adds a rule that could contradict it).
--
-- Why the definers are safe (TL T80, identical hygiene to 0014/0017/0042): every
-- function is owned by `evidgate_owner`, whose `*_owner_backfill` policies (0004,
-- 0011:191-203) let that role read and write under FORCE row level security;
-- `set search_path = ''` plus schema-qualified references means an object the caller
-- creates cannot shadow a table or operator; no dynamic SQL; PUBLIC holds no EXECUTE
-- (the built-in grant is revoked globally in 0001, restated per function below).
-- `evidgate_app` gets EXECUTE on the two resolvers and on nothing else here; the
-- sweep is granted to `evidgate_worker_sched` only, and the app role is refused by
-- the database itself, not by convention.
--
-- Two partial indexes carry the new predicates, because both paths run on a schedule:
-- the sweep filters `status = 'pending' and invite_expires_at < now()` and the
-- auto-bind lookup filters `status = 'pending' and invited_email = $1`. Indexing only
-- pending rows keeps both tiny (the partial-unique `membership_pending_email_key` of
-- 0011 leads with `org_id`, so it cannot serve a lookup by address alone).
--
-- The org's status is deliberately not part of `invite_by_secret`'s predicate: the
-- resolver answers "which membership owns this prefix", one row. A suspended or deleted
-- org is a tenant-path decision: after the scope opens, the bind re-reads the membership
-- under RLS and reads the org's status under the same lock, refusing a non-active one.
-- `invite_autobind_candidates` filters the org instead (AC8's opt-in needs `active`).
--
-- Deliberately absent, each owned by another task:
--
--   * token minting, the pepper value and the notification outbox row — 02.1.2-T2's
--     `InviteMember`/`ResendInvite`/`CancelInvite` commands and `InviteSecrets`
--     (`SecretNames.InvitePepper`, `infra/config-matrix.json`). This file only stores
--     what they write.
--   * the `invite-expiry` Cronos schedule and its task handler — 02.1.2-T2.1's
--     `InviteExpiryTask` (02.1.2 §7); it calls `evidgate.expire_pending_invites`.
--   * the bind handler and its call from login — 02.1.2-T2.2's `MembershipBinder`,
--     wired into the login transaction by 02.3.1-T2. Nothing in `Login/**` calls the
--     resolvers of this file yet.
--   * no new table, so `contracts/rls-exceptions.txt` is unchanged.
--   * no foreign key from `membership.user_id` to `user (id)`: 0041:46-53 records
--     why (the invite path writes a `user_id` before any `user` row exists, and the
--     existing membership fixtures insert arbitrary user ids), and 02.1.2 §3a/§4
--     keep the reference by value. D-S78-Q4 keeps it absent here.
--
-- lint: audit-deferred 02.7.1-T2.2 evidgate_resolve.invite_by_secret, evidgate_resolve.invite_autobind_candidates
-- The per-call audit stub of 02.6.1 AC6 is not written here, exactly like 0014: `audit_log`
-- (0015) is tenant-scoped and its writer is 02.7.1-T2.2's retrofit of every resolver — an
-- invite lookup has no org to attribute an audit row to until the scope opens.

alter table public.membership
  add column if not exists invite_pepper_version integer;

-- `drop ... if exists` before `add` is what makes this file re-runnable: PostgreSQL has
-- no `add constraint if not exists`.
alter table public.membership
  drop constraint if exists membership_invite_pepper_version_check,
  drop constraint if exists membership_invite_token_all_or_none;

alter table public.membership
  -- A pepper version is a lookup key into the configured peppers (0042:83 shape).
  add constraint membership_invite_pepper_version_check
    check (invite_pepper_version is null or invite_pepper_version >= 1),
  -- All three token columns or none, plus the expiry the resolver filters on and a hash
  -- that is really HMAC-SHA256. A row with no token stays the legal pending row of 0011.
  add constraint membership_invite_token_all_or_none check (
    (invite_secret_prefix is null
      and invite_token_hash is null
      and invite_pepper_version is null)
    or (invite_secret_prefix is not null
      and invite_token_hash is not null
      and invite_pepper_version is not null
      and invite_expires_at is not null
      and octet_length(invite_token_hash) = 32));

alter table public.organization
  add column if not exists invite_autobind_verified_domain boolean not null default false;

-- The sweep's predicate: only pending rows, ordered by the column it ranges over.
create index if not exists membership_pending_expiry_idx
  on public.membership (invite_expires_at)
  where status = 'pending';

-- The auto-bind lookup's predicate: one address across every opted-in org.
create index if not exists membership_pending_invited_email_idx
  on public.membership (invited_email)
  where status = 'pending';

-- Which membership owns an invite prefix (T75). Runs before any tenant scope exists, so
-- it is the only way the login path can learn the org the invite belongs to; the caller
-- then opens that org's scope and re-reads the row under RLS. The prefix is the public
-- lookup key of the link (the secret half is never stored), and 0011's
-- `membership_invite_secret_prefix_key` makes it resolve to at most one row.
create or replace function evidgate_resolve.invite_by_secret(p_secret_prefix text)
returns table (org_id uuid, membership_id uuid)
language sql
stable
security definer
set search_path = ''
as $$
  select m.org_id, m.id
    from public.membership m
   where m.invite_secret_prefix = p_secret_prefix
     and m.status = 'pending'
     and m.invite_expires_at > now()
$$;

-- The candidates of AC8's opt-in path: the pending invites addressed to a
-- provider-verified email, in orgs that opted into email-only auto-bind and are still
-- active. `p_email` must already be normalised by the caller (lower/trim, 02.1.2 §5);
-- this function compares exactly, so an unnormalised argument matches nothing instead
-- of half-matching. Ordered by org, capped at 50 rows: deterministic and bounded.
create or replace function evidgate_resolve.invite_autobind_candidates(p_email text)
returns table (org_id uuid, membership_id uuid)
language sql
stable
security definer
set search_path = ''
as $$
  select m.org_id, m.id
    from public.membership m
    join public.organization o on o.id = m.org_id
   where m.invited_email = p_email
     and m.status = 'pending'
     and m.invite_expires_at > now()
     and o.invite_autobind_verified_domain
     and o.status = 'active'
   order by m.org_id
   limit 50
$$;

-- The `invite-expiry` sweep of 02.1.2 §7: bounded batches of past-due pending invites
-- become `expired`. The bound is enforced here, not only at the caller, so a large
-- backlog drains in short transactions and the task can loop until a short batch comes
-- back. A NULL or non-positive limit sweeps nothing (fail-closed: an absent batch size
-- must not turn into an unbounded update). Rows whose `invite_expires_at` is NULL are
-- never swept — a pending row without a minted token never expires, and the comparison
-- range is sargable so `membership_pending_expiry_idx` serves it.
create or replace function evidgate.expire_pending_invites(p_limit integer) returns integer
  language sql
  security definer
  set search_path = ''
  as $$
    with batch as (
      select m.id
        from public.membership m
       where m.status = 'pending'
         and m.invite_expires_at < now()
       order by m.invite_expires_at
       limit least(greatest(coalesce(p_limit, 0), 0), 10000)
         for update skip locked
    ), expired as (
      -- `status = 'pending'` is re-checked here, not only in the batch: this function
      -- touches a state machine, not an append-only cache. Under READ COMMITTED a row a
      -- concurrent bind committed as `active` after this statement's snapshot must not be
      -- dragged back to `expired`, and EvalPlanQual re-evaluates the UPDATE's own
      -- condition (not the CTE's), so the guard belongs on the UPDATE.
      update public.membership m
         set status = 'expired'
        from batch b
       where m.id = b.id
         and m.status = 'pending'
      returning m.id
    )
    select count(*)::integer from expired
  $$;

revoke all on function evidgate_resolve.invite_by_secret(text) from public;
revoke all on function evidgate_resolve.invite_autobind_candidates(text) from public;
revoke all on function evidgate.expire_pending_invites(integer) from public;
grant execute on function evidgate_resolve.invite_by_secret(text) to evidgate_app;
grant execute on function evidgate_resolve.invite_autobind_candidates(text) to evidgate_app;
grant execute on function evidgate.expire_pending_invites(integer) to evidgate_worker_sched;
