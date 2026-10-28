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

create index security_event_list_idx
  on security_event (org_id, project_id, type, severity, created_at desc, id);
