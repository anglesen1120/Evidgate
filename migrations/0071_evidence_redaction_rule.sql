-- 0071_evidence_redaction_rule.sql
--
-- WHY: a project's capture control has two halves, and only one of them had a home. `artifact_kind_policy` already
-- carries the per-kind switch that refuses a capture outright; the field names a reporter must strip *before* the bytes
-- leave the machine had nowhere to live. Without them the only defence is the server-side scan that runs after upload,
-- which is the second line, not the first.
--
-- SHAPE: one row per declared rule. `kind` is the same eight-value closed list `artifact_kind_policy` carries, so a rule
-- belongs to exactly one artifact kind; `rule_type` is the allowlist of four — `header`, `cookie`, `form_field`,
-- `query_param` — and `name_pattern` is the field name or a `*`/`?` glob over it. The pattern is applied by the reporter
-- and never interpreted here: the server validates the shape (bounded length, an allowlisted character class) and stores
-- it as data. A rule outside the four types cannot be stored at all (the CHECK), and the route refuses it earlier with
-- its own 422 code, so a client can act on the refusal. `unique (org_id, project_id, kind, rule_type, name_pattern)`
-- makes one declaration one row, so replacing a kind's declared set can never store a duplicate, and `unique (org_id, id)`
-- is the composite-FK target every tenant table declares. The composite FK to `project (org_id, id)` means a rule cannot
-- name another tenant's project even if a handler bug let it past, and an erased project takes its rules with it through
-- the owner's one erasure path.
--
-- The capture switch stays `artifact_kind_policy.capture_enabled`: this table adds rules and never a second enable flag.
--
-- INDEX: no index of its own. `redaction_rule_kind_key` starts `(org_id, project_id, kind, …)`, so it already serves both
-- reads the module makes — every rule of one `(project, kind)` and every rule of one project — and a separate
-- `(org_id, project_id)` index would duplicate that prefix.
--
-- GRANTS: `evidgate_app` reads, inserts and deletes (a `PUT` replaces the kind's declared set inside one transaction, so
-- delete is the operation, not an accident) and never updates: a rule is written or replaced, never edited in place.
-- `evidgate_operator` keeps SELECT only, with no operator policy, so that grant reads zero rows until the grant-gated
-- operator form lands. `evidgate_worker_sched` holds nothing: no scheduled job touches configuration.
--
-- ROW SECURITY: ENABLE + FORCE with the tenant policy for `evidgate_app` and the owner backfill policy — the same shape
-- `artifact_kind_policy` uses. `redaction_rule` carries `org_id`, so it is not an entry in `contracts/rls-exceptions.txt`.
--
-- LOCKS: `set local lock_timeout` bounds the DDL's wait, so a busy database fails the migration for a retry instead of
-- stalling writes. Everything here is a catalogue write on a new relation.
--
-- ORDER: additive — a new relation nothing referenced before — so apply it before the route that writes rules. No down
-- migration: dropping the table would delete every declared redaction rule, which no rollback restores.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`, `drop policy if exists`
-- before every policy, and the repeatable `revoke`/`grant`.

set local lock_timeout = '5s';

create table if not exists public.redaction_rule (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  kind text not null,
  rule_type text not null,
  name_pattern text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint redaction_rule_pkey primary key (id),
  constraint redaction_rule_org_id_key unique (org_id, id),
  constraint redaction_rule_kind_key unique (org_id, project_id, kind, rule_type, name_pattern),
  constraint redaction_rule_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint redaction_rule_kind_check check (kind in ('trace', 'video', 'screenshot', 'raw_report', 'dom_snapshot', 'a11y_snapshot', 'patch', 'ui_hierarchy')),
  constraint redaction_rule_rule_type_check check (rule_type in ('header', 'cookie', 'form_field', 'query_param')),
  -- The one shape rule the database owns: a declared name is 1..200 characters of `[A-Za-z0-9._~%+:*?/-]`, so a writer
  -- that skips the route cannot store a control character, a newline or an unbounded string. `*` and `?` are the only
  -- metacharacters — the reporter's glob, never a server-side pattern engine.
  constraint redaction_rule_name_pattern_check check (char_length(name_pattern) between 1 and 200
                                                     and name_pattern ~ '^[A-Za-z0-9._~%+:*?/-]+$'),
  constraint redaction_rule_version_check check (version >= 1)
);

drop trigger if exists redaction_rule_set_updated_at_and_version on public.redaction_rule;
create trigger redaction_rule_set_updated_at_and_version
  before update on public.redaction_rule
  for each row execute function public.set_updated_at_and_version();

alter table public.redaction_rule enable row level security;
alter table public.redaction_rule force row level security;

drop policy if exists redaction_rule_app_tenant on public.redaction_rule;
create policy redaction_rule_app_tenant on public.redaction_rule
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists redaction_rule_owner_backfill on public.redaction_rule;
create policy redaction_rule_owner_backfill on public.redaction_rule
  for all to evidgate_owner using (true) with check (true);

revoke update on public.redaction_rule from evidgate_app;
grant select, insert, delete on public.redaction_rule to evidgate_app;
grant select on public.redaction_rule to evidgate_operator;
revoke all on public.redaction_rule from evidgate_worker_sched;
