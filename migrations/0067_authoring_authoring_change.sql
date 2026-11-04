-- 0067_authoring_authoring_change.sql
--
-- WHY: a reviewer's comment, a delete, a merge or a split is a proposal, not a write. It waits in
-- a queue until someone applies the whole selection in one transaction, so nothing an editor is
-- looking at changes under them and an abandoned proposal leaves no trace. The queue row is the
-- evidence of what was proposed, by whom and why, and it must survive the apply it produced.
--
-- SHAPE: one mutable-by-lifecycle row per proposal. `payload` is the change itself (validated
-- against contracts/schemas/authoring-change.v1.json), `base_version` is the target's version when
-- the proposal was queued and `affected_versions` is `{item id: version}` for the target plus every
-- item the payload names — the set the apply locks and hashes. `target_id` is the item for
-- `comment`/`delete`/`split` and the survivor for `merge`. The composite foreign keys pin the
-- project and the target to the same tenant *and* the same project, so a cross-project proposal
-- fails at 23503 whatever a caller does.
--
-- STATE: `pending -> applied | discarded` only, and both terminal states are frozen. The
-- transition guard below also freezes every content and identity column for the row's whole life:
-- a queued proposal is never rewritten, so the apply's etag and the audit trail cannot disagree
-- about what was proposed.
--
-- GUARDS: `authoring_change_affected_guard` refuses a second pending structural change that covers
-- any item of a new one. An `exists` alone cannot see another transaction's uncommitted row under
-- READ COMMITTED, so the check is serialised per project by a transaction-scoped advisory lock;
-- two overlapping structural proposals therefore cannot both commit, and apply never has to
-- resolve an ambiguity between two of them.
--
-- RETENTION: `discarded` rows are purged after 180 days by `evidgate.purge_expired_authoring_changes`
-- (owner `SECURITY DEFINER`, EXECUTE only to `evidgate_worker_sched`). Applied rows are review
-- evidence of the versions they carry and are never purged.
--
-- Idempotent in the shape the migration contract allows: `create table if not exists`,
-- `create ... if not exists`, `drop ... if exists`, `create or replace`.

set local lock_timeout = '5s';

-- A CHECK is evaluated per inserted row and cannot carry a subquery or a set-returning call, so the
-- 1..50-entry bound needs one immutable counter. It stays a plain invoker function: it reads
-- only its argument, and the app role (which evaluates the CHECK) needs EXECUTE on it.
create or replace function evidgate.jsonb_key_count(p_value jsonb) returns integer
  language sql
  immutable
  as $$
    select count(*)::integer from jsonb_object_keys(p_value)
  $$;

revoke all on function evidgate.jsonb_key_count(jsonb) from public;
grant execute on function evidgate.jsonb_key_count(jsonb) to evidgate_app;

create table if not exists public.authoring_change (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  target_kind text not null default 'checklist_item',
  target_id uuid not null,
  change_type text not null,
  payload jsonb not null,
  base_version integer not null,
  affected_versions jsonb not null,
  state text not null default 'pending',
  source text not null default 'manual',
  ai_run_id uuid,
  author_membership_id uuid not null,
  reason text,
  applied_at timestamptz,
  applied_batch_id uuid,
  applied_by_membership_id uuid,
  discarded_at timestamptz,
  discarded_by_membership_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint authoring_change_pkey primary key (id),
  constraint authoring_change_org_id_key unique (org_id, id),
  constraint authoring_change_project_id_key unique (org_id, project_id, id),
  constraint authoring_change_project_fk foreign key (org_id, project_id)
    references public.project (org_id, id),
  constraint authoring_change_target_fk foreign key (org_id, project_id, target_id)
    references public.checklist_item (org_id, project_id, id),
  constraint authoring_change_author_fk foreign key (org_id, author_membership_id)
    references public.membership (org_id, id),
  constraint authoring_change_applied_by_fk foreign key (org_id, applied_by_membership_id)
    references public.membership (org_id, id),
  constraint authoring_change_discarded_by_fk foreign key (org_id, discarded_by_membership_id)
    references public.membership (org_id, id),
  constraint authoring_change_target_kind_check check (target_kind in ('checklist_item')),
  constraint authoring_change_type_check check (change_type in ('comment', 'delete', 'merge', 'split')),
  constraint authoring_change_state_check check (state in ('pending', 'applied', 'discarded')),
  constraint authoring_change_source_check check (source in ('manual', 'ai', 'import')),
  constraint authoring_change_ai_pairing_check check ((source = 'ai') = (ai_run_id is not null)),
  constraint authoring_change_base_version_check check (base_version >= 1),
  constraint authoring_change_affected_versions_check
    check (jsonb_typeof(affected_versions) = 'object' and affected_versions <> '{}'::jsonb
           and evidgate.jsonb_key_count(affected_versions) between 1 and 50
           and octet_length(affected_versions::text) <= 8192),
  constraint authoring_change_payload_size_check
    -- Measured on the stored `jsonb` text, which is what the column holds: it spells one space after every
    -- `:` and `,`, so it is longer than the minified request body. A route-level raw cap must therefore sit
    -- below this bound (a one-member payload already loses one byte to its colon), never equal it.
    check (jsonb_typeof(payload) = 'object' and octet_length(payload::text) <= 32768),
  constraint authoring_change_comment_body_check
    check (change_type <> 'comment'
           or (payload ? 'body' and char_length(payload ->> 'body') between 1 and 2000)),
  constraint authoring_change_reason_length_check check (reason is null or char_length(reason) <= 500),
  constraint authoring_change_applied_consistent_check
    check (state <> 'applied' or (applied_at is not null and applied_batch_id is not null
                                  and applied_by_membership_id is not null)),
  constraint authoring_change_discarded_consistent_check
    check (state <> 'discarded' or (discarded_at is not null and discarded_by_membership_id is not null))
);

