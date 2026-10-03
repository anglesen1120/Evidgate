-- 0012_tenancy_slug_grammar.sql
--
-- Owner: 02.1.1-T2.1. Slug grammar of DECISIONS §8.1 D-A1 (owner, 2026-10-02):
--
--   ^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$
--
-- 2–63 characters, lowercase ASCII letters, digits and hyphens, starting and ending
-- with a letter or digit. The 0004/0005 checks (`^[a-z0-9][a-z0-9-]{1,62}$`) accepted
-- a trailing hyphen (`ab-`); 0004/0005 are checksum-locked, so the replacement is
-- this file. The command validators of 02.1.1-T2 use the same expression, and the
-- check here stays the authority for any writer that bypasses them.
--
-- Both constraints are dropped and re-added in this one transaction; `add constraint`
-- validates every existing row, so a stored slug that ends in a hyphen stops the
-- migration (no such row exists before 02.1.1-T2 ships the write path).

alter table public.organization
  drop constraint if exists organization_slug_format_check;

alter table public.organization
  add constraint organization_slug_format_check
  check (slug ~ '^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$');

alter table public.project
  drop constraint if exists project_slug_format_check;

alter table public.project
  add constraint project_slug_format_check
  check (slug ~ '^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$');
