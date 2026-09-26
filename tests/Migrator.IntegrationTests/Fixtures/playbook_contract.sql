-- 01.4.1-T4.2 / AC7 fixture — step 2 of 2 (contract).
--
-- CONTRACT: only the new column survives. The backfill is repeated because rows
-- written by the old code after the expand may still have a NULL `title`; it must
-- run before NOT NULL is enforced. Dropping `label` is the cutover — from here on
-- the old code path cannot work, which is why this file ships in a later release
-- than the expand file (docs/migrations-expand-contract.md).
update public.rls_probe
   set title = label
 where title is null;

alter table public.rls_probe alter column title set not null;

alter table public.rls_probe drop column label;