-- Five indexes, the number the plan fixes. The two list shapes lead with the keyset order, never with a
-- filter: the default page has no `state`/`change_type`, and leading with either would make that page sort
-- the project's whole history. Those two stay residual predicates, so a filter needed no second copy of a page.
create index if not exists authoring_change_list_idx
  on public.authoring_change (org_id, project_id, created_at, id);
-- One item's change history and the comment panel read, in the same keyset order.
create index if not exists authoring_change_target_idx
  on public.authoring_change (org_id, project_id, target_id, created_at, id);
-- The 500-pending limit is one index-only count.
create index if not exists authoring_change_pending_idx
  on public.authoring_change (org_id, project_id) where state = 'pending';
-- Comments may repeat on one item; a structural proposal may not.
create unique index if not exists authoring_change_pending_structural_uq
  on public.authoring_change (org_id, target_id, change_type)
  where state = 'pending' and change_type in ('delete', 'merge', 'split');
-- The purge scan is tenant-free: `discarded_at` leads because the sweep orders by it across every
-- organization, so an `org_id` prefix would make the ordered batch a sort of the whole backlog.
create index if not exists authoring_change_discarded_idx
  on public.authoring_change (discarded_at)
  where state = 'discarded';

-- One row per statement, so `jsonb_object_keys` is evaluated once for each side of the overlap
-- test. The advisory lock is what makes the `exists` correct under READ COMMITTED: two concurrent
-- queue requests for the same project serialise here, and the second one sees the first's row.
create or replace function evidgate.authoring_change_affected_guard() returns trigger
  language plpgsql
  as $$
  declare
    new_keys text[];
    old_keys text[];
  begin
    if new.state = 'pending' and new.change_type in ('delete', 'merge', 'split') then
      perform pg_advisory_xact_lock(
        hashtextextended('authoring_change:' || new.org_id::text || ':' || new.project_id::text, 0));

      select coalesce(array_agg(k), '{}'::text[]) into new_keys
        from jsonb_object_keys(new.affected_versions) k;

      if exists (
           select 1
             from public.authoring_change c
            where c.org_id = new.org_id
              and c.project_id = new.project_id
              and c.id <> new.id
              and c.state = 'pending'
              and c.change_type in ('delete', 'merge', 'split')
              and (c.affected_versions ?| new_keys or new.affected_versions ?| (
                     select coalesce(array_agg(k), '{}'::text[])
                       from jsonb_object_keys(c.affected_versions) k))
         ) then
        raise exception 'authoring_change_affected: a pending structural change already covers one of these items'
          using errcode = '23514';
      end if;
    end if;
    return new;
  end
  $$;

revoke all on function evidgate.authoring_change_affected_guard() from public;

drop trigger if exists authoring_change_affected_guard on public.authoring_change;
create trigger authoring_change_affected_guard
  before insert or update on public.authoring_change
  for each row execute function evidgate.authoring_change_affected_guard();

