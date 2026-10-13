-- 0037_run_identity.sql
--
-- Owner: 05.2.2-T1.2. `run`'s branch / pull-request / commit identity (feature plan 05.2.2 §4, AC4
-- and AC5): eight write-once columns the create handler fills from verified GitHub OIDC claims (AC1)
-- or from CLI-supplied values labelled `unverified_ref` (AC2/AC3), and that nothing may change
-- afterwards (AC5). Nothing else moves on `run`: no table privilege, no policy, no RLS statement and
-- no index; the one grant added is EXECUTE on the new trigger function, which the app role's own
-- `update … set state` fires on every write. The migration's one other object is the Identity-owned
-- `ci_token_issue` index the verified-caller check reads (D-S45-Q2, below); it adds no column,
-- privilege, policy or RLS statement to that table.
--
--   * `ref_kind` text          — the ref's kind: branch | tag | pull_request;
--   * `ref_name` text          — the ref name as stored (`main`, `v1.2.3`, `42/merge` for pull
--                                request 42), 1..255 chars;
--   * `pr_number` integer      — the pull-request number, required when `ref_kind = 'pull_request'`;
--   * `head_sha` / `base_sha`  — the commit the run was created for / the merge base: 40 hex chars,
--                                or 64 on a SHA-256 repository (the GitHub `sha` claim);
--   * `trigger_event` text     — push | pull_request | schedule | manual | dispatch (the closed set the
--                                create path labels a run with);
--   * `is_fork` boolean        — the ref comes from a fork, so the run is advisory-only (AC7);
--   * `unverified_ref` boolean — the values came from the caller, not from verified claims (AC2/AC3).
-- Existing rows: the six nullable columns stay NULL and the two flags default false, which is the
-- pre-05.2.2 reading of every run (plan §4, "existing rows").
--
-- Index (D-S45-Q2, coordinator, 2026-10-13). The verified-caller check reads the `ci_token_issue` row
-- of the GitHub run attempt the minted token carries, not of its own `jti`: 02.4.1 stores the GitHub
-- OIDC token's `jti` in `ci_token_issue.jti` while the `ci_producer` token Evidgate mints has a fresh
-- `jti` (Identity/Application/Commands/ExchangeCommands.cs), so a `where jti = <credential id>` read
-- never matches in production. The lookup is therefore
-- `select ref, sha from public.ci_token_issue where org_id, project_id, repository_id, run_id and
-- run_attempt match order by created_at desc limit 1` (`ref` and `sha` are fixed per GitHub run
-- attempt, so the newest row of the attempt is authoritative; `limit 1` also avoids a throw when one
-- attempt exchanged several tokens). `ci_token_issue` is Identity-owned (0017); this migration adds
-- only the covering index `ci_token_issue_run_idx (project_id, repository_id, run_id, run_attempt,
-- created_at desc)` below, and no column, privilege, policy or RLS statement of that table changes.
--
-- Bound CHECKs (the 0006-0036 `char_length`/regex style), each mirroring the plan §4 DDL and the source
-- it copies:
--   * `run_ref_kind_check` / `run_trigger_event_check` — the two closed vocabularies above. The plan §4
--     snippet adds them as inline column CHECKs; PostgreSQL would name such a constraint
--     `run_ref_kind_check` anyway, so they are written explicitly and the name is stable for the tests
--     and for 05.2.2-T2's API validation contract (422 `request_invalid`).
--   * `run_ref_name_check` — `1..255`: the plan writes `<= 255`, and the lower bound rejects the empty
--     string, which is not a ref name and would be indistinguishable from an absent `ref_name` filter
--     in AC6's optional query parameter.
--   * `run_pr_number_check` — `> 0`: GitHub numbers pull requests from 1; the plan's inline CHECK is
--     the same bound. NULL stays legal, so a branch run carries no number.
--   * `run_head_sha_check` / `run_base_sha_check` — `^[0-9a-f]{40}([0-9a-f]{24})?$`: 40 hex chars, or
--     64 on a SHA-256 repository (AC4). Lowercase only, and the anchors mean 41/63 chars, uppercase and
--     non-hex are refused.
--   * `run_pr_requires_number` — the cross-column rule of AC4: `ref_kind = 'pull_request'`
--     requires `pr_number`. This is the plan §4's own name and it is kept: the six column CHECKs above
--     take 0031's `run_<column>_check` form (they guard one column each), while this rule spans two and
--     the plan is the name of record for it.
--
-- Immutability (AC5): `forbid_run_identity_update()` refuses any UPDATE that changes one of the eight
-- columns — an 8-element row comparison under `is distinct from`, so NULL -> value and value -> NULL
-- both count as a change — and returns NEW untouched otherwise, so `state`, `updated_at`, `version`,
-- `current_evidence_revision_id` and every other column stay writable and 05.3.1's guarded
-- `update … set state = @state` is unaffected. The function raises with no errcode, so the SQLSTATE is
-- the plpgsql default P0001 that AC5 names (the 0020-0030 guards raise 23514 instead: they refuse a
-- state machine, this one refuses a write that does not exist as a concept, and AC5 pins P0001).
--   * Defense in depth, not the only barrier: 0031 revoked table-level UPDATE on `run` and granted
--     `select, insert` only, and the later column grants are 0034's `update (updated_at)` and 0036's
--     `update (state)` — the app role holds no privilege on any identity column, so its UPDATE cannot
--     name one (42501, before the trigger is reached). The trigger's real target is the owner role,
--     which holds UPDATE on everything and would otherwise be able to rewrite a run's identity.
--   * Trigger order: PostgreSQL fires BEFORE row triggers in name order, and `run_identity_immutable`
--     sorts before 0031's `run_set_updated_at_and_version`, so the identity comparison sees the row as
--     the caller wrote it and the shared trigger's `updated_at`/`version` assignment follows. The two do
--     not interact: `updated_at` and `version` are not identity columns.
--   * EXECUTE: `grant execute on function public.forbid_run_identity_update() to evidgate_app` below.
--     The function sits in `public` next to 0004's shared `set_updated_at_and_version` (0001 revokes the
--     built-in PUBLIC EXECUTE on every function evidgate_owner creates), and the app role is the one
--     that fires it on every `update … set state`. No grant to `evidgate_operator` or
--     `evidgate_worker_sched`: neither may update a run at all.
--
-- Deferred on purpose: the `(project_id, ref_name, created_at, id)` index the plan §4 shows as
-- `run_project_ref_idx` is NOT created here — 05.2.2-T2.2 owns it together with the AC6 list query it
-- serves (this task is the columns, the constraints and the trigger). No index on `run` itself either:
-- 0031's `run_project_created_idx` still serves the unfiltered list, and the new columns are written
-- once at create time. The one index below is on `ci_token_issue`, not `run` (D-S45-Q2).
--
-- Idempotent like 0013/0025/0027-0036: `add column if not exists`, `drop constraint if exists` before
-- the `add constraint` group (a bare `add constraint` is not idempotent — 0033/0035's form),
-- `create index if not exists`, `create or replace function` and `drop trigger if exists`, so a re-run
-- is a no-op.

alter table public.run
  add column if not exists ref_kind text,
  add column if not exists ref_name text,
  add column if not exists pr_number integer,
  add column if not exists head_sha text,
  add column if not exists base_sha text,
  add column if not exists trigger_event text,
  add column if not exists is_fork boolean not null default false,
  add column if not exists unverified_ref boolean not null default false;

-- P1: the drop list and the add list are one statement each, so PostgreSQL scans `run` once for the
-- whole group instead of once per constraint (a bare `add constraint` is still not idempotent, so the
-- drops come first). AC4's cross-column rule: a pull-request run names its number — NULL `ref_kind`
-- passes (the whole `ref_kind <> 'pull_request'` disjunct is NULL, and a CHECK accepts NULL); only an
-- explicit `'pull_request'` with a NULL `pr_number` is refused.
alter table public.run
  drop constraint if exists run_ref_kind_check,
  drop constraint if exists run_ref_name_check,
  drop constraint if exists run_pr_number_check,
  drop constraint if exists run_head_sha_check,
  drop constraint if exists run_base_sha_check,
  drop constraint if exists run_trigger_event_check,
  drop constraint if exists run_pr_requires_number;

alter table public.run
  add constraint run_ref_kind_check check (ref_kind is null or ref_kind in ('branch', 'tag', 'pull_request')),
  add constraint run_ref_name_check check (ref_name is null or char_length(ref_name) between 1 and 255),
  add constraint run_pr_number_check check (pr_number is null or pr_number > 0),
  add constraint run_head_sha_check check (head_sha is null or head_sha ~ '^[0-9a-f]{40}([0-9a-f]{24})?$'),
  add constraint run_base_sha_check check (base_sha is null or base_sha ~ '^[0-9a-f]{40}([0-9a-f]{24})?$'),
  add constraint run_trigger_event_check check (
    trigger_event is null or trigger_event in ('push', 'pull_request', 'schedule', 'manual', 'dispatch')),
  add constraint run_pr_requires_number check (ref_kind <> 'pull_request' or pr_number is not null);

-- D-S45-Q2: the verified-caller check reads the run attempt's `ci_token_issue` row (see the header),
-- not the credential `jti`. This is the only change 0037 makes to the Identity-owned table: no grant,
-- no policy, no RLS statement. `if not exists` keeps the re-run a no-op.
create index if not exists ci_token_issue_run_idx
  on public.ci_token_issue (project_id, repository_id, run_id, run_attempt, created_at desc);

-- AC5: the eight identity columns never change. The guard lives in `public` beside 0004's shared
-- version trigger; it raises without an errcode so the SQLSTATE is P0001 (AC5).
create or replace function public.forbid_run_identity_update() returns trigger
  language plpgsql
  as $$
  begin
    if (new.ref_kind, new.ref_name, new.pr_number, new.head_sha, new.base_sha,
        new.trigger_event, new.is_fork, new.unverified_ref)
       is distinct from
       (old.ref_kind, old.ref_name, old.pr_number, old.head_sha, old.base_sha,
        old.trigger_event, old.is_fork, old.unverified_ref) then
      raise exception 'run_identity_immutable: a run''s identity columns (ref_kind, ref_name, pr_number, head_sha, base_sha, trigger_event, is_fork, unverified_ref) never change; a re-run attempt creates a new run';
    end if;

    return new;
  end
  $$;

revoke all on function public.forbid_run_identity_update() from public;
grant execute on function public.forbid_run_identity_update() to evidgate_app;

drop trigger if exists run_identity_immutable on public.run;
create trigger run_identity_immutable
  before update on public.run
  for each row execute function public.forbid_run_identity_update();
