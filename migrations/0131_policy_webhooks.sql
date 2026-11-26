-- 0131_policy_webhooks.sql
-- Why: outbound webhooks. A project registers HTTPS endpoints; every evaluation fans out one signed
--   `evaluation.created` delivery per active endpoint. The tables hold the endpoint (with its secret
--   encrypted by the application, never readable as plaintext here) and the per-endpoint delivery
--   state that the retry, dead-letter and replay paths move.
-- What: public.webhook_endpoint, public.webhook_outbound_delivery (the inbound CI receiver of 0106
--   keeps public.webhook_delivery), their guards, FORCE RLS policies, minimal grants and the
--   own-org replay function evidgate.replay_webhook_delivery.
-- Refs: plans/00-overview.md section 9.5; 0059 (evaluation), 0030 (guard and grant style),
--   0104 (own-org definer with in-transaction audit).
-- Order: after 0130; the migrator refuses a gap, so 0129 and 0130 must land first.
set local lock_timeout = '5s';

create table if not exists public.webhook_endpoint (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  url text not null,
  secret_ciphertext bytea not null,
  secret_hint text not null,
  secret_version integer not null default 1,
  event_types text[] not null default array['evaluation.created']::text[],
  state text not null default 'active',
  created_by uuid not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  deleted_at timestamptz,
  version integer not null default 1,
  constraint webhook_endpoint_pkey primary key (id),
  constraint webhook_endpoint_org_id_key unique (org_id, id),
  constraint webhook_endpoint_org_project_id_key unique (org_id, project_id, id),
  constraint webhook_endpoint_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  -- Same shape as api_token.created_by: the creator is a member of this org, never another tenant's.
  constraint webhook_endpoint_created_by_fk foreign key (org_id, created_by) references public.membership (org_id, id),
  constraint webhook_endpoint_url_check check (char_length(url) between 9 and 2048 and url like 'https://%'),
  constraint webhook_endpoint_secret_ciphertext_check check (octet_length(secret_ciphertext) between 1 and 1024),
  constraint webhook_endpoint_secret_hint_check check (char_length(secret_hint) = 4),
  constraint webhook_endpoint_secret_version_check check (secret_version >= 1),
  constraint webhook_endpoint_event_types_check check (
    case
      when array_ndims(event_types) is distinct from 1 then false
      else cardinality(event_types) between 1 and 8
           and array_position(event_types, null) is null
           and event_types <@ array['evaluation.created']::text[]
    end),
  constraint webhook_endpoint_state_check check (state in ('active', 'disabled', 'deleted')),
  constraint webhook_endpoint_deleted_at_check check ((state = 'deleted') = (deleted_at is not null)),
  constraint webhook_endpoint_version_check check (version >= 1)
);

-- A deleted endpoint keeps its row for delivery history, so its URL must be registrable again.
create unique index if not exists webhook_endpoint_live_url_key
  on public.webhook_endpoint (org_id, project_id, url)
  where state <> 'deleted';

create index if not exists webhook_endpoint_project_state_idx
  on public.webhook_endpoint (org_id, project_id, state, created_at, id);

create table if not exists public.webhook_outbound_delivery (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  endpoint_id uuid not null,
  event_type text not null,
  event_id uuid not null,
  state text not null default 'queued',
  attempts integer not null default 0,
  next_attempt_at timestamptz not null default now(),
  last_error text,
  last_status_code integer,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint webhook_outbound_delivery_pkey primary key (id),
  constraint webhook_outbound_delivery_org_id_key unique (org_id, id),
  -- One row per endpoint and event: a repeated fan-out is a conflict, never a second delivery.
  constraint webhook_outbound_delivery_event_key unique (org_id, endpoint_id, event_id),
  constraint webhook_outbound_delivery_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint webhook_outbound_delivery_endpoint_fk foreign key (org_id, project_id, endpoint_id)
    references public.webhook_endpoint (org_id, project_id, id),
  -- event_id is the evaluation id; the row cannot outlive that evaluation and has no expiry of its own.
  constraint webhook_outbound_delivery_evaluation_fk foreign key (org_id, event_id)
    references public.evaluation (org_id, id),
  constraint webhook_outbound_delivery_event_type_check check (event_type in ('evaluation.created')),
  constraint webhook_outbound_delivery_state_check
    check (state in ('queued', 'sending', 'delivered', 'retrying', 'dead_letter')),
  constraint webhook_outbound_delivery_attempts_check check (attempts >= 0),
  constraint webhook_outbound_delivery_last_error_check check (last_error is null or char_length(last_error) between 1 and 64),
  constraint webhook_outbound_delivery_status_code_check
    check (last_status_code is null or last_status_code between 100 and 599),
  constraint webhook_outbound_delivery_version_check check (version >= 1)
);

