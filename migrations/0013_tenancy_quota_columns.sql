-- 0013_tenancy_quota_columns.sql
--
-- Owner: 02.1.1-T3.1. The tenant limit columns of feature plan 02.1.1 §4 that plan 12
-- reads through `ITenantLimits` (Tenancy.Contracts):
--
--   organization.max_projects       projects an org may hold that are not `deleted`
--   organization.max_seats          active memberships an org may hold (enforced by 12)
--   project.retention_days          overrides organization.retention_default_days
--   project.monthly_run_quota       runs per calendar month (enforced by 12)
--
-- NULL means "no limit set" (the default; plan 12's plan catalogue fills them). Zero
-- is a legal limit for the quota columns (an org that may create nothing more), never
-- for a retention period. Additive and nullable: the running version neither reads
-- nor writes them, so expand-only (docs/migrations-expand-contract.md).

alter table public.organization
  add column if not exists max_projects integer,
  add column if not exists max_seats integer;

alter table public.organization
  add constraint organization_max_projects_check check (max_projects is null or max_projects >= 0),
  add constraint organization_max_seats_check check (max_seats is null or max_seats >= 0);

alter table public.project
  add column if not exists retention_days integer,
  add column if not exists monthly_run_quota integer;

alter table public.project
  add constraint project_retention_days_check check (retention_days is null or retention_days > 0),
  add constraint project_monthly_run_quota_check check (monthly_run_quota is null or monthly_run_quota >= 0);
