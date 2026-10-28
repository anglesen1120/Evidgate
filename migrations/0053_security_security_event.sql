-- 0053_security_security_event.sql
--
-- Owner: 11.5.1-T5.1 (feature plan 11.5.1 §4). `security_event` is the tenant-visible record of
-- security-relevant events (who probed, who failed to authenticate, who used a kill switch, who
-- accessed a tenant as support). Emitter modules insert rows against this shape; the tenant reads
-- them through `GET /v1/orgs/{o}/security-events` (`security_event:read`, 11.5.1-T5.2).
--
-- Closed V1 `type` set (a docs decision, owned by `docs/security/security-event-baseline.md`; a new
-- type belongs to 11.1.1-T4.1, so there is deliberately NO check constraint and NO database enum):
--   auth_failure | cross_tenant_probe | secret_scanner_alert | kill_switch_used | support_access
-- V2 adds `injection_attempt` (11.4.1-T2).
-- `severity` set: info | warning | critical.
-- `details` carries ids, hashes and counts only: never a raw payload, a secret or a reason text.
-- `actor` is `{kind, id}`; no secrets.
--
-- RLS (DECISIONS F5, rulings T2/T3): ENABLE + FORCE, one policy over `evidgate.current_org_id()`,
-- `unique (org_id, id)` as the target of any later composite tenant FK.
--
-- Indexes (00 §5.7 P3/P4): every page orders by `created_at desc, id desc`. The unfiltered read and the
-- `from`/`to`-window read filter on `org_id` alone, so `security_event_org_created_idx
-- (org_id, created_at desc, id desc)` answers them index-ordered. A read that binds `project_id`, `type`
-- and `severity` is narrowed by `security_event_list_idx (org_id, project_id, type, severity, created_at
-- desc, id desc)`: the equalities come first, the cursor tuple follows, so an index scan returns the page
-- already ordered. Two planner choices still sort, legitimately: a bitmap scan (cheaper for a small
-- selective page, and a bitmap keeps no order), and a partial bind on the three filters (an unbound column
-- precedes `created_at` in the key).
--
-- Privileges: append-only for the application role. `0001`'s default privileges hand `evidgate_app`
-- select/insert/update/delete on every table `evidgate_owner` creates; this table is a record of what
-- already happened, not a mutable aggregate — emitters only insert, nothing rewrites or removes a row
-- (no retention sweep yet) — so the revoke at the end of this file removes the three statements that
-- could rewrite history (0015/0031/0049 precedent). select/insert survive; `evidgate_operator` keeps
-- its default `select`.

create table security_event (
  id          uuid primary key,
  org_id      uuid not null,
  project_id  uuid,
  type        text not null,
  severity    text not null,
  actor       jsonb not null,
  details     jsonb not null default '{}',
  request_id  text,
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now(),
  version     int not null default 1,
  unique (org_id, id)
);

alter table security_event enable row level security;
alter table security_event force row level security;

create policy tenant_isolation on security_event
  using (org_id = evidgate.current_org_id());

-- The unfiltered page and the `from`/`to` window: one keyset on (created_at, id) descending under one org.
create index security_event_org_created_idx
  on security_event (org_id, created_at desc, id desc);

-- The filter-led page: a read binding all three filter columns is narrowed here, the equalities first and the
-- cursor order (created_at, id) descending after them, so an index scan returns the page already ordered. A
-- bitmap scan (cheap for a small selective page, order not preserved) and a partial bind (an unbound column
-- precedes created_at in the key) both still sort. The plan test explains the ordered path.
create index security_event_list_idx
  on security_event (org_id, project_id, type, severity, created_at desc, id desc);

-- Append-only in SQL (0015:124, 0031, 0049 precedent): rows are inserted by emitters and never rewritten
-- or removed by the application role, so the read side cannot lose or rewrite an event after the fact.
-- `select` and `insert` stay from 0001's defaults.
revoke update, delete, truncate on public.security_event from evidgate_app;