create index if not exists webhook_outbound_delivery_project_state_idx
  on public.webhook_outbound_delivery (org_id, project_id, state, created_at, id);

create index if not exists webhook_outbound_delivery_project_idx
  on public.webhook_outbound_delivery (org_id, project_id, created_at, id);

create index if not exists webhook_outbound_delivery_event_idx
  on public.webhook_outbound_delivery (org_id, event_id, state);

create index if not exists webhook_outbound_delivery_endpoint_idx
  on public.webhook_outbound_delivery (org_id, endpoint_id, created_at, id);

drop trigger if exists webhook_endpoint_set_updated_at_and_version on public.webhook_endpoint;
create trigger webhook_endpoint_set_updated_at_and_version
  before update on public.webhook_endpoint
  for each row execute function public.set_updated_at_and_version();

drop trigger if exists webhook_outbound_delivery_set_updated_at_and_version on public.webhook_outbound_delivery;
create trigger webhook_outbound_delivery_set_updated_at_and_version
  before update on public.webhook_outbound_delivery
  for each row execute function public.set_updated_at_and_version();

create or replace function evidgate.webhook_endpoint_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'webhook_endpoint_immutable: an endpoint is soft-deleted, never removed'
        using errcode = '23514';
    end if;

    if old.state = 'deleted' then
      raise exception 'webhook_endpoint_state_invalid: a deleted endpoint never changes'
        using errcode = '23514';
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.created_by is distinct from old.created_by
       or new.created_at is distinct from old.created_at then
      raise exception 'webhook_endpoint_immutable: endpoint identity and creator never change (UPDATE)'
        using errcode = '23514';
    end if;

    if new.state is distinct from old.state
       and not (   (old.state = 'active' and new.state in ('disabled', 'deleted'))
                or (old.state = 'disabled' and new.state in ('active', 'deleted'))) then
      raise exception 'webhook_endpoint_state_invalid: % -> %', old.state, new.state
        using errcode = '23514';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.webhook_endpoint_guard() from public;

drop trigger if exists webhook_endpoint_guard on public.webhook_endpoint;
create trigger webhook_endpoint_guard
  before update or delete on public.webhook_endpoint
  for each row execute function evidgate.webhook_endpoint_guard();

-- dead_letter -> queued is the replay move: only the owner-run replay function makes it, so the
-- application role cannot resurrect an exhausted delivery by a plain UPDATE.
create or replace function evidgate.webhook_outbound_delivery_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'webhook_outbound_delivery_immutable: a delivery is never removed'
        using errcode = '23514';
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.endpoint_id is distinct from old.endpoint_id
       or new.event_type is distinct from old.event_type
       or new.event_id is distinct from old.event_id
       or new.created_at is distinct from old.created_at then
      raise exception 'webhook_outbound_delivery_immutable: delivery identity never changes (UPDATE)'
        using errcode = '23514';
    end if;

    if old.state = 'delivered' then
      raise exception 'webhook_outbound_delivery_state_invalid: a delivered row never changes'
        using errcode = '23514';
    end if;

    if new.state is distinct from old.state or old.state = 'dead_letter' then
      if not (   (old.state = 'queued' and new.state = 'sending')
              or (old.state = 'sending' and new.state in ('delivered', 'retrying', 'dead_letter'))
              or (old.state = 'retrying' and new.state in ('sending', 'dead_letter'))
              or (old.state = 'dead_letter' and new.state = 'queued')) then
        raise exception 'webhook_outbound_delivery_state_invalid: % -> %', old.state, new.state
          using errcode = '23514';
      end if;

      if old.state = 'dead_letter' and current_user = 'evidgate_app' then
        raise exception 'webhook_outbound_delivery_state_invalid: dead_letter is left only by evidgate.replay_webhook_delivery'
          using errcode = '23514';
      end if;
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.webhook_outbound_delivery_guard() from public;

