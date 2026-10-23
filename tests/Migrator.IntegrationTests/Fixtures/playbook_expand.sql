-- 01.4.1-T4.2 / AC7 fixture — step 1 of 2 (expand).
--
-- The test copies this file into a migrations directory under the next free
-- number, after the repository's real migrations, so it runs through the
-- Migrator exactly like a production change (tests/Migrator.IntegrationTests).
--
-- EXPAND: add the new column as NULLABLE and backfill existing rows. Nullable is
-- what makes the change zero-downtime: the deployed (old) code keeps writing
-- `label` only, so its INSERT keeps succeeding while the new code reads and
-- dual-writes `title`. Both paths work in this window — that is the invariant
-- the playbook test asserts before the contract file is applied.
alter table public.rls_probe add column if not exists title text;

update public.rls_probe
   set title = label
 where title is null;
