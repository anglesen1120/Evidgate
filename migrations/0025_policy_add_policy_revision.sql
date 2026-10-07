-- 0025_policy_add_policy_revision.sql
--
-- Owner: 07.1.1-T1.1. `policy_revision`: every version of a project's release policy that was ever written,
-- stored append-only, identified by (project_id, revision_no) and by its content address `hash` (feature plan
-- 07.1.1 §4, 00 §7/§8.6, DECISIONS A3/A9, rulings T3/T25). The same canonical body in the same project is the
-- same row; any change is a new revision. A revision is never updated or deleted, so an evaluation (07.2.2) and
-- a run pin (05.2.1) always read a reproducible input.
--
-- Immutability is defence in depth: the app role holds SELECT, INSERT only (no UPDATE/DELETE, C6), and the
-- trigger below refuses DELETE and any UPDATE that touches a column other than `state`/`version`. `state` moves
-- only draft -> active -> retired (07.1.1-T4, §6); `body` and `hash` never change, so a stored revision stays
-- byte-identical to the hash it was written under. The database owns the `version` increment (00 §7): a state
-- transition sets `version = old.version + 1`, overwriting whatever the caller sent, so the optimistic-concurrency
-- token advances with the lifecycle and a version-only UPDATE is refused.
--
-- `created_by` is the member id that wrote the revision; it carries no foreign key (a member row can be removed
-- while the revision stays, and the id is an audit fact) — recorded open item on the feature plan.
--
-- No extra `(org_id, project_id, revision_no desc)` index: the unique btree of `policy_revision_revision_no_key`
-- already orders (org_id, project_id, revision_no), and a backward scan of it serves the newest-first list of
-- `GET .../revisions` (P4).

create table if not exists public.policy_revision (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  revision_no integer not null,
  hash text not null,
  body jsonb not null,
  state text not null default 'draft',
  created_by uuid not null,
  created_at timestamptz not null default now(),
  version integer not null default 1,
  constraint policy_revision_pkey primary key (id),
  constraint policy_revision_org_id_key unique (org_id, id),
  constraint policy_revision_revision_no_key unique (org_id, project_id, revision_no),
  constraint policy_revision_hash_key unique (org_id, project_id, hash),
  constraint policy_revision_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint policy_revision_hash_check check (hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint policy_revision_state_check check (state in ('draft', 'active', 'retired')),
  constraint policy_revision_revision_no_check check (revision_no >= 1),
  constraint policy_revision_version_check check (version >= 1),
  constraint policy_revision_body_check check (jsonb_typeof(body) = 'object')
);

-- Append-only: the trigger refuses DELETE and any UPDATE beyond the §6 lifecycle, whoever issues it; the grants
-- say the same to the app role. `state` may move only draft -> active -> retired, and the database owns the
-- `version` increment (00 §7): a transition sets `version = old.version + 1`, overwriting whatever the caller
-- sent, while a same-state UPDATE that changes `version` is refused.
create or replace function evidgate.policy_revision_immutable() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'revision_immutable: a policy revision is append-only (DELETE)'
        using errcode = '23514';
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.revision_no is distinct from old.revision_no
       or new.hash is distinct from old.hash
       or new.body is distinct from old.body
       or new.created_by is distinct from old.created_by
       or new.created_at is distinct from old.created_at then
      raise exception 'revision_immutable: a policy revision body, hash and identity never change (UPDATE)'
        using errcode = '23514';
    end if;

    if new.state is distinct from old.state then
      if not ((old.state = 'draft' and new.state = 'active')
              or (old.state = 'active' and new.state = 'retired')) then
        raise exception 'revision_state_invalid: a policy revision moves only draft -> active -> retired (% -> %)',
          old.state, new.state
          using errcode = '23514';
      end if;

      -- The database owns the increment (00 §7): a transition bumps `version`, whatever the caller sent.
      new.version := old.version + 1;
    elsif new.version is distinct from old.version then
      raise exception 'revision_immutable: version moves only with a state transition (UPDATE)'
        using errcode = '23514';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.policy_revision_immutable() from public;

drop trigger if exists policy_revision_immutable on public.policy_revision;
create trigger policy_revision_immutable
  before update or delete on public.policy_revision
  for each row execute function evidgate.policy_revision_immutable();

alter table public.policy_revision enable row level security;
alter table public.policy_revision force row level security;

drop policy if exists policy_revision_app_tenant on public.policy_revision;
create policy policy_revision_app_tenant on public.policy_revision
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists policy_revision_owner_backfill on public.policy_revision;
create policy policy_revision_owner_backfill on public.policy_revision
  for all to evidgate_owner using (true) with check (true);

grant select, insert on public.policy_revision to evidgate_app;
revoke update, delete on public.policy_revision from evidgate_app;
grant select on public.policy_revision to evidgate_operator;
revoke all on public.policy_revision from evidgate_worker_sched;