drop trigger if exists webhook_outbound_delivery_guard on public.webhook_outbound_delivery;
create trigger webhook_outbound_delivery_guard
  before update or delete on public.webhook_outbound_delivery
  for each row execute function evidgate.webhook_outbound_delivery_guard();

alter table public.webhook_endpoint enable row level security;
alter table public.webhook_endpoint force row level security;

drop policy if exists webhook_endpoint_app_tenant on public.webhook_endpoint;
create policy webhook_endpoint_app_tenant on public.webhook_endpoint
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists webhook_endpoint_owner_backfill on public.webhook_endpoint;
create policy webhook_endpoint_owner_backfill on public.webhook_endpoint
  for all to evidgate_owner using (true) with check (true);

alter table public.webhook_outbound_delivery enable row level security;
alter table public.webhook_outbound_delivery force row level security;

drop policy if exists webhook_outbound_delivery_app_tenant on public.webhook_outbound_delivery;
create policy webhook_outbound_delivery_app_tenant on public.webhook_outbound_delivery
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists webhook_outbound_delivery_owner_backfill on public.webhook_outbound_delivery;
create policy webhook_outbound_delivery_owner_backfill on public.webhook_outbound_delivery
  for all to evidgate_owner using (true) with check (true);

-- The endpoint row carries the encrypted secret, so no standing operator read exists; the delivery
-- table follows. Support reads go through a support grant, never a role default.
revoke all on public.webhook_endpoint from evidgate_app, evidgate_operator, evidgate_worker_sched;
grant select on public.webhook_endpoint to evidgate_app;
grant insert (id, org_id, project_id, url, secret_ciphertext, secret_hint, event_types, created_by)
  on public.webhook_endpoint to evidgate_app;
grant update (url, event_types, state, deleted_at) on public.webhook_endpoint to evidgate_app;

revoke all on public.webhook_outbound_delivery from evidgate_app, evidgate_operator, evidgate_worker_sched;
grant select on public.webhook_outbound_delivery to evidgate_app;
grant insert (id, org_id, project_id, endpoint_id, event_type, event_id, next_attempt_at)
  on public.webhook_outbound_delivery to evidgate_app;
grant update (state, attempts, next_attempt_at, last_error, last_status_code)
  on public.webhook_outbound_delivery to evidgate_app;

-- p_actor is the operator's identity as it appears in audit_log.actor_id (the operator is not a member of
-- the tenant, so it is not a membership id and carries no FK); the caller passes its authenticated id.
-- Operator replay of every dead-lettered delivery of one event in the caller's own org. The state
-- guard above refuses that move to evidgate_app, so this owner-run function is the only path. It is
-- tenant-scoped like replay_dead_letter_task (0104): an unset tenant is refused, and the audit row
-- commits in the same transaction as the state change.
create or replace function evidgate.replay_webhook_delivery(p_event_id uuid, p_actor uuid)
  returns table (delivery_id uuid)
  language plpgsql
  security definer
  set search_path = ''
  as $$
  declare
    v_org uuid := evidgate.current_org_id();
    v_row record;
  begin
    if v_org is null then
      raise exception 'replay_webhook_delivery: no tenant in scope'
        using errcode = '42501';
    end if;

    set local lock_timeout = '5s';

    for v_row in
      select d.id, d.endpoint_id, d.attempts
        from public.webhook_outbound_delivery d
        join public.webhook_endpoint e
          on e.org_id = d.org_id and e.project_id = d.project_id and e.id = d.endpoint_id
       where d.org_id = v_org
         and d.event_id = p_event_id
         and d.state = 'dead_letter'
         and e.state = 'active'
       order by d.id
         for update of d
    loop
      update public.webhook_outbound_delivery
         set state = 'queued', next_attempt_at = now(), last_error = null, last_status_code = null
       where org_id = v_org and id = v_row.id;

      perform evidgate.audit_append_user_event(
        'webhook_delivery.replay', v_org, p_actor, 'webhook_delivery', v_row.id::text,
        pg_catalog.jsonb_build_object(
          'delivery_id', v_row.id::text,
          'endpoint_id', v_row.endpoint_id::text,
          'event_id', p_event_id::text,
          'attempts', v_row.attempts::text),
        null, null);

      delivery_id := v_row.id;
      return next;
    end loop;
  end
  $$;

revoke all on function evidgate.replay_webhook_delivery(uuid, uuid) from public;
grant execute on function evidgate.replay_webhook_delivery(uuid, uuid) to evidgate_app;
