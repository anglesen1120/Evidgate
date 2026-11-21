-- 0120_catalog_template_taxonomy.sql
--
-- WHY: a template has to say what kind of project it generates (`platform`), how wide the generated suites run
-- (`level`) and why they exist (`purpose`), so `evidgate init --template api/...` and the draft renderer can pick a
-- release-gate template without reading its files. The lists are the closed catalogue the suite taxonomy already uses
-- (0068, 03.1.4 AC3); a fourth platform or a renamed purpose is a contract change and a migration, never a new string.
--
-- SHAPE: three `text not null` columns on `public.template`, each with a default and a named CHECK over the same
-- catalogue: `platform` (web, api, unit, mobile), `level` (unit, integration, e2e, smoke) and `purpose`
-- (regression, smoke, release_gate, exploratory). The defaults are the suite behaviour the product has today: a
-- browser project at `e2e` for `regression`. A template is content-addressed (`body_ref` names the packaged files),
-- so it carries no stored revision hash to bump and no immutable revision row: changing the taxonomy of a template
-- is a new `version` row, exactly as changing its body is (AC7). No hash input exists for templates.
--
-- BACKFILL: 0119's built-in row is set explicitly to `web`/`e2e`/`regression`, the values it was authored under. The
-- owner role's `template_owner_backfill` policy allows the update (`for all ... using (true)`); `evidgate_app` keeps
-- SELECT only, its grants are unchanged, and it can never rewrite a template row.
--
-- INDEX: `template_taxonomy_idx` serves the list's `(platform, level, purpose)` equality filters. Neither existing
-- index carries those columns (`template_kind_active` is `(kind, active)`, `template_created_id_idx` is
-- `(created_at, id)`), so neither can narrow a taxonomy-filtered page.
--
-- LOCKS: one DDL pass over an existing catalogue table; `lock_timeout` bounds its wait. Deploy before a client sends
-- the filters or reads the new members. Rollback: drop the three constraints and columns; no row loses data the
-- catalogue cannot re-derive from the version it lands on.
--
-- Idempotent in the shape the README's file contract allows: `add column if not exists`, `drop constraint if exists`
-- before every `add constraint`, `create index if not exists`, and a backfill of one fixed id.

set local lock_timeout = '5s';

alter table public.template
  add column if not exists platform text not null default 'web';

alter table public.template
  add column if not exists level text not null default 'e2e';

alter table public.template
  add column if not exists purpose text not null default 'regression';

alter table public.template
  drop constraint if exists template_platform_check;
alter table public.template
  add constraint template_platform_check check (platform in ('web', 'api', 'unit', 'mobile'));

alter table public.template
  drop constraint if exists template_level_check;
alter table public.template
  add constraint template_level_check check (level in ('unit', 'integration', 'e2e', 'smoke'));

alter table public.template
  drop constraint if exists template_purpose_check;
alter table public.template
  add constraint template_purpose_check check (purpose in ('regression', 'smoke', 'release_gate', 'exploratory'));

create index if not exists template_taxonomy_idx on public.template (platform, level, purpose);

update public.template
   set platform = 'web',
       level = 'e2e',
       purpose = 'regression'
 where id = '018f8f5e-0000-7000-8000-000000000119'::uuid;