-- A queued proposal is a promise about the past: its content and identity never change, and a
-- terminal row never moves again. Without this the app's table-wide UPDATE grant would let a
-- caller retarget a pending change after the apply already hashed it.
create or replace function evidgate.authoring_change_state_guard() returns trigger
  language plpgsql
  as $$
  begin
    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.target_kind is distinct from old.target_kind
       or new.target_id is distinct from old.target_id
       or new.change_type is distinct from old.change_type
       or new.payload is distinct from old.payload
       or new.base_version is distinct from old.base_version
       or new.affected_versions is distinct from old.affected_versions
       or new.source is distinct from old.source
       or new.ai_run_id is distinct from old.ai_run_id
       or new.author_membership_id is distinct from old.author_membership_id
       or new.reason is distinct from old.reason
       or new.created_at is distinct from old.created_at then
      raise exception 'authoring_change_state_invalid: a queued change is never rewritten'
        using errcode = '23514';
    end if;

    if old.state <> 'pending' then
      if new.state is distinct from old.state
         or new.applied_at is distinct from old.applied_at
         or new.applied_batch_id is distinct from old.applied_batch_id
         or new.applied_by_membership_id is distinct from old.applied_by_membership_id
         or new.discarded_at is distinct from old.discarded_at
         or new.discarded_by_membership_id is distinct from old.discarded_by_membership_id then
        raise exception 'authoring_change_state_invalid: % is terminal', old.state
          using errcode = '23514';
      end if;
      return new;
    end if;

    if new.state = 'pending' then
      if new.applied_at is not null or new.applied_batch_id is not null
         or new.applied_by_membership_id is not null
         or new.discarded_at is not null or new.discarded_by_membership_id is not null then
        raise exception 'authoring_change_state_invalid: a pending change carries no outcome stamps'
          using errcode = '23514';
      end if;
      return new;
    end if;

    if new.state = 'applied' then
      if new.discarded_at is not null or new.discarded_by_membership_id is not null then
        raise exception 'authoring_change_state_invalid: an applied change carries no discard stamps'
          using errcode = '23514';
      end if;
    else
      if new.applied_at is not null or new.applied_batch_id is not null
         or new.applied_by_membership_id is not null then
        raise exception 'authoring_change_state_invalid: a discarded change carries no apply stamps'
          using errcode = '23514';
      end if;
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.authoring_change_state_guard() from public;

drop trigger if exists authoring_change_state_guard on public.authoring_change;
create trigger authoring_change_state_guard
  before update on public.authoring_change
  for each row execute function evidgate.authoring_change_state_guard();

drop trigger if exists authoring_change_set_updated_at_and_version on public.authoring_change;
create trigger authoring_change_set_updated_at_and_version
  before update on public.authoring_change
  for each row execute function public.set_updated_at_and_version();

alter table public.authoring_change enable row level security;
alter table public.authoring_change force row level security;

drop policy if exists authoring_change_app_tenant on public.authoring_change;
create policy authoring_change_app_tenant on public.authoring_change
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- FORCE binds the owner too, so the retention sweep would delete zero rows without this policy.
drop policy if exists authoring_change_owner_backfill on public.authoring_change;
create policy authoring_change_owner_backfill on public.authoring_change
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- No `TO evidgate_operator` policy on purpose: the operator's read path is grant-gated by
-- `evidgate.operator_grant_ok`. With FORCE and no operator policy the SELECT grant reads zero rows
-- until a support grant opens the scope.
grant select, insert, update on public.authoring_change to evidgate_app;
revoke delete, truncate on public.authoring_change from evidgate_app;
grant select on public.authoring_change to evidgate_operator;
revoke all on public.authoring_change from evidgate_worker_sched;

-- Deletes only discarded proposals past the retention window, oldest first, in bounded batches:
-- the scheduled sweep passes the batch size and loops while a full batch comes back, so a backlog
-- never holds one long transaction. The function caps the batch itself; a non-positive limit
-- deletes nothing. Applied rows are review evidence and never match this predicate.
create or replace function evidgate.purge_expired_authoring_changes(p_limit integer) returns integer
  language sql
  security definer
  set search_path = ''
  as $$
    with batch as (
      select c.id
        from public.authoring_change c
       where c.state = 'discarded'
         and c.discarded_at < now() - interval '180 days'
       order by c.discarded_at
       limit least(greatest(p_limit, 0), 10000)
         for update skip locked
    ), removed as (
      delete from public.authoring_change c
       using batch b
       where c.id = b.id
      returning c.id
    )
    select count(*)::integer from removed
  $$;

-- The schema's default privileges hand new functions to evidgate_app, so the app role is named here
-- as well: the sweep is the one caller, and an app-held EXECUTE would let any tenant transaction
-- delete another tenant's discarded history.
revoke all on function evidgate.purge_expired_authoring_changes(integer) from public, evidgate_app;
grant execute on function evidgate.purge_expired_authoring_changes(integer) to evidgate_worker_sched;
